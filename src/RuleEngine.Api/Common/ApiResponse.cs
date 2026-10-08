using System.Text.Json.Serialization;
using RuleEngine.Application.Common;

namespace RuleEngine.Api.Common;

/// <summary>Response envelope used by every endpoint: <c>{ success, data, message, errors?, traceId? }</c>.</summary>
public sealed class ApiResponse<T>
{
    public bool Success { get; init; }

    public T? Data { get; init; }

    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ErrorItem>? Errors { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceId { get; init; }

    public static ApiResponse<T> Ok(T data, string? message = null) => new() { Success = true, Data = data, Message = message };
}

/// <summary>Error envelope: <c>{ success: false, message, errors: [{ field, message, code }], traceId }</c>.</summary>
public sealed class ApiErrorResponse
{
    public bool Success => false;

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<ErrorItem> Errors { get; init; } = Array.Empty<ErrorItem>();

    public string? TraceId { get; init; }

    public static ApiErrorResponse Create(string message, IEnumerable<ErrorItem>? errors, string? traceId) =>
        new() { Message = message, Errors = errors?.ToList() ?? new List<ErrorItem>(), TraceId = traceId };
}
