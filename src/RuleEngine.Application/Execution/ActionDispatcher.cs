using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuleEngine.Application.Common;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;

namespace RuleEngine.Application.Execution;

/// <summary>Outcome of executing one selected action (after retries).</summary>
public sealed record ActionOutcome(
    SelectedAction Selected,
    ActionExecutionStatus Status,
    int Attempts,
    string? Message,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc);

public interface IActionDispatcher
{
    Task<IReadOnlyList<ActionOutcome>> DispatchAsync(
        Guid executionId,
        Guid ruleSetId,
        string correlationId,
        IReadOnlyList<SelectedAction> actions,
        JsonObject facts,
        CancellationToken cancellationToken);
}

/// <summary>Abstraction over waiting so retry tests don't sleep.</summary>
public interface IRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class TaskRetryDelay : IRetryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
}

public static class ExponentialBackoff
{
    /// <summary>
    /// "Equal jitter" backoff: half of the capped exponential delay is fixed, the other half random.
    /// <paramref name="retryNumber"/> starts at 1; <paramref name="jitterSample"/> is in [0, 1).
    /// </summary>
    public static TimeSpan Compute(int retryNumber, TimeSpan baseDelay, TimeSpan maxDelay, double jitterSample)
    {
        var exponent = Math.Min(Math.Max(retryNumber - 1, 0), 30);
        var raw = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(raw, maxDelay.TotalMilliseconds);
        var jitter = Math.Clamp(jitterSample, 0d, 1d);
        return TimeSpan.FromMilliseconds((capped / 2d) + (jitter * capped / 2d));
    }
}

/// <summary>
/// Executes selected actions sequentially in evaluation order through their <see cref="IActionExecutor"/>,
/// retrying transient failures with exponential backoff + jitter. One action failing never stops the others;
/// each outcome is reported individually.
/// </summary>
public sealed class ActionDispatcher : IActionDispatcher
{
    private readonly Dictionary<RuleActionType, IActionExecutor> _executors;
    private readonly IRetryDelay _delay;
    private readonly RuleEngineOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ActionDispatcher> _logger;

    public ActionDispatcher(
        IEnumerable<IActionExecutor> executors,
        IRetryDelay delay,
        IOptions<RuleEngineOptions> options,
        TimeProvider clock,
        ILogger<ActionDispatcher> logger)
    {
        _executors = new Dictionary<RuleActionType, IActionExecutor>();
        foreach (var executor in executors)
        {
            _executors[executor.ActionType] = executor;
        }

        _delay = delay;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ActionOutcome>> DispatchAsync(
        Guid executionId,
        Guid ruleSetId,
        string correlationId,
        IReadOnlyList<SelectedAction> actions,
        JsonObject facts,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<ActionOutcome>(actions.Count);
        foreach (var selected in actions.OrderBy(a => a.Sequence))
        {
            var context = new ActionExecutionContext(executionId, ruleSetId, correlationId, selected, facts);
            outcomes.Add(await ExecuteOneAsync(context, cancellationToken));
        }

        return outcomes;
    }

    private async Task<ActionOutcome> ExecuteOneAsync(ActionExecutionContext context, CancellationToken cancellationToken)
    {
        var started = Now;
        var selected = context.Selected;
        var type = selected.Action.ActionType;
        if (type is null || !_executors.TryGetValue(type.Value, out var executor))
        {
            return new ActionOutcome(selected, ActionExecutionStatus.Failed, 0, $"No executor registered for action type '{selected.Action.Type}'.", started, Now);
        }

        var maxAttempts = Math.Max(1, _options.ActionMaxAttempts);
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                var result = await executor.ExecuteAsync(context, cancellationToken);
                return new ActionOutcome(selected, result.Status, attempt, result.Message, started, Now);
            }
            catch (TransientActionException ex) when (attempt < maxAttempts)
            {
                var delay = ExponentialBackoff.Compute(
                    attempt,
                    TimeSpan.FromMilliseconds(_options.ActionBaseDelayMs),
                    TimeSpan.FromMilliseconds(_options.ActionMaxDelayMs),
                    Random.Shared.NextDouble());
                _logger.LogWarning(
                    "Transient failure executing {ActionType} (sequence {Sequence}, attempt {Attempt}/{MaxAttempts}): {Error}. Retrying in {DelayMs} ms",
                    type,
                    selected.Sequence,
                    attempt,
                    maxAttempts,
                    ex.Message,
                    (long)delay.TotalMilliseconds);
                await _delay.DelayAsync(delay, cancellationToken);
            }
            catch (TransientActionException ex)
            {
                _logger.LogError("Action {ActionType} (sequence {Sequence}) failed after {Attempts} attempts: {Error}", type, selected.Sequence, attempt, ex.Message);
                return new ActionOutcome(selected, ActionExecutionStatus.Failed, attempt, $"Failed after {attempt} attempt(s): {ex.Message}", started, Now);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Action {ActionType} (sequence {Sequence}) failed permanently", type, selected.Sequence);
                return new ActionOutcome(selected, ActionExecutionStatus.Failed, attempt, ex.Message, started, Now);
            }
        }
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
}
