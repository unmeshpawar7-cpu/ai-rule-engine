using System.Text.Json.Nodes;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;

namespace RuleEngine.Application.Execution;

/// <summary>Everything an executor needs to perform one selected action.</summary>
public sealed record ActionExecutionContext(
    Guid ExecutionId,
    Guid RuleSetId,
    string CorrelationId,
    SelectedAction Selected,
    JsonObject Facts);

public sealed record ActionExecutorResult(ActionExecutionStatus Status, string? Message)
{
    public static ActionExecutorResult Success(string? message = null) => new(ActionExecutionStatus.Succeeded, message);

    public static ActionExecutorResult Skip(string message) => new(ActionExecutionStatus.Skipped, message);

    public static ActionExecutorResult Fail(string message) => new(ActionExecutionStatus.Failed, message);
}

/// <summary>
/// Strategy interface: one implementation per <see cref="RuleActionType"/>. Throw
/// <see cref="TransientActionException"/> for failures worth retrying (timeouts, 5xx, 429);
/// return <see cref="ActionExecutorResult.Fail"/> for permanent ones.
/// </summary>
public interface IActionExecutor
{
    RuleActionType ActionType { get; }

    Task<ActionExecutorResult> ExecuteAsync(ActionExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>A failure that may succeed if retried (network error, timeout, HTTP 5xx/408/429).</summary>
public sealed class TransientActionException : Exception
{
    public TransientActionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
