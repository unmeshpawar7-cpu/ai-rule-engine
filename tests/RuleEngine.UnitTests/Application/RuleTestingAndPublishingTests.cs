using Microsoft.EntityFrameworkCore;
using RuleEngine.Application.Common;
using RuleEngine.Application.Rules;
using RuleEngine.Application.Testing;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;

namespace RuleEngine.UnitTests.Application;

public class RuleTestingAndPublishingTests : IDisposable
{
    private const string QuantityOver10 = """{ "field": "document.quantity", "operator": "GreaterThan", "value": 10 }""";
    private const string Approval = """[ { "type": "RequireApproval", "parameters": { "role": "Manager" } } ]""";

    private readonly ServiceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private RuleTestCase TestCase(Guid ruleId, string name, string facts, bool expectedMatch, params string[] expectedActions) =>
        RuleTestCase.Create(ruleId, name, facts, expectedMatch, expectedActions.Length == 0 ? null : expectedActions, "tester", _fixture.Clock.GetUtcNow().UtcDateTime);

    [Fact]
    public void Runner_reports_passes_and_each_kind_of_failure()
    {
        var rule = TestData.Rule("large", 10, TestData.Leaf("document.quantity", ConditionOperator.GreaterThan, 10), RuleActionDefinition.RequireApproval("Manager"));
        var cases = new[]
        {
            TestCase(rule.RuleId, "a-matches", """{ "document": { "quantity": 25, "amount": 1 } }""", true, "RequireApproval"),
            TestCase(rule.RuleId, "b-wrong-expectation", """{ "document": { "quantity": 5, "amount": 1 } }""", true),
            TestCase(rule.RuleId, "c-wrong-actions", """{ "document": { "quantity": 25, "amount": 1 } }""", true, "Reject"),
            TestCase(rule.RuleId, "d-invalid-facts", """{ "document": { "amount": 1 } }""", false),
        };

        var report = _fixture.TestRunner.Run(TestData.PurchaseSchema(), rule, cases);

        report.Total.Should().Be(4);
        report.Passed.Should().Be(1);
        report.Results.Single(r => r.Name == "a-matches").Passed.Should().BeTrue();
        report.Results.Single(r => r.Name == "b-wrong-expectation").Failures.Single().Should().Contain("Expected match");
        report.Results.Single(r => r.Name == "c-wrong-actions").Failures.Single().Should().Contain("Expected actions");
        report.Results.Single(r => r.Name == "d-invalid-facts").Failures.Single().Should().Contain("fact schema");
    }

    [Fact]
    public async Task Failing_tests_block_publishing_when_required()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        var rule = await _fixture.SeedRuleAsync(ruleSet.Id, "large", 10, QuantityOver10, Approval, publish: false);
        _fixture.Db.RuleTestCases.Add(TestCase(rule.Id, "small orders do not match", """{ "document": { "quantity": 50, "amount": 1 } }""", false));
        await _fixture.Db.SaveChangesAsync();

        var act = () => _fixture.Rules.PublishAsync(rule.Id, new PublishRuleRequest { VersionNumber = 1 }, CancellationToken.None);

        var error = await act.Should().ThrowAsync<BusinessRuleException>();
        error.Which.Errors.Single().Code.Should().Be("TEST_FAILED");
        (await _fixture.Db.RuleVersions.SingleAsync()).Status.Should().Be(RuleVersionStatus.Draft);
    }

    [Fact]
    public async Task Passing_tests_allow_publishing_and_are_recorded()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        var rule = await _fixture.SeedRuleAsync(ruleSet.Id, "large", 10, QuantityOver10, Approval, publish: false);
        _fixture.Db.RuleTestCases.Add(TestCase(rule.Id, "large orders match", """{ "document": { "quantity": 50, "amount": 1 } }""", true, "RequireApproval"));
        await _fixture.Db.SaveChangesAsync();

        var published = await _fixture.Rules.PublishAsync(rule.Id, new PublishRuleRequest { VersionNumber = 1 }, CancellationToken.None);

        published.CurrentPublishedVersionNumber.Should().Be(1);
        (await _fixture.Db.RuleTestCases.SingleAsync()).LastRunPassed.Should().BeTrue();
        (await _fixture.Db.AuditLogs.CountAsync(a => a.Action == "Published")).Should().Be(1);
    }

    [Fact]
    public async Task Invalid_definitions_are_rejected_on_create()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        var request = new CreateRuleRequest
        {
            Name = "bad",
            Condition = TestData.Json("""{ "field": "document.colour", "operator": "Equals", "value": "red" }"""),
            Actions = TestData.Json(Approval),
        };

        var act = () => _fixture.Rules.CreateAsync(ruleSet.Id, request, CancellationToken.None);

        var error = await act.Should().ThrowAsync<BusinessRuleException>();
        error.Which.Errors.Single().Code.Should().Be("UNKNOWN_FIELD");
        (await _fixture.Db.Rules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_returns_conflict_warnings_but_still_saves()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        await _fixture.SeedRuleAsync(ruleSet.Id, "reject-huge", 10, """{ "field": "document.quantity", "operator": "GreaterThan", "value": 100 }""", """[ { "type": "Reject", "parameters": { "reason": "too many" } } ]""");
        var request = new CreateRuleRequest
        {
            Name = "approve-large",
            Priority = 10,
            Condition = TestData.Json(QuantityOver10),
            Actions = TestData.Json(Approval),
        };

        var result = await _fixture.Rules.CreateAsync(ruleSet.Id, request, CancellationToken.None);

        result.Warnings.Should().ContainSingle().Which.Code.Should().Be("CONFLICT");
        result.Rule.Versions.Single().Status.Should().Be(RuleVersionStatus.Draft);
    }
}
