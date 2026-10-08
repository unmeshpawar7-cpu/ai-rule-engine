namespace RuleEngine.Domain.Enums;

/// <summary>Overall outcome of an execution, derived from the per-action outcomes.</summary>
public enum ExecutionStatus
{
    Succeeded,
    PartiallyFailed,
    Failed,
}
