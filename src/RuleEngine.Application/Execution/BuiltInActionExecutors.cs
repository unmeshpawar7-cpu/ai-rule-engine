using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Enums;

namespace RuleEngine.Application.Execution;

/// <summary>SetValue is applied by the evaluator (so later rules can chain on it); execution just records it.</summary>
public sealed class SetValueActionExecutor : IActionExecutor
{
    public RuleActionType ActionType => RuleActionType.SetValue;

    public Task<ActionExecutorResult> ExecuteAsync(ActionExecutionContext context, CancellationToken cancellationToken)
    {
        var field = context.Selected.Action.GetString(ActionParameters.Field);
        return Task.FromResult(ActionExecutorResult.Success($"Set '{field}' in output facts."));
    }
}

/// <summary>Records that approval is required; the execution decision becomes ApprovalRequired.</summary>
public sealed class RequireApprovalActionExecutor : IActionExecutor
{
    public RuleActionType ActionType => RuleActionType.RequireApproval;

    public Task<ActionExecutorResult> ExecuteAsync(ActionExecutionContext context, CancellationToken cancellationToken)
    {
        var role = context.Selected.Action.GetString(ActionParameters.Role);
        var reason = context.Selected.Action.GetString(ActionParameters.Reason);
        var message = reason is null ? $"Approval required from role '{role}'." : $"Approval required from role '{role}': {reason}";
        return Task.FromResult(ActionExecutorResult.Success(message));
    }
}

/// <summary>Adds a tag to the execution result.</summary>
public sealed class AddTagActionExecutor : IActionExecutor
{
    public RuleActionType ActionType => RuleActionType.AddTag;

    public Task<ActionExecutorResult> ExecuteAsync(ActionExecutionContext context, CancellationToken cancellationToken)
    {
        var tag = context.Selected.Action.GetString(ActionParameters.Tag);
        return Task.FromResult(ActionExecutorResult.Success($"Tagged '{tag}'."));
    }
}

/// <summary>Records a rejection; the execution decision becomes Rejected.</summary>
public sealed class RejectActionExecutor : IActionExecutor
{
    public RuleActionType ActionType => RuleActionType.Reject;

    public Task<ActionExecutorResult> ExecuteAsync(ActionExecutionContext context, CancellationToken cancellationToken)
    {
        var reason = context.Selected.Action.GetString(ActionParameters.Reason);
        return Task.FromResult(ActionExecutorResult.Success($"Rejected: {reason}"));
    }
}
