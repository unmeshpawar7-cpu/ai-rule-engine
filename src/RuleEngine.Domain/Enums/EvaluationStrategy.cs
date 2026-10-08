namespace RuleEngine.Domain.Enums;

/// <summary>How a rule set combines matching rules.</summary>
public enum EvaluationStrategy
{
    /// <summary>Stop at the first matching rule (by priority).</summary>
    FirstMatch,

    /// <summary>Evaluate every rule; all matching rules contribute actions.</summary>
    AllMatches,
}
