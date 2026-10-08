using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RuleEngine.Application.Common;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Application.Validation;
using RuleEngine.Domain.Common;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;

namespace RuleEngine.Application.Generation;

/// <summary>Natural-language rule authoring request.</summary>
public sealed class GenerateRuleRequest
{
    /// <summary>The rule in plain language, e.g. "Documents with quantity greater than 10 should require manager approval."</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Optional rule name; defaults to a name derived from the condition.</summary>
    public string? Name { get; init; }

    /// <summary>Optional priority (lower = evaluated first); defaults to 100.</summary>
    public int? Priority { get; init; }

    /// <summary>When false the validated proposal is only returned, not stored. Default true.</summary>
    public bool SaveAsDraft { get; init; } = true;
}

public static class GenerationOutcomes
{
    /// <summary>Proposal passed the validation gate and was stored as a Draft / AiGenerated version.</summary>
    public const string DraftSaved = "DraftSaved";

    /// <summary>Proposal passed the validation gate; nothing was stored (saveAsDraft = false).</summary>
    public const string ProposalOnly = "ProposalOnly";

    /// <summary>A proposal was produced but failed the validation gate; nothing was stored.</summary>
    public const string RejectedByValidation = "RejectedByValidation";

    /// <summary>No generator could interpret the text.</summary>
    public const string NotUnderstood = "NotUnderstood";
}

public sealed record GeneratedProposalDto(
    string Name,
    int Priority,
    JsonNode? Condition,
    JsonNode? Actions,
    string Explanation,
    double Confidence,
    IReadOnlyList<string> Assumptions);

public sealed class GenerateRuleResponse
{
    /// <summary>DraftSaved | ProposalOnly | RejectedByValidation | NotUnderstood.</summary>
    public string Outcome { get; init; } = GenerationOutcomes.NotUnderstood;

    public bool Saved => Outcome == GenerationOutcomes.DraftSaved;

    public Guid? RuleId { get; init; }

    public int? VersionNumber { get; init; }

    /// <summary>Always Draft when saved: generated rules are never published or executed automatically.</summary>
    public RuleVersionStatus? VersionStatus { get; init; }

    public GeneratedProposalDto? Proposal { get; init; }

    public IReadOnlyList<ValidationIssue> Errors { get; init; } = Array.Empty<ValidationIssue>();

    public IReadOnlyList<ValidationIssue> Warnings { get; init; } = Array.Empty<ValidationIssue>();

    /// <summary>"pattern" or "llm:&lt;provider&gt;".</summary>
    public string Generator { get; init; } = string.Empty;

    public string? PromptVersion { get; init; }

    public string? Model { get; init; }

    public int Attempts { get; init; }

    public long? LatencyMs { get; init; }
}

public sealed class GenerateRuleRequestValidator : AbstractValidator<GenerateRuleRequest>
{
    public GenerateRuleRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().MaximumLength(PatternRuleGenerator.MaxTextLength);
        RuleFor(x => x.Name).MaximumLength(Rule.NameMaxLength);
        RuleFor(x => x.Priority).InclusiveBetween(0, 1_000_000).When(x => x.Priority is not null);
    }
}

/// <summary>
/// AI-assisted authoring use case. The generator only <em>proposes</em>; the proposal must pass the same
/// <see cref="RuleValidator"/> gate as hand-written rules and is then stored as an inactive rule with a
/// Draft / AiGenerated version. Publishing and activation stay explicit human steps.
/// </summary>
public sealed class RuleGenerationService
{
    private const int DefaultPriority = 100;
    private const int SourceTextMaxLength = 2000;

    private readonly IRuleEngineDbContext _db;
    private readonly IRuleGenerator _generator;
    private readonly RuleValidator _validator;
    private readonly IAuditTrail _audit;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;
    private readonly ILogger<RuleGenerationService> _logger;

    public RuleGenerationService(
        IRuleEngineDbContext db,
        IRuleGenerator generator,
        RuleValidator validator,
        IAuditTrail audit,
        ICurrentUser currentUser,
        TimeProvider clock,
        ILogger<RuleGenerationService> logger)
    {
        _db = db;
        _generator = generator;
        _validator = validator;
        _audit = audit;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
    }

    public async Task<GenerateRuleResponse> GenerateAsync(Guid ruleSetId, GenerateRuleRequest request, CancellationToken cancellationToken)
    {
        var ruleSet = await _db.RuleSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == ruleSetId, cancellationToken)
            ?? throw new NotFoundException("Rule set", ruleSetId);

        var schema = ruleSet.GetFactSchema();
        var generation = await _generator.GenerateAsync(new RuleGenerationRequest(request.Text.Trim(), schema, ruleSet.Name), cancellationToken);
        if (!generation.Succeeded)
        {
            _logger.LogInformation(
                "Rule generation for rule set {RuleSetId} not understood by {Generator} after {Attempts} attempt(s)",
                ruleSetId,
                generation.Generator,
                generation.Attempts);
            return Response(generation, GenerationOutcomes.NotUnderstood, null, generation.Errors.Select(e => new ValidationIssue("GENERATION_FAILED", "text", e)).ToList());
        }

        var proposal = generation.Proposal!;
        var name = string.IsNullOrWhiteSpace(request.Name) ? proposal.Name : request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Generated rule";
        }

        var priority = request.Priority ?? proposal.SuggestedPriority ?? DefaultPriority;
        var condition = JsonSerializer.SerializeToElement(proposal.Condition, DomainJson.Options);
        var actions = JsonSerializer.SerializeToElement(proposal.Actions, DomainJson.Options);

        // The validation gate: identical to manual authoring (schema, operators, values, limits, actions, conflicts).
        var validation = await _validator.ValidateAsync(ruleSet, condition, actions, priority, null, name, cancellationToken);
        var proposalDto = new GeneratedProposalDto(
            name,
            priority,
            JsonNode.Parse(condition.GetRawText()),
            JsonNode.Parse(actions.GetRawText()),
            proposal.Explanation,
            Math.Clamp(proposal.Confidence, 0d, 1d),
            proposal.Assumptions);

        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "Generated rule for rule set {RuleSetId} rejected by the validation gate with {ErrorCount} error(s)",
                ruleSetId,
                validation.Errors.Count);
            return Response(generation, GenerationOutcomes.RejectedByValidation, proposalDto, validation.Errors, validation.Warnings);
        }

        if (!request.SaveAsDraft)
        {
            return Response(generation, GenerationOutcomes.ProposalOnly, proposalDto, Array.Empty<ValidationIssue>(), validation.Warnings);
        }

        var uniqueName = await UniqueNameAsync(ruleSetId, name, cancellationToken);
        var metadata = JsonSerializer.Serialize(
            new
            {
                generation.Generator,
                generation.Model,
                generation.PromptVersion,
                generation.Attempts,
                proposal.Explanation,
                Confidence = proposalDto.Confidence,
                proposal.Assumptions,
                SourceText = Text.Truncate(request.Text.Trim(), SourceTextMaxLength),
            },
            DomainJson.Options);

        var content = new RuleVersionContent(
            validation.NormalizedConditionJson,
            validation.NormalizedActionsJson,
            RuleSource.AiGenerated,
            generation.PromptVersion,
            metadata);
        var rule = Rule.Create(ruleSetId, uniqueName, proposal.Explanation, priority, content, _currentUser.UserName, _clock.GetUtcNow().UtcDateTime);
        _db.Rules.Add(rule);
        _audit.Record(
            "Rule",
            rule.Id,
            "AiDraftGenerated",
            null,
            new { rule.Name, Version = 1, Status = RuleVersionStatus.Draft, generation.Generator, generation.PromptVersion, Confidence = proposalDto.Confidence });
        await _db.SaveChangesAsync(cancellationToken);

        return new GenerateRuleResponse
        {
            Outcome = GenerationOutcomes.DraftSaved,
            RuleId = rule.Id,
            VersionNumber = 1,
            VersionStatus = RuleVersionStatus.Draft,
            Proposal = proposalDto with { Name = uniqueName },
            Errors = Array.Empty<ValidationIssue>(),
            Warnings = validation.Warnings,
            Generator = generation.Generator,
            PromptVersion = generation.PromptVersion,
            Model = generation.Model,
            Attempts = generation.Attempts,
            LatencyMs = generation.LatencyMs,
        };
    }

    private async Task<string> UniqueNameAsync(Guid ruleSetId, string name, CancellationToken cancellationToken)
    {
        var baseName = Text.Truncate(name, Rule.NameMaxLength - 6)!;
        var existing = await _db.Rules.AsNoTracking()
            .Where(r => r.RuleSetId == ruleSetId && r.Name.StartsWith(baseName))
            .Select(r => r.Name)
            .ToListAsync(cancellationToken);
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return Text.Truncate($"{baseName} ({Guid.NewGuid():N})", Rule.NameMaxLength)!;
    }

    private static GenerateRuleResponse Response(
        RuleGenerationResult generation,
        string outcome,
        GeneratedProposalDto? proposal,
        IReadOnlyList<ValidationIssue> errors,
        IReadOnlyList<ValidationIssue>? warnings = null) =>
        new()
        {
            Outcome = outcome,
            Proposal = proposal,
            Errors = errors,
            Warnings = warnings ?? Array.Empty<ValidationIssue>(),
            Generator = generation.Generator,
            PromptVersion = generation.PromptVersion,
            Model = generation.Model,
            Attempts = generation.Attempts,
            LatencyMs = generation.LatencyMs,
        };
}
