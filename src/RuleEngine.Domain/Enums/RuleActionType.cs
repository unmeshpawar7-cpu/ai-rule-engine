namespace RuleEngine.Domain.Enums;

public enum RuleActionType
{
    SetValue,
    RequireApproval,
    AddTag,
    Reject,
    Notify,
    TriggerWorkflow,
}
