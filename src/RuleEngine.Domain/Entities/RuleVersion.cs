using RuleEngine.Domain.Enums;

namespace RuleEngine.Domain.Entities;

/// <summary>Content of a rule version as authored (already validated and normalised JSON).</summary>
public sealed record RuleVersionContent(
    string ConditionJson,
    string ActionsJson,
    RuleSource Source,
    string? PromptVersion = null,
    string? GenerationMetadataJson = null);

/// <summary>
/// An immutable snapshot of a rule's logic. Only the lifecycle status changes
/// (Draft -> Published -> Retired); condition and actions never do.
/// </summary>
public sealed class RuleVersion
{
    public const int PromptVersionMaxLength = 100;

    private RuleVersion()
    {
    }

    public Guid Id { get; private set; }

    public Guid RuleId { get; private set; }

    public int VersionNumber { get; private set; }

    public string ConditionJson { get; private set; } = "{}";

    public string ActionsJson { get; private set; } = "[]";

    public RuleVersionStatus Status { get; private set; }

    public RuleSource Source { get; private set; }

    /// <summary>Prompt template version for AI-generated versions (e.g. <c>rule-generation/v1</c>).</summary>
    public string? PromptVersion { get; private set; }

    /// <summary>Explanation, confidence, assumptions and generator metadata for AI-generated versions.</summary>
    public string? GenerationMetadataJson { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public string? PublishedBy { get; private set; }

    public DateTime? PublishedAtUtc { get; private set; }

    public DateTime? RetiredAtUtc { get; private set; }

    internal static RuleVersion CreateDraft(Guid ruleId, int versionNumber, RuleVersionContent content, string createdBy, DateTime nowUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            RuleId = ruleId,
            VersionNumber = versionNumber,
            ConditionJson = content.ConditionJson,
            ActionsJson = content.ActionsJson,
            Status = RuleVersionStatus.Draft,
            Source = content.Source,
            PromptVersion = content.PromptVersion,
            GenerationMetadataJson = content.GenerationMetadataJson,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

    internal void MarkPublished(string publishedBy, DateTime nowUtc)
    {
        Status = RuleVersionStatus.Published;
        PublishedBy = publishedBy;
        PublishedAtUtc = nowUtc;
    }

    internal void Retire(DateTime nowUtc)
    {
        Status = RuleVersionStatus.Retired;
        RetiredAtUtc = nowUtc;
    }
}
