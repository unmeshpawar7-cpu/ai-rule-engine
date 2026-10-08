using RuleEngine.Domain.Common;
using RuleEngine.Domain.Enums;

namespace RuleEngine.Domain.Entities;

/// <summary>
/// A rule inside a rule set. Its logic lives in immutable <see cref="RuleVersion"/>s; at most one
/// version is Published at a time and only the published version of an active rule is executed.
/// </summary>
public sealed class Rule
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;

    private Rule()
    {
    }

    public Guid Id { get; private set; }

    public Guid RuleSetId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>Lower value = evaluated first.</summary>
    public int Priority { get; private set; }

    public bool IsActive { get; private set; }

    public Guid? CurrentPublishedVersionId { get; private set; }

    public int? CurrentPublishedVersionNumber { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    public List<RuleVersion> Versions { get; private set; } = new();

    public List<RuleTestCase> TestCases { get; private set; } = new();

    public static Rule Create(
        Guid ruleSetId,
        string name,
        string? description,
        int priority,
        RuleVersionContent content,
        string createdBy,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            throw new DomainException($"Rule name is required and must be at most {NameMaxLength} characters.");
        }

        if (priority < 0)
        {
            throw new DomainException("Priority must be zero or positive.");
        }

        var rule = new Rule
        {
            Id = Guid.NewGuid(),
            RuleSetId = ruleSetId,
            Name = name.Trim(),
            Description = Text.Truncate(description?.Trim(), DescriptionMaxLength),
            Priority = priority,
            IsActive = false,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            ConcurrencyStamp = Guid.NewGuid(),
        };

        rule.Versions.Add(RuleVersion.CreateDraft(rule.Id, 1, content, createdBy, nowUtc));
        return rule;
    }

    public RuleVersion? FindVersion(int versionNumber) =>
        Versions.FirstOrDefault(v => v.VersionNumber == versionNumber);

    public RuleVersion? LatestDraft() =>
        Versions.Where(v => v.Status == RuleVersionStatus.Draft).OrderByDescending(v => v.VersionNumber).FirstOrDefault();

    public RuleVersion AddDraftVersion(RuleVersionContent content, string createdBy, DateTime nowUtc)
    {
        var next = Versions.Count == 0 ? 1 : Versions.Max(v => v.VersionNumber) + 1;
        var version = RuleVersion.CreateDraft(Id, next, content, createdBy, nowUtc);
        Versions.Add(version);
        Touch(nowUtc);
        return version;
    }

    /// <summary>Publishes a draft; the previously published version (if any) is retired.</summary>
    public RuleVersion Publish(int versionNumber, string publishedBy, DateTime nowUtc)
    {
        var version = FindVersion(versionNumber)
            ?? throw new DomainException($"Version {versionNumber} does not exist.");
        if (version.Status != RuleVersionStatus.Draft)
        {
            throw new DomainException($"Only draft versions can be published; version {versionNumber} is {version.Status}.");
        }

        foreach (var published in Versions.Where(v => v.Status == RuleVersionStatus.Published))
        {
            published.Retire(nowUtc);
        }

        version.MarkPublished(publishedBy, nowUtc);
        CurrentPublishedVersionId = version.Id;
        CurrentPublishedVersionNumber = version.VersionNumber;
        Touch(nowUtc);
        return version;
    }

    public void Activate(DateTime nowUtc)
    {
        if (CurrentPublishedVersionId is null)
        {
            throw new DomainException("A rule needs a published version before it can be activated.");
        }

        IsActive = true;
        Touch(nowUtc);
    }

    public void Deactivate(DateTime nowUtc)
    {
        IsActive = false;
        Touch(nowUtc);
    }

    /// <summary>Removes a draft version. Published and retired versions are immutable history.</summary>
    public RuleVersion RemoveDraft(int versionNumber, DateTime nowUtc)
    {
        var version = FindVersion(versionNumber)
            ?? throw new DomainException($"Version {versionNumber} does not exist.");
        if (version.Status != RuleVersionStatus.Draft)
        {
            throw new DomainException("Only draft versions can be deleted.");
        }

        Versions.Remove(version);
        Touch(nowUtc);
        return version;
    }

    private void Touch(DateTime nowUtc)
    {
        UpdatedAtUtc = nowUtc;
        ConcurrencyStamp = Guid.NewGuid();
    }
}
