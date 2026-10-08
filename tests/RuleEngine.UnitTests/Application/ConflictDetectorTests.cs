using System.Text.Json.Nodes;
using RuleEngine.Application.Validation;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Enums;
using static RuleEngine.UnitTests.TestData;

namespace RuleEngine.UnitTests.Application;

public class ConflictDetectorTests
{
    private readonly ConflictDetector _detector = new();

    private static RuleCandidate Candidate(string name, int priority, ConditionNode condition, params RuleActionDefinition[] actions) =>
        new(Guid.NewGuid(), name, priority, condition, actions);

    [Fact]
    public void Overlapping_conditions_with_contradictory_actions_warn()
    {
        var candidate = Candidate("approve", 10, Leaf("document.quantity", ConditionOperator.GreaterThan, 10), RuleActionDefinition.RequireApproval("Manager"));
        var existing = Candidate("reject-huge", 10, Leaf("document.quantity", ConditionOperator.GreaterThan, 50), RuleActionDefinition.Reject("too many"));

        var issues = _detector.Detect(candidate, new[] { existing }, PurchaseSchema());

        issues.Should().ContainSingle().Which.Code.Should().Be(ValidationCodes.Conflict);
        issues[0].Message.Should().Contain("reject-huge");
    }

    [Fact]
    public void Disjoint_ranges_do_not_conflict()
    {
        var candidate = Candidate("approve", 10, Leaf("document.quantity", ConditionOperator.GreaterThan, 10), RuleActionDefinition.RequireApproval("Manager"));
        var existing = Candidate("reject-small", 10, Leaf("document.quantity", ConditionOperator.LessThanOrEqual, 10), RuleActionDefinition.Reject("too few"));

        _detector.Detect(candidate, new[] { existing }, PurchaseSchema()).Should().BeEmpty();
    }

    [Fact]
    public void Disjoint_value_sets_do_not_conflict()
    {
        var candidate = Candidate("it", 10, Leaf("document.department", ConditionOperator.Equals, "IT"), RuleActionDefinition.RequireApproval("Manager"));
        var existing = Candidate("hr", 10, Leaf("document.department", ConditionOperator.In, new JsonArray("HR", "Finance")), RuleActionDefinition.Reject("no"));

        _detector.Detect(candidate, new[] { existing }, PurchaseSchema()).Should().BeEmpty();
    }

    [Fact]
    public void Different_priorities_are_not_reported()
    {
        var candidate = Candidate("approve", 10, Leaf("document.quantity", ConditionOperator.GreaterThan, 10), RuleActionDefinition.RequireApproval("Manager"));
        var existing = Candidate("reject", 20, Leaf("document.quantity", ConditionOperator.GreaterThan, 10), RuleActionDefinition.Reject("no"));

        _detector.Detect(candidate, new[] { existing }, PurchaseSchema()).Should().BeEmpty();
    }

    [Fact]
    public void Setting_the_same_field_to_different_values_warns()
    {
        var candidate = Candidate("high", 5, Leaf("document.amount", ConditionOperator.GreaterThanOrEqual, 1000), RuleActionDefinition.SetValue("routing.priority", "high"));
        var existing = Candidate("low", 5, Leaf("document.amount", ConditionOperator.Between, new JsonArray(500, 2000)), RuleActionDefinition.SetValue("routing.priority", "low"));

        _detector.Detect(candidate, new[] { existing }, PurchaseSchema()).Should().ContainSingle();
    }

    [Fact]
    public void Non_conjunctive_conditions_are_skipped()
    {
        var candidate = Candidate(
            "any",
            10,
            ConditionNode.AnyOf(Leaf("document.quantity", ConditionOperator.GreaterThan, 10), Leaf("document.amount", ConditionOperator.GreaterThan, 10)),
            RuleActionDefinition.RequireApproval("Manager"));
        var existing = Candidate("reject", 10, Leaf("document.quantity", ConditionOperator.GreaterThan, 10), RuleActionDefinition.Reject("no"));

        _detector.Detect(candidate, new[] { existing }, PurchaseSchema()).Should().BeEmpty();
    }
}
