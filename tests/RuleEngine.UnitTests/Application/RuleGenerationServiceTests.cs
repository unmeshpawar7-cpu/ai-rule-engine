using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RuleEngine.Application.Generation;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Enums;

namespace RuleEngine.UnitTests.Application;

public class RuleGenerationServiceTests : IDisposable
{
    private readonly ServiceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static IRuleGenerator Returning(GeneratedRuleProposal proposal)
    {
        var generator = Substitute.For<IRuleGenerator>();
        generator.GenerateAsync(Arg.Any<RuleGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RuleGenerationResult { Proposal = proposal, Generator = "stub", PromptVersion = "stub/v1", Model = "stub-model" }));
        return generator;
    }

    [Fact]
    public async Task Valid_proposals_are_saved_as_inactive_ai_generated_drafts_only()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        var service = _fixture.Generation(new CompositeRuleGenerator(new PatternRuleGenerator(), Array.Empty<IFallbackRuleGenerator>(), NullLogger<CompositeRuleGenerator>.Instance));

        var response = await service.GenerateAsync(
            ruleSet.Id,
            new GenerateRuleRequest { Text = "Documents with quantity greater than 10 should require manager approval." },
            CancellationToken.None);

        response.Outcome.Should().Be(GenerationOutcomes.DraftSaved);
        response.Saved.Should().BeTrue();
        response.VersionStatus.Should().Be(RuleVersionStatus.Draft);
        response.PromptVersion.Should().Be(PatternRuleGenerator.GrammarVersion);

        var rule = await _fixture.Db.Rules.Include(r => r.Versions).SingleAsync();
        rule.IsActive.Should().BeFalse();
        rule.CurrentPublishedVersionId.Should().BeNull();
        var version = rule.Versions.Single();
        version.Status.Should().Be(RuleVersionStatus.Draft);
        version.Source.Should().Be(RuleSource.AiGenerated);
        version.PromptVersion.Should().Be(PatternRuleGenerator.GrammarVersion);
        version.GenerationMetadataJson.Should().Contain("confidence");
        (await _fixture.Db.RuleExecutions.CountAsync()).Should().Be(0);
        (await _fixture.Db.AuditLogs.CountAsync(a => a.Action == "AiDraftGenerated")).Should().Be(1);
    }

    [Fact]
    public async Task Proposals_failing_the_validation_gate_are_not_saved()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        var hallucinated = new GeneratedRuleProposal
        {
            Name = "colour rule",
            Condition = ConditionNode.Leaf("document.colour", ConditionOperator.Equals, "red"),
            Actions = new List<RuleActionDefinition> { RuleActionDefinition.Reject("red") },
            Explanation = "x",
            Confidence = 0.9,
        };

        var response = await _fixture.Generation(Returning(hallucinated)).GenerateAsync(ruleSet.Id, new GenerateRuleRequest { Text = "red things are rejected" }, CancellationToken.None);

        response.Outcome.Should().Be(GenerationOutcomes.RejectedByValidation);
        response.Saved.Should().BeFalse();
        response.Errors.Should().Contain(e => e.Code == "UNKNOWN_FIELD");
        response.Proposal.Should().NotBeNull();
        (await _fixture.Db.Rules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Proposal_only_mode_stores_nothing()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        var proposal = new GeneratedRuleProposal
        {
            Name = "large",
            Condition = ConditionNode.Leaf("document.quantity", ConditionOperator.GreaterThan, 10),
            Actions = new List<RuleActionDefinition> { RuleActionDefinition.RequireApproval("Manager") },
            Confidence = 0.9,
        };

        var response = await _fixture.Generation(Returning(proposal)).GenerateAsync(ruleSet.Id, new GenerateRuleRequest { Text = "x", SaveAsDraft = false }, CancellationToken.None);

        response.Outcome.Should().Be(GenerationOutcomes.ProposalOnly);
        (await _fixture.Db.Rules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Duplicate_generated_names_get_a_suffix()
    {
        var ruleSet = await _fixture.SeedRuleSetAsync();
        var service = _fixture.Generation(new PatternRuleGenerator());
        var request = new GenerateRuleRequest { Text = "If quantity > 10 then tag as bulk" };

        var first = await service.GenerateAsync(ruleSet.Id, request, CancellationToken.None);
        var second = await service.GenerateAsync(ruleSet.Id, request, CancellationToken.None);

        second.Proposal!.Name.Should().Be(first.Proposal!.Name + " (2)");
    }

    [Fact]
    public async Task Composite_uses_the_fallback_only_when_the_grammar_fails()
    {
        var fallback = Substitute.For<IFallbackRuleGenerator>();
        fallback.Name.Returns("llm:stub");
        fallback.GenerateAsync(Arg.Any<RuleGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(RuleGenerationResult.Failure("llm:stub", "rule-generation/v1", new[] { "model refused" })));
        var composite = new CompositeRuleGenerator(new PatternRuleGenerator(), new[] { fallback }, NullLogger<CompositeRuleGenerator>.Instance);

        var understood = await composite.GenerateAsync(new RuleGenerationRequest("If quantity > 10 then reject", TestData.PurchaseSchema(), "x"), CancellationToken.None);
        var notUnderstood = await composite.GenerateAsync(new RuleGenerationRequest("Make it fair for everybody", TestData.PurchaseSchema(), "x"), CancellationToken.None);

        understood.Generator.Should().Be("pattern");
        notUnderstood.Succeeded.Should().BeFalse();
        notUnderstood.Errors.Should().Contain(e => e.StartsWith("pattern:")).And.Contain(e => e.Contains("model refused"));
        await fallback.Received(1).GenerateAsync(Arg.Any<RuleGenerationRequest>(), Arg.Any<CancellationToken>());
    }
}
