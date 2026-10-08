namespace RuleEngine.Domain.Common;

/// <summary>
/// Raised when an operation would violate a domain invariant (e.g. publishing a non-draft version).
/// Mapped to HTTP 422 by the API layer.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }
}
