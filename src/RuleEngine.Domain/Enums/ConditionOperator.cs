namespace RuleEngine.Domain.Enums;

/// <summary>Leaf comparison operators supported by the condition DSL.</summary>
public enum ConditionOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Contains,
    StartsWith,
    EndsWith,
    In,
    NotIn,
    Between,
    IsNull,
    IsNotNull,
    Matches,
}
