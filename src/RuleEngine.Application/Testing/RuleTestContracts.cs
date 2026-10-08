using System.Text.Json.Nodes;
using FluentValidation;
using RuleEngine.Application.Common;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;

namespace RuleEngine.Application.Testing;

/// <summary>Creates or replaces a rule test case.</summary>
public sealed class RuleTestCaseRequest
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Input facts, validated against the rule set's schema when the test runs.</summary>
    public JsonObject? InputFacts { get; init; }

    /// <summary>Whether the rule is expected to match these facts.</summary>
    public bool ExpectedMatch { get; init; }

    /// <summary>Expected action types (order-insensitive). Omit to skip the action check.</summary>
    public List<string>? ExpectedActionTypes { get; init; }
}

public sealed class RuleTestCaseListQuery : PageQuery
{
}

public sealed record RuleTestCaseDto(
    Guid Id,
    Guid RuleId,
    string Name,
    JsonNode? InputFacts,
    bool ExpectedMatch,
    IReadOnlyList<string>? ExpectedActionTypes,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LastRunAtUtc,
    bool? LastRunPassed)
{
    public static RuleTestCaseDto From(RuleTestCase testCase) => new(
        testCase.Id,
        testCase.RuleId,
        testCase.Name,
        JsonNode.Parse(testCase.InputFactsJson),
        testCase.ExpectedMatch,
        testCase.GetExpectedActionTypes(),
        testCase.CreatedAtUtc,
        testCase.UpdatedAtUtc,
        testCase.LastRunAtUtc,
        testCase.LastRunPassed);
}

public sealed record RuleTestCaseResultDto(
    Guid TestCaseId,
    string Name,
    bool Passed,
    bool ExpectedMatch,
    bool ActualMatch,
    IReadOnlyList<string>? ExpectedActionTypes,
    IReadOnlyList<string> ActualActionTypes,
    IReadOnlyList<string> Failures,
    RuleEvaluationTrace? Trace);

public sealed record RuleTestRunReport(
    Guid RuleId,
    int VersionNumber,
    RuleVersionStatus VersionStatus,
    int Total,
    int Passed,
    int Failed,
    IReadOnlyList<RuleTestCaseResultDto> Results)
{
    public bool AllPassed => Failed == 0;
}

public sealed class RuleTestCaseRequestValidator : AbstractValidator<RuleTestCaseRequest>
{
    public RuleTestCaseRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(RuleTestCase.NameMaxLength);
        RuleFor(x => x.InputFacts).NotNull().WithMessage("'inputFacts' must be a JSON object.");
        RuleForEach(x => x.ExpectedActionTypes)
            .Must(t => RuleActionTypes.TryParse(t, out _))
            .WithMessage("Unknown action type '{PropertyValue}'.");
    }
}
