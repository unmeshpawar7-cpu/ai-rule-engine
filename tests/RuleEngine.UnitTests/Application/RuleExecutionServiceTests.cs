using Microsoft.EntityFrameworkCore;
using RuleEngine.Application.Common;
using RuleEngine.Application.Execution;
using RuleEngine.Domain.Enums;

namespace RuleEngine.UnitTests.Application;

public class RuleExecutionServiceTests : IDisposable
{
    private const string QuantityOver10 = """{ "field": "document.quantity", "operator": "GreaterThan", "value": 10 }""";
    private const string ManagerApproval = """[ { "type": "RequireApproval", "parameters": { "role": "Manager" } }, { "type": "AddTag", "parameters": { "tag": "large" } } ]""";

    private readonly ServiceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Execute_evaluates_runs_actions_and_records_execution_and_audit()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        await _fixture.SeedRuleAsync(ruleSet.Id, "large", 10, QuantityOver10, ManagerApproval);

        var result = await _fixture.Execution.ExecuteAsync(ruleSet.Id, TestData.Facts("""{ "document": { "quantity": 25, "amount": 100 } }"""), null, CancellationToken.None);

        result.Decision.Should().Be(Decisions.ApprovalRequired);
        result.Status.Should().Be(ExecutionStatus.Succeeded);
        result.MatchedRules.Should().ContainSingle().Which.RuleName.Should().Be("large");
        result.Actions.Select(a => a.Status).Should().AllBeEquivalentTo(ActionExecutionStatus.Succeeded);
        result.Tags.Should().Equal("large");
        result.Trace.Should().ContainSingle();
        (await _fixture.Db.RuleExecutions.CountAsync()).Should().Be(1);
        (await _fixture.Db.ActionExecutions.CountAsync()).Should().Be(2);
        (await _fixture.Db.AuditLogs.CountAsync(a => a.Action == "Executed")).Should().Be(1);
        var stored = await _fixture.Db.RuleExecutions.SingleAsync();
        stored.InputHash.Should().HaveLength(64);
        stored.CorrelationId.Should().Be("corr-test");
    }

    [Fact]
    public async Task Same_idempotency_key_and_payload_replays_the_stored_result()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        await _fixture.SeedRuleAsync(ruleSet.Id, "large", 10, QuantityOver10, ManagerApproval);

        var first = await _fixture.Execution.ExecuteAsync(ruleSet.Id, TestData.Facts("""{ "document": { "quantity": 25, "amount": 100 } }"""), "key-1", CancellationToken.None);
        // Same facts, different property order: canonical hashing treats them as identical.
        var second = await _fixture.Execution.ExecuteAsync(ruleSet.Id, TestData.Facts("""{ "document": { "amount": 100, "quantity": 25 } }"""), "key-1", CancellationToken.None);

        first.Replayed.Should().BeFalse();
        second.Replayed.Should().BeTrue();
        second.ExecutionId.Should().Be(first.ExecutionId!.Value);
        (await _fixture.Db.RuleExecutions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_with_another_payload_conflicts()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        await _fixture.SeedRuleAsync(ruleSet.Id, "large", 10, QuantityOver10, ManagerApproval);
        await _fixture.Execution.ExecuteAsync(ruleSet.Id, TestData.Facts("""{ "document": { "quantity": 25, "amount": 100 } }"""), "key-2", CancellationToken.None);

        var act = () => _fixture.Execution.ExecuteAsync(ruleSet.Id, TestData.Facts("""{ "document": { "quantity": 26, "amount": 100 } }"""), "key-2", CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Facts_are_validated_against_the_schema_before_evaluation()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();

        var act = () => _fixture.Execution.ExecuteAsync(ruleSet.Id, TestData.Facts("""{ "document": { "amount": "lots" } }"""), null, CancellationToken.None);

        var error = await act.Should().ThrowAsync<BusinessRuleException>();
        error.Which.Errors.Select(e => e.Field).Should().BeEquivalentTo(new[] { "facts.document.quantity", "facts.document.amount" });
        (await _fixture.Db.RuleExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Inactive_rule_sets_cannot_be_executed()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        ruleSet.Deactivate(_fixture.Clock.GetUtcNow().UtcDateTime);
        await _fixture.Db.SaveChangesAsync();

        var act = () => _fixture.Execution.ExecuteAsync(ruleSet.Id, TestData.Facts("""{ "document": { "quantity": 1, "amount": 1 } }"""), null, CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>();
    }

    [Fact]
    public async Task Only_published_versions_of_active_rules_execute()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        await _fixture.SeedRuleAsync(ruleSet.Id, "draft-only", 10, QuantityOver10, ManagerApproval, publish: false);

        var result = await _fixture.Execution.ExecuteAsync(ruleSet.Id, TestData.Facts("""{ "document": { "quantity": 25, "amount": 1 } }"""), null, CancellationToken.None);

        result.Decision.Should().Be(Decisions.NoMatch);
        result.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Simulation_includes_drafts_and_has_no_side_effects()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        await _fixture.SeedRuleAsync(ruleSet.Id, "draft-only", 10, QuantityOver10, ManagerApproval, publish: false);
        var auditBefore = await _fixture.Db.AuditLogs.CountAsync();

        var result = await _fixture.Execution.SimulateAsync(
            ruleSet.Id,
            new SimulateRuleSetRequest { Facts = TestData.Facts("""{ "document": { "quantity": 25, "amount": 1 } }""") },
            CancellationToken.None);

        result.IsSimulation.Should().BeTrue();
        result.ExecutionId.Should().BeNull();
        result.Decision.Should().Be(Decisions.ApprovalRequired);
        result.MatchedRules.Single().VersionStatus.Should().Be(RuleVersionStatus.Draft);
        result.Actions.Should().OnlyContain(a => a.Status == ActionExecutionStatus.NotExecuted);
        (await _fixture.Db.RuleExecutions.CountAsync()).Should().Be(0);
        (await _fixture.Db.AuditLogs.CountAsync()).Should().Be(auditBefore);
    }

    [Fact]
    public async Task Reject_wins_over_approval_in_the_decision()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        await _fixture.SeedRuleAsync(ruleSet.Id, "approve", 10, QuantityOver10, ManagerApproval);
        await _fixture.SeedRuleAsync(
            ruleSet.Id,
            "reject-risky",
            20,
            """{ "field": "supplier.riskRating", "operator": "Equals", "value": "Critical" }""",
            """[ { "type": "Reject", "parameters": { "reason": "Critical supplier" } } ]""");

        var result = await _fixture.Execution.ExecuteAsync(
            ruleSet.Id,
            TestData.Facts("""{ "document": { "quantity": 25, "amount": 1 }, "supplier": { "riskRating": "Critical" } }"""),
            null,
            CancellationToken.None);

        result.Decision.Should().Be(Decisions.Rejected);
        result.MatchedRules.Should().HaveCount(2);
    }
}
