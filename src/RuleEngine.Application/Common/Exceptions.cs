namespace RuleEngine.Application.Common;

/// <summary>One error entry of the API error envelope.</summary>
public sealed record ErrorItem(string Field, string Message, string? Code = null);

/// <summary>Maps to HTTP 404.</summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string entity, object key)
        : base($"{entity} '{key}' was not found.")
    {
    }
}

/// <summary>Maps to HTTP 409 (duplicate names, stale concurrency stamps, idempotency key reuse).</summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>Maps to HTTP 422: the request is well-formed but violates a business rule.</summary>
public sealed class BusinessRuleException : Exception
{
    public BusinessRuleException(string message, IEnumerable<ErrorItem>? errors = null)
        : base(message)
    {
        Errors = errors?.ToList() ?? new List<ErrorItem>();
    }

    public IReadOnlyList<ErrorItem> Errors { get; }
}

/// <summary>Maps to HTTP 401.</summary>
public sealed class AuthenticationFailedException : Exception
{
    public AuthenticationFailedException()
        : base("Invalid user name or password.")
    {
    }
}
