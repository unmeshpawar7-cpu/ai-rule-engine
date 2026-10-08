using RuleEngine.Application.Generation;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Enums;

namespace RuleEngine.UnitTests.Application;

public class PatternRuleGeneratorTests
{
    private readonly PatternRuleGenerator _generator = new();

    private RuleGenerationResult Generate(string text) =>
        _generator.Generate(new RuleGenerationRequest(text, TestData.PurchaseSchema(), "purchase-approval"));

    [Fact]
    public void Canonical_example_produces_manager_approval_for_large_quantities()
    {
        var result = Generate("Documents with quantity greater than 10 should require manager approval.");

        result.Succeeded.Should().BeTrue();
        result.Generator.Should().Be(PatternRuleGenerator.GeneratorName);
        result.PromptVersion.Should().Be(PatternRuleGenerator.GrammarVersion);
        var proposal = result.Proposal!;
        proposal.Condition.Kind.Should().Be(ConditionNodeKind.Leaf);
        proposal.Condition.Field.Should().Be("document.quantity");
        proposal.Condition.Operator.Should().Be("GreaterThan");
        proposal.Condition.Value!.GetValue<decimal>().Should().Be(10m);
        proposal.Actions.Should().ContainSingle();
        proposal.Actions[0].ActionType.Should().Be(RuleActionType.RequireApproval);
        proposal.Actions[0].GetString("role").Should().Be("Manager");
        proposal.Confidence.Should().BeGreaterThanOrEqualTo(0.9);
        proposal.Explanation.Should().Contain("document.quantity > 10");
        proposal.Assumptions.Should().Contain(a => a.Contains("Documents"));
    }

    [Fact]
    public void If_then_with_conjunction_and_multiple_actions()
    {
        var result = Generate("If amount is at least 50000 and department is IT, require director approval and tag as high-value");

        result.Succeeded.Should().BeTrue();
        var condition = result.Proposal!.Condition;
        condition.Kind.Should().Be(ConditionNodeKind.All);
        condition.All!.Select(c => (c.Field, c.Operator)).Should().Equal(
            ("document.amount", "GreaterThanOrEqual"),
            ("document.department", "Equals"));
        condition.All![1].IgnoreCase.Should().BeTrue();
        result.Proposal.Actions.Select(a => a.Type).Should().Equal("RequireApproval", "AddTag");
        result.Proposal.Actions[0].GetString("role").Should().Be("Director");
        result.Proposal.Actions[1].GetString("tag").Should().Be("high-value");
    }

    [Fact]
    public void Action_first_phrasing_with_list_and_reason()
    {
        var result = Generate("Reject purchase orders where supplier risk rating is one of High, Critical because supplier risk is too high");

        result.Succeeded.Should().BeTrue();
        var condition = result.Proposal!.Condition;
        condition.Field.Should().Be("supplier.riskRating");
        condition.Operator.Should().Be("In");
        condition.Value!.AsArray().Select(v => v!.GetValue<string>()).Should().Equal("High", "Critical");
        result.Proposal.Actions.Single().GetString("reason").Should().Be("supplier risk is too high");
    }

    [Fact]
    public void Or_with_bare_value_continues_the_previous_comparison()
    {
        var result = Generate("If department is HR or IT then add tag department-review");

        result.Succeeded.Should().BeTrue();
        var condition = result.Proposal!.Condition;
        condition.Kind.Should().Be(ConditionNodeKind.Any);
        condition.Any!.Select(c => c.Value!.GetValue<string>()).Should().Equal("HR", "IT");
        result.Proposal.Actions.Single().GetString("tag").Should().Be("department-review");
    }

    [Fact]
    public void Between_is_not_split_on_its_and()
    {
        var result = Generate("If quantity is between 10 and 20 then tag as medium-batch");

        result.Succeeded.Should().BeTrue();
        result.Proposal!.Condition.Operator.Should().Be("Between");
        result.Proposal.Condition.Value!.AsArray().Select(v => v!.GetValue<decimal>()).Should().Equal(10m, 20m);
    }

    [Theory]
    [InlineData("If amount is over $50,000 then require CFO approval", "document.amount", "GreaterThan", 50000)]
    [InlineData("If order value is at least 50k then require CFO approval", "document.amount", "GreaterThanOrEqual", 50000)]
    [InlineData("Require CFO approval when total > 100000", "document.amount", "GreaterThan", 100000)]
    [InlineData("When qty is 100 or more, require CFO approval", "document.quantity", "GreaterThanOrEqual", 100)]
    [InlineData("If number of items does not exceed 3 then require CFO approval", "document.quantity", "LessThanOrEqual", 3)]
    public void Numeric_phrasings_and_aliases(string text, string field, string op, int value)
    {
        var result = Generate(text);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors));
        result.Proposal!.Condition.Field.Should().Be(field);
        result.Proposal.Condition.Operator.Should().Be(op);
        result.Proposal.Condition.Value!.GetValue<decimal>().Should().Be(value);
        result.Proposal.Actions.Single().GetString("role").Should().Be("CFO");
    }

    [Fact]
    public void Notify_and_workflow_actions()
    {
        var result = Generate("When amount exceeds 5000, notify procurement using the large-order template and trigger workflow vendor-review");

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors));
        var actions = result.Proposal!.Actions;
        actions.Select(a => a.Type).Should().Equal("Notify", "TriggerWorkflow");
        actions[0].GetString("channel").Should().Be("procurement");
        actions[0].GetString("template").Should().Be("large-order");
        actions[1].GetString("workflow").Should().Be("vendor-review");
    }

    [Fact]
    public void Defaults_are_listed_as_assumptions_and_lower_confidence()
    {
        var result = Generate("If supplier risk is Critical then reject");

        result.Succeeded.Should().BeTrue();
        result.Proposal!.Actions.Single().GetString("reason").Should().StartWith("Rule condition met");
        result.Proposal.Assumptions.Should().Contain(a => a.Contains("No rejection reason"));
        result.Proposal.Confidence.Should().BeLessThan(0.95);
    }

    [Fact]
    public void List_fields_use_contains_and_set_value_is_supported()
    {
        var result = Generate("If tags contain urgent then set routing priority to high");

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors));
        result.Proposal!.Condition.Operator.Should().Be("Contains");
        var action = result.Proposal.Actions.Single();
        action.ActionType.Should().Be(RuleActionType.SetValue);
        action.GetString("field").Should().Be("routing.priority");
        action.GetString("value").Should().Be("high");
    }

    [Fact]
    public void Unknown_fields_are_reported_never_invented()
    {
        var result = Generate("If colour is red then reject");

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Unknown field 'colour'"));
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("If quantity is large then reject")]
    [InlineData("If quantity > 10 then dance")]
    [InlineData("")]
    public void Uninterpretable_text_fails_with_guidance(string text)
    {
        var result = Generate(text);

        result.Succeeded.Should().BeFalse();
        result.Proposal.Should().BeNull();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        const string text = "If amount is at least 50000 and department is IT, require director approval";

        var first = Generate(text).Proposal!;
        var second = Generate(text).Proposal!;

        ConditionSerializer.Serialize(first.Condition).Should().Be(ConditionSerializer.Serialize(second.Condition));
        first.Name.Should().Be(second.Name);
    }
}
