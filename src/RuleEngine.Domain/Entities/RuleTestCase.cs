using System.Text.Json;
using RuleEngine.Domain.Common;

namespace RuleEngine.Domain.Entities;

/// <summary>A regression test for a rule: input facts plus the expected match and action types.</summary>
public sealed class RuleTestCase
{
    public const int NameMaxLength = 200;

    private RuleTestCase()
    {
    }

    public Guid Id { get; private set; }

    public Guid RuleId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string InputFactsJson { get; private set; } = "{}";

    public bool ExpectedMatch { get; private set; }

    /// <summary>JSON array of expected action type names; null means "don't check actions".</summary>
    public string? ExpectedActionTypesJson { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public DateTime? LastRunAtUtc { get; private set; }

    public bool? LastRunPassed { get; private set; }

    public static RuleTestCase Create(
        Guid ruleId,
        string name,
        string inputFactsJson,
        bool expectedMatch,
        IReadOnlyCollection<string>? expectedActionTypes,
        string createdBy,
        DateTime nowUtc)
    {
        var testCase = new RuleTestCase
        {
            Id = Guid.NewGuid(),
            RuleId = ruleId,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };
        testCase.Update(name, inputFactsJson, expectedMatch, expectedActionTypes, nowUtc);
        return testCase;
    }

    public void Update(string name, string inputFactsJson, bool expectedMatch, IReadOnlyCollection<string>? expectedActionTypes, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMaxLength)
        {
            throw new DomainException($"Test case name is required and must be at most {NameMaxLength} characters.");
        }

        Name = name.Trim();
        InputFactsJson = inputFactsJson;
        ExpectedMatch = expectedMatch;
        ExpectedActionTypesJson = expectedActionTypes is null
            ? null
            : JsonSerializer.Serialize(expectedActionTypes.ToList(), DomainJson.Options);
        UpdatedAtUtc = nowUtc;
    }

    public IReadOnlyList<string>? GetExpectedActionTypes() =>
        ExpectedActionTypesJson is null
            ? null
            : JsonSerializer.Deserialize<List<string>>(ExpectedActionTypesJson, DomainJson.Options) ?? new List<string>();

    public void RecordRun(bool passed, DateTime nowUtc)
    {
        LastRunPassed = passed;
        LastRunAtUtc = nowUtc;
    }
}
