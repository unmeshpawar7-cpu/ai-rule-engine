using RuleEngine.Domain.Common;

namespace RuleEngine.Domain.Entities;

/// <summary>Append-only audit record, written in the same transaction as the change it describes.</summary>
public sealed class AuditLog
{
    public const int SummaryMaxLength = 4000;

    private AuditLog()
    {
    }

    public Guid Id { get; private set; }

    public string EntityType { get; private set; } = string.Empty;

    public string EntityId { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;

    public string Actor { get; private set; } = string.Empty;

    public DateTime OccurredAtUtc { get; private set; }

    public string? BeforeSummary { get; private set; }

    public string? AfterSummary { get; private set; }

    public string? CorrelationId { get; private set; }

    public static AuditLog Create(
        string entityType,
        string entityId,
        string action,
        string actor,
        DateTime occurredAtUtc,
        string? beforeSummary,
        string? afterSummary,
        string? correlationId) =>
        new()
        {
            Id = Guid.NewGuid(),
            EntityType = Text.Truncate(entityType, 100)!,
            EntityId = Text.Truncate(entityId, 100)!,
            Action = Text.Truncate(action, 100)!,
            Actor = Text.Truncate(actor, 100)!,
            OccurredAtUtc = occurredAtUtc,
            BeforeSummary = Text.Truncate(beforeSummary, SummaryMaxLength),
            AfterSummary = Text.Truncate(afterSummary, SummaryMaxLength),
            CorrelationId = Text.Truncate(correlationId, 100),
        };
}
