using RuleEngine.Domain.Common;
using RuleEngine.Domain.Enums;

namespace RuleEngine.Domain.Entities;

/// <summary>Outcome of one action executed as part of a <see cref="RuleExecution"/>.</summary>
public sealed class ActionExecution
{
    public const int MessageMaxLength = 2000;

    private ActionExecution()
    {
    }

    public Guid Id { get; private set; }

    public Guid RuleExecutionId { get; private set; }

    public int Sequence { get; private set; }

    public Guid RuleId { get; private set; }

    public Guid RuleVersionId { get; private set; }

    public string ActionType { get; private set; } = string.Empty;

    public string ParametersJson { get; private set; } = "{}";

    public ActionExecutionStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public string? Message { get; private set; }

    public DateTime StartedAtUtc { get; private set; }

    public DateTime CompletedAtUtc { get; private set; }

    public long DurationMs { get; private set; }

    public static ActionExecution Create(
        Guid ruleExecutionId,
        int sequence,
        Guid ruleId,
        Guid ruleVersionId,
        string actionType,
        string parametersJson,
        ActionExecutionStatus status,
        int attempts,
        string? message,
        DateTime startedAtUtc,
        DateTime completedAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            RuleExecutionId = ruleExecutionId,
            Sequence = sequence,
            RuleId = ruleId,
            RuleVersionId = ruleVersionId,
            ActionType = actionType,
            ParametersJson = parametersJson,
            Status = status,
            Attempts = attempts,
            Message = Text.Truncate(message, MessageMaxLength),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            DurationMs = Math.Max(0, (long)(completedAtUtc - startedAtUtc).TotalMilliseconds),
        };
}
