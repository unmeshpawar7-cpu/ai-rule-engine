using System.Text.Json;
using RuleEngine.Domain.Common;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Application.Common.Abstractions;

public interface IAuditTrail
{
    /// <summary>
    /// Stages an audit record on the current unit of work. It is persisted by the same
    /// SaveChangesAsync call (and therefore the same transaction) as the change it describes.
    /// </summary>
    void Record(string entityType, Guid entityId, string action, object? before, object? after);
}

public sealed class AuditTrail : IAuditTrail
{
    private readonly IRuleEngineDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;

    public AuditTrail(IRuleEngineDbContext db, ICurrentUser currentUser, TimeProvider clock)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
    }

    public void Record(string entityType, Guid entityId, string action, object? before, object? after)
    {
        _db.AuditLogs.Add(AuditLog.Create(
            entityType,
            entityId.ToString(),
            action,
            _currentUser.UserName,
            _clock.GetUtcNow().UtcDateTime,
            Summarize(before),
            Summarize(after),
            _currentUser.CorrelationId));
    }

    private static string? Summarize(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => JsonSerializer.Serialize(value, value.GetType(), DomainJson.Options),
    };
}
