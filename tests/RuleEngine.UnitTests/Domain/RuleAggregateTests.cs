using System.Text.Json.Nodes;
using RuleEngine.Domain.Common;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Facts;

namespace RuleEngine.UnitTests.Domain;

public class RuleAggregateTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RuleVersionContent Content = new("""{"field":"document.quantity","operator":"GreaterThan","value":10}""", """[{"type":"AddTag","parameters":{"tag":"x"}}]""", RuleSource.Manual);

    [Fact]
    public void New_rule_starts_inactive_with_a_draft_version()
    {
        var rule = Rule.Create(Guid.NewGuid(), "Large orders", null, 10, Content, "author", Now);

        rule.IsActive.Should().BeFalse();
        rule.Versions.Should().ContainSingle().Which.Status.Should().Be(RuleVersionStatus.Draft);
        rule.CurrentPublishedVersionId.Should().BeNull();
    }

    [Fact]
    public void Publishing_a_new_version_retires_the_previous_one()
    {
        var rule = Rule.Create(Guid.NewGuid(), "Large orders", null, 10, Content, "author", Now);
        rule.Publish(1, "admin", Now);
        var stampAfterFirstPublish = rule.ConcurrencyStamp;
        rule.AddDraftVersion(Content, "author", Now);

        rule.Publish(2, "admin", Now.AddMinutes(1));

        rule.FindVersion(1)!.Status.Should().Be(RuleVersionStatus.Retired);
        rule.FindVersion(2)!.Status.Should().Be(RuleVersionStatus.Published);
        rule.CurrentPublishedVersionNumber.Should().Be(2);
        rule.ConcurrencyStamp.Should().NotBe(stampAfterFirstPublish);
    }

    [Fact]
    public void Only_drafts_can_be_published_or_deleted()
    {
        var rule = Rule.Create(Guid.NewGuid(), "Large orders", null, 10, Content, "author", Now);
        rule.Publish(1, "admin", Now);

        rule.Invoking(r => r.Publish(1, "admin", Now)).Should().Throw<DomainException>();
        rule.Invoking(r => r.RemoveDraft(1, Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Activation_requires_a_published_version()
    {
        var rule = Rule.Create(Guid.NewGuid(), "Large orders", null, 10, Content, "author", Now);

        rule.Invoking(r => r.Activate(Now)).Should().Throw<DomainException>();
        rule.Publish(1, "admin", Now);
        rule.Activate(Now);
        rule.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Rule_set_rejects_invalid_fact_schema()
    {
        var schema = new FactSchema { Fields = { new FactField { Path = "bad path!", Type = FactType.Number } } };

        var act = () => RuleSet.Create("x", null, EvaluationStrategy.AllMatches, schema, "author", Now);

        act.Should().Throw<DomainException>().WithMessage("Invalid fact schema*");
    }

    [Fact]
    public void Fact_validator_reports_missing_required_and_wrong_types()
    {
        var errors = FactValidator.Validate(TestData.PurchaseSchema(), TestData.Facts("""{ "document": { "amount": "lots" } }"""));

        errors.Select(e => e.Field).Should().BeEquivalentTo(new[] { "document.quantity", "document.amount" });
    }

    [Fact]
    public void Fact_paths_are_case_insensitive_and_settable()
    {
        var facts = TestData.Facts("""{ "Document": { "Quantity": 3 } }""");

        FactPath.TryGet(facts, "document.quantity", out var value).Should().BeTrue();
        value!.GetValue<int>().Should().Be(3);

        FactPath.Set(facts, "routing.priority", JsonValue.Create("high"));
        facts["routing"]!["priority"]!.GetValue<string>().Should().Be("high");
    }
}
