namespace RuleEngine.Domain.Enums;

public enum ActionExecutionStatus
{
    Succeeded,
    Failed,
    Skipped,

    /// <summary>The action was selected but deliberately not executed (simulation / test runs).</summary>
    NotExecuted,
}
