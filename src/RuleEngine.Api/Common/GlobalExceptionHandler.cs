using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RuleEngine.Application.Common;
using RuleEngine.Domain.Common;

namespace RuleEngine.Api.Common;

/// <summary>Maps exceptions to HTTP status codes and the error envelope. Internal details are never returned for 500s.</summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = CorrelationIdMiddleware.Get(httpContext);
        var (status, message, errors) = Map(exception);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            _logger.LogInformation("Request failed with {StatusCode}: {Message}", status, message);
        }

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(ApiErrorResponse.Create(message, errors, traceId), cancellationToken);
        return true;
    }

    internal static (int Status, string Message, IReadOnlyList<ErrorItem> Errors) Map(Exception exception) => exception switch
    {
        ValidationException validation => (
            StatusCodes.Status400BadRequest,
            "Validation failed",
            validation.Errors.Select(e => new ErrorItem(ToCamelPath(e.PropertyName), e.ErrorMessage, e.ErrorCode)).ToList()),
        BusinessRuleException business => (StatusCodes.Status422UnprocessableEntity, business.Message, business.Errors),
        DomainException domain => (StatusCodes.Status422UnprocessableEntity, domain.Message, Array.Empty<ErrorItem>()),
        NotFoundException notFound => (StatusCodes.Status404NotFound, notFound.Message, Array.Empty<ErrorItem>()),
        ConflictException conflict => (StatusCodes.Status409Conflict, conflict.Message, Array.Empty<ErrorItem>()),
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "The resource was modified concurrently. Reload it and retry.", Array.Empty<ErrorItem>()),
        AuthenticationFailedException auth => (StatusCodes.Status401Unauthorized, auth.Message, Array.Empty<ErrorItem>()),
        BadHttpRequestException badRequest => (badRequest.StatusCode, "The request could not be read.", Array.Empty<ErrorItem>()),
        OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "The request was cancelled.", Array.Empty<ErrorItem>()),
        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", Array.Empty<ErrorItem>()),
    };

    internal static string ToCamelPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        return string.Join('.', path.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
    }
}
