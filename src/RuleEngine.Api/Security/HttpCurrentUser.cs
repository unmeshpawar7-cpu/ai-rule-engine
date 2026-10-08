using RuleEngine.Api.Common;
using RuleEngine.Application.Common.Abstractions;

namespace RuleEngine.Api.Security;

/// <summary>Resolves the caller from the authenticated principal and the request's correlation id.</summary>
public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    private readonly string _fallbackCorrelationId = Guid.NewGuid().ToString("N");

    public HttpCurrentUser(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public string UserName
    {
        get
        {
            var user = _accessor.HttpContext?.User;
            var name = user?.FindFirst(JwtOptions.NameClaim)?.Value ?? user?.Identity?.Name;
            return string.IsNullOrWhiteSpace(name) ? "anonymous" : name;
        }
    }

    public string CorrelationId =>
        _accessor.HttpContext is { } context ? CorrelationIdMiddleware.Get(context) : _fallbackCorrelationId;
}
