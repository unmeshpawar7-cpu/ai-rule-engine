using System.ComponentModel.DataAnnotations;
using RuleEngine.Domain.Evaluation;

namespace RuleEngine.Application.Common;

/// <summary>Engine limits and policies, bound from the <c>RuleEngine</c> configuration section.</summary>
public sealed class RuleEngineOptions
{
    public const string SectionName = "RuleEngine";

    /// <summary>When true, a version can only be published if all of the rule's test cases pass against it.</summary>
    public bool RequirePassingTestsToPublish { get; set; } = true;

    [Range(1, 50)]
    public int MaxConditionDepth { get; set; } = 10;

    [Range(1, 5000)]
    public int MaxConditionNodes { get; set; } = 200;

    [Range(1, 10_000)]
    public int RegexTimeoutMs { get; set; } = 100;

    [Range(1, 4096)]
    public int MaxRegexLength { get; set; } = 256;

    [Range(1, 100)]
    public int MaxActionsPerRule { get; set; } = 20;

    /// <summary>Total attempts (first try + retries) for actions failing with a transient error.</summary>
    [Range(1, 10)]
    public int ActionMaxAttempts { get; set; } = 3;

    [Range(0, 60_000)]
    public int ActionBaseDelayMs { get; set; } = 200;

    [Range(0, 600_000)]
    public int ActionMaxDelayMs { get; set; } = 5_000;

    public EvaluationOptions ToEvaluationOptions() => new()
    {
        MaxDepth = MaxConditionDepth,
        MaxNodes = MaxConditionNodes,
        RegexTimeout = TimeSpan.FromMilliseconds(RegexTimeoutMs),
    };
}
