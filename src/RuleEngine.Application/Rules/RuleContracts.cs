using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using RuleEngine.Application.Common;
using RuleEngine.Application.Validation;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;

namespace RuleEngine.Application.Rules;

/// <summary>Creates a rule together with its first draft version.</summary>
public sealed class CreateRuleRequest
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>Lower value = evaluated first.</summary>
    public int Priority { get; init; } = 100;

    /// <summary>Condition tree, e.g. <c>{ "field": "document.quantity", "operator": "GreaterThan", "value": 10 }</c>.</summary>
    public JsonElement Condition { get; init; }

    /// <summary>Action list, e.g. <c>[{ "type": "RequireApproval", "parameters": { "role": "Manager" } }]</c>.</summary>
    public JsonElement Actions { get; init; }
}

/// <summary>Adds a new draft version to an existing rule.</summary>
public sealed class AddRuleVersionRequest
{
    public JsonElement Condition { get; init; }

    public JsonElement Actions { get; init; }

    public Guid? ExpectedConcurrencyStamp { get; init; }
}

public sealed class PublishRuleRequest
{
    public int VersionNumber { get; init; }

    public Guid? ExpectedConcurrencyStamp { get; init; }
}

public sealed class RuleListQuery : PageQuery
{
    public Guid? RuleSetId { get; set; }

    public bool? IsActive { get; set; }

    /// <summary>Only rules that currently have at least one draft version.</summary>
    public bool? HasDraft { get; set; }

    /// <summary>Only rules having at least one version from this source (Manual / AiGenerated).</summary>
    public RuleSource? Source { get; set; }

    public string? Search { get; set; }
}

public sealed record RuleSummaryDto(
    Guid Id,
    Guid RuleSetId,
    string Name,
    int Priority,
    bool IsActive,
    int? CurrentPublishedVersionNumber,
    int LatestVersionNumber,
    bool HasDraft,
    int VersionCount,
    DateTime UpdatedAtUtc);

public sealed record RuleVersionDto(
    Guid Id,
    int VersionNumber,
    RuleVersionStatus Status,
    RuleSource Source,
    JsonNode? Condition,
    JsonNode? Actions,
    string? PromptVersion,
    JsonNode? Generation,
    string CreatedBy,
    DateTime CreatedAtUtc,
    string? PublishedBy,
    DateTime? PublishedAtUtc,
    DateTime? RetiredAtUtc);

public sealed record RuleDetailDto(
    Guid Id,
    Guid RuleSetId,
    string Name,
    string? Description,
    int Priority,
    bool IsActive,
    int? CurrentPublishedVersionNumber,
    string CreatedBy,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    Guid ConcurrencyStamp,
    int TestCaseCount,
    IReadOnlyList<RuleVersionDto> Versions);

/// <summary>Result of a write: the rule plus non-blocking validation warnings (e.g. conflicts).</summary>
public sealed record RuleWriteResultDto(RuleDetailDto Rule, IReadOnlyList<ValidationIssue> Warnings);

public static class RuleMapper
{
    public static RuleDetailDto ToDetail(Rule rule, int testCaseCount) => new(
        rule.Id,
        rule.RuleSetId,
        rule.Name,
        rule.Description,
        rule.Priority,
        rule.IsActive,
        rule.CurrentPublishedVersionNumber,
        rule.CreatedBy,
        rule.CreatedAtUtc,
        rule.UpdatedAtUtc,
        rule.ConcurrencyStamp,
        testCaseCount,
        rule.Versions.OrderByDescending(v => v.VersionNumber).Select(ToDto).ToList());

    public static RuleVersionDto ToDto(RuleVersion version) => new(
        version.Id,
        version.VersionNumber,
        version.Status,
        version.Source,
        JsonNode.Parse(version.ConditionJson),
        JsonNode.Parse(version.ActionsJson),
        version.PromptVersion,
        version.GenerationMetadataJson is null ? null : JsonNode.Parse(version.GenerationMetadataJson),
        version.CreatedBy,
        version.CreatedAtUtc,
        version.PublishedBy,
        version.PublishedAtUtc,
        version.RetiredAtUtc);

    public static IReadOnlyList<ErrorItem> ToErrorItems(IEnumerable<ValidationIssue> issues) =>
        issues.Select(i => new ErrorItem(i.Path, i.Message, i.Code)).ToList();
}

public sealed class CreateRuleRequestValidator : AbstractValidator<CreateRuleRequest>
{
    public CreateRuleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Rule.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(Rule.DescriptionMaxLength);
        RuleFor(x => x.Priority).InclusiveBetween(0, 1_000_000);
        RuleFor(x => x.Condition).Must(c => c.ValueKind == JsonValueKind.Object).WithMessage("'condition' must be a JSON object.");
        RuleFor(x => x.Actions).Must(a => a.ValueKind == JsonValueKind.Array).WithMessage("'actions' must be a JSON array.");
    }
}

public sealed class AddRuleVersionRequestValidator : AbstractValidator<AddRuleVersionRequest>
{
    public AddRuleVersionRequestValidator()
    {
        RuleFor(x => x.Condition).Must(c => c.ValueKind == JsonValueKind.Object).WithMessage("'condition' must be a JSON object.");
        RuleFor(x => x.Actions).Must(a => a.ValueKind == JsonValueKind.Array).WithMessage("'actions' must be a JSON array.");
    }
}

public sealed class PublishRuleRequestValidator : AbstractValidator<PublishRuleRequest>
{
    public PublishRuleRequestValidator()
    {
        RuleFor(x => x.VersionNumber).GreaterThan(0);
    }
}
