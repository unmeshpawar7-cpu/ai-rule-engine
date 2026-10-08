namespace RuleEngine.Application.Common.Abstractions;

/// <summary>Ambient caller information (user name for audit, correlation id for tracing).</summary>
public interface ICurrentUser
{
    string UserName { get; }

    string CorrelationId { get; }
}
