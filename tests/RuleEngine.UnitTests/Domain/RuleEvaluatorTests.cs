using System.Text.Json;
using System.Text.Json.Nodes;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Common;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;
using static RuleEngine.UnitTests.TestData;

namespace RuleEngine.UnitTests.Domain;

public class RuleEvaluatorTests
{
    private readonly RuleEvaluator _evaluator = new();

    private static readonly JsonObject LargeItOrder = Facts("""
        { "document": { "quantity": 25, "amount": 12000, "department": "IT", "tags": ["urgent"] }, "supplier": { "riskRating": "High" } }
        """);

    [Fact]
    public void AllMatches_returns_every_matching_rule_in_priority_order()
    {
        var rules = new[]
        {
            Rule("tag-it", 20, Leaf("document.department", ConditionOperator.Equals, "IT"), RuleActionDefinition.AddTag("it")),
            Rule("approve-large", 10, Leaf("document.quantity", ConditionOperator.GreaterThan, 10), RuleActionDefinition.RequireApproval("Manager")),
            Rule("never", 5, Leaf("document.quantity", ConditionOperator.LessThan, 0), RuleActionDefinition.Reject("negative")),
        };

        var result = _evaluator.Evaluate(LargeItOrder, rules, EvaluationStrategy.AllMatches, PurchaseSchema());

        result.Rules.Select(r => r.RuleName).Should().Equal("never", "approve-large", "tag-it");
        result.MatchedRules.Select(r => r.RuleName).Should().Equal("approve-large", "tag-it");
        result.Actions.Select(a => a.Sequence).Should().Equal(1, 2);
        result.Actions.Select(a => a.Action.Type).Should().Equal("RequireApproval", "AddTag");
    }

    [Fact]
    public void FirstMatch_stops_after_the_first_matching_rule()
    {
        var rules = new[]
        {
            Rule("first", 1, Leaf("document.quantity", ConditionOperator.GreaterThan, 10), RuleActionDefinition.AddTag("first")),
            Rule("second", 2, Leaf("document.quantity", ConditionOperator.GreaterThan, 5), RuleActionDefinition.AddTag("second")),
        };

        var result = _evaluator.Evaluate(LargeItOrder, rules, EvaluationStrategy.FirstMatch, PurchaseSchema());

        result.MatchedRules.Should().ContainSingle().Which.RuleName.Should().Be("first");
        result.Rules.Single(r => r.RuleName == "second").Evaluated.Should().BeFalse();
        result.Actions.Should().ContainSingle();
    }

    [Fact]
    public void Equal_priorities_are_ordered_by_name_for_determinism()
    {
        var rules = new[]
        {
            Rule("b-rule", 1, Leaf("document.quantity", ConditionOperator.GreaterThan, 1), RuleActionDefinition.AddTag("b")),
            Rule("a-rule", 1, Leaf("document.quantity", ConditionOperator.GreaterThan, 1), RuleActionDefinition.AddTag("a")),
        };

        var result = _evaluator.Evaluate(LargeItOrder, rules, EvaluationStrategy.FirstMatch);

        result.MatchedRules.Single().RuleName.Should().Be("a-rule");
    }

    [Fact]
    public void Composite_nodes_combine_children()
    {
        var condition = ConditionNode.AllOf(
            Leaf("document.quantity", ConditionOperator.Between, new JsonArray(10, 50)),
            ConditionNode.AnyOf(
                Leaf("document.department", ConditionOperator.Equals, "HR"),
                Leaf("supplier.riskRating", ConditionOperator.In, new JsonArray("High", "Critical"))),
            ConditionNode.Negate(Leaf("document.tags", ConditionOperator.Contains, "blocked")));

        var trace = _evaluator.EvaluateCondition(condition, LargeItOrder, PurchaseSchema());

        trace.Result.Should().BeTrue();
        trace.Kind.Should().Be("all");
        trace.Children![1].Kind.Should().Be("any");
        trace.Children[1].Children!.Select(c => c.Result).Should().Equal(false, true);
        trace.Children[2].Kind.Should().Be("not");
    }

    [Fact]
    public void Empty_all_group_never_matches()
    {
        var trace = _evaluator.EvaluateCondition(new ConditionNode { All = new List<ConditionNode>() }, LargeItOrder);

        trace.Result.Should().BeFalse();
        trace.Note.Should().Contain("Empty");
    }

    [Fact]
    public void Leaf_trace_records_expected_and_actual_values()
    {
        var trace = _evaluator.EvaluateCondition(Leaf("document.quantity", ConditionOperator.GreaterThan, 10), LargeItOrder, PurchaseSchema());

        trace.Field.Should().Be("document.quantity");
        trace.Operator.Should().Be("GreaterThan");
        trace.Expected.Should().Be("10");
        trace.Actual.Should().Be("25");
        trace.Result.Should().BeTrue();
    }

    [Fact]
    public void Missing_fact_is_reported_in_trace()
    {
        var trace = _evaluator.EvaluateCondition(Leaf("document.documentType", ConditionOperator.Equals, "Invoice"), LargeItOrder, PurchaseSchema());

        trace.Result.Should().BeFalse();
        trace.Actual.Should().BeNull();
        trace.Note.Should().Contain("not present");
    }

    [Fact]
    public void Rules_exceeding_limits_are_not_evaluated()
    {
        var evaluator = new RuleEvaluator(new EvaluationOptions { MaxDepth = 2, MaxNodes = 10 });
        var deep = ConditionNode.Negate(ConditionNode.Negate(Leaf("document.quantity", ConditionOperator.GreaterThan, 1)));

        var result = evaluator.Evaluate(LargeItOrder, new[] { Rule("deep", 1, deep, RuleActionDefinition.AddTag("x")) }, EvaluationStrategy.AllMatches);

        result.Rules.Single().Matched.Should().BeFalse();
        result.Rules.Single().Error.Should().Contain("exceeds limits");
        result.Actions.Should().BeEmpty();
    }

    [Fact]
    public void SetValue_chains_into_later_rules_without_mutating_input()
    {
        var rules = new[]
        {
            Rule("set-priority", 1, Leaf("document.quantity", ConditionOperator.GreaterThan, 10), RuleActionDefinition.SetValue("routing.priority", "high")),
            Rule("route-high", 2, Leaf("routing.priority", ConditionOperator.Equals, "high"), RuleActionDefinition.RequireApproval("Director")),
        };

        var result = _evaluator.Evaluate(LargeItOrder, rules, EvaluationStrategy.AllMatches, PurchaseSchema());

        result.MatchedRules.Should().HaveCount(2);
        result.OutputFacts["routing"]!["priority"]!.GetValue<string>().Should().Be("high");
        LargeItOrder.ContainsKey("routing").Should().BeFalse();
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        var rules = new[]
        {
            Rule("a", 1, Leaf("document.quantity", ConditionOperator.GreaterThan, 10), RuleActionDefinition.AddTag("a")),
            Rule("b", 1, Leaf("document.amount", ConditionOperator.LessThan, 100), RuleActionDefinition.AddTag("b")),
        };

        string Run() => JsonSerializer.Serialize(_evaluator.Evaluate(LargeItOrder, rules, EvaluationStrategy.AllMatches, PurchaseSchema()).Rules, DomainJson.Options);

        Run().Should().Be(Run());
    }

    [Fact]
    public void Unknown_operator_in_stored_tree_is_reported_not_thrown()
    {
        var node = new ConditionNode { Field = "document.quantity", Operator = "Bigger", Value = 1 };

        var trace = _evaluator.EvaluateCondition(node, LargeItOrder);

        trace.Result.Should().BeFalse();
        trace.Note.Should().Contain("Unknown operator");
    }
}
