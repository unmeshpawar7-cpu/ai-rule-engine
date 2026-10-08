using System.Text.RegularExpressions;
using Serilog.Context;

namespace RuleEngine.Api.Common;

/// <summary>
/// Accepts a safe inbound <c>X-Correlation-ID</c> (or creates one), exposes it as the request's
/// TraceIdentifier, echoes it in the response and pushes it into the Serilog log context.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";

    private static readonly Regex SafeId = new("^[A-Za-z0-9._:-]{1,100}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[HeaderName].ToString();
        var correlationId = !string.IsNullOrEmpty(inbound) && SafeId.IsMatch(inbound) ? inbound : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = correlationId;
        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    public static string Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id ? id : context.TraceIdentifier;
}
