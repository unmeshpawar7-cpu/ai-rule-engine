using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace RuleEngine.Infrastructure.Ai;

/// <summary>LLM settings. Provider and keys come from environment variables (see <see cref="FromConfiguration"/>).</summary>
public sealed class LlmOptions
{
    public const string Offline = "offline";
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";

    public string Provider { get; set; } = Offline;

    public string? OpenAiApiKey { get; set; }

    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/";

    public string? AnthropicApiKey { get; set; }

    public string AnthropicModel { get; set; } = "claude-sonnet-4-5";

    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com/";

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Total model calls per request: the first answer plus one retry with validation errors fed back.</summary>
    public int MaxAttempts { get; set; } = 2;

    public int MaxOutputTokens { get; set; } = 1024;

    public static LlmOptions FromConfiguration(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        static string? Read(Microsoft.Extensions.Configuration.IConfiguration c, string env, string key) =>
            string.IsNullOrWhiteSpace(c[env]) ? (string.IsNullOrWhiteSpace(c[key]) ? null : c[key]) : c[env];

        var options = new LlmOptions
        {
            Provider = (Read(configuration, "LLM_PROVIDER", "Llm:Provider") ?? Offline).Trim().ToLowerInvariant(),
            OpenAiApiKey = Read(configuration, "OPENAI_API_KEY", "Llm:OpenAiApiKey"),
            AnthropicApiKey = Read(configuration, "ANTHROPIC_API_KEY", "Llm:AnthropicApiKey"),
        };
        options.OpenAiModel = Read(configuration, "OPENAI_MODEL", "Llm:OpenAiModel") ?? options.OpenAiModel;
        options.AnthropicModel = Read(configuration, "ANTHROPIC_MODEL", "Llm:AnthropicModel") ?? options.AnthropicModel;
        options.OpenAiBaseUrl = Read(configuration, "OPENAI_BASE_URL", "Llm:OpenAiBaseUrl") ?? options.OpenAiBaseUrl;
        options.AnthropicBaseUrl = Read(configuration, "ANTHROPIC_BASE_URL", "Llm:AnthropicBaseUrl") ?? options.AnthropicBaseUrl;
        if (int.TryParse(configuration["Llm:TimeoutSeconds"], out var timeout) && timeout > 0)
        {
            options.TimeoutSeconds = timeout;
        }

        if (int.TryParse(configuration["Llm:MaxAttempts"], out var attempts) && attempts is > 0 and <= 5)
        {
            options.MaxAttempts = attempts;
        }

        return options;
    }
}

public sealed record LlmMessage(string Role, string Content);

public sealed record LlmRequest(string SystemPrompt, IReadOnlyList<LlmMessage> Messages, int MaxOutputTokens);

public sealed record LlmCompletion(string Content, int? InputTokens, int? OutputTokens);

/// <summary>Provider-neutral chat completion returning a JSON document as text.</summary>
public interface ILlmClient
{
    string Provider { get; }

    string Model { get; }

    Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}

/// <summary>The provider call failed (HTTP error, unreadable response). Message never contains secrets.</summary>
public sealed class LlmProviderException : Exception
{
    public LlmProviderException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal static class LlmJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>OpenAI Chat Completions with <c>response_format: json_object</c>. Hand-written DTOs, no SDK.</summary>
public sealed class OpenAiChatClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public OpenAiChatClient(HttpClient http, IOptions<LlmOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public string Provider => LlmOptions.OpenAi;

    public string Model => _options.OpenAiModel;

    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var messages = new List<OpenAiMessage> { new() { Role = "system", Content = request.SystemPrompt } };
        messages.AddRange(request.Messages.Select(m => new OpenAiMessage { Role = m.Role, Content = m.Content }));
        var body = new OpenAiChatRequest
        {
            Model = _options.OpenAiModel,
            Messages = messages,
            Temperature = 0,
            MaxTokens = request.MaxOutputTokens,
            ResponseFormat = new OpenAiResponseFormat { Type = "json_object" },
        };

        using var response = await _http.PostAsJsonAsync("v1/chat/completions", body, LlmJson.Options, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmProviderException($"OpenAI returned HTTP {(int)response.StatusCode}.");
        }

        OpenAiChatResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(LlmJson.Options, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new LlmProviderException("OpenAI returned an unreadable response body.", ex);
        }

        var content = payload?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new LlmProviderException("OpenAI returned no message content.");
        }

        return new LlmCompletion(content, payload!.Usage?.PromptTokens, payload.Usage?.CompletionTokens);
    }

    internal sealed class OpenAiChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<OpenAiMessage> Messages { get; set; } = new();

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("response_format")]
        public OpenAiResponseFormat? ResponseFormat { get; set; }
    }

    internal sealed class OpenAiResponseFormat
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "json_object";
    }

    internal sealed class OpenAiMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    internal sealed class OpenAiChatResponse
    {
        [JsonPropertyName("choices")]
        public List<OpenAiChoice>? Choices { get; set; }

        [JsonPropertyName("usage")]
        public OpenAiUsage? Usage { get; set; }
    }

    internal sealed class OpenAiChoice
    {
        [JsonPropertyName("message")]
        public OpenAiMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    internal sealed class OpenAiUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int? PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int? CompletionTokens { get; set; }
    }
}

/// <summary>Anthropic Messages API. Hand-written DTOs, no SDK. JSON-only output is enforced by the prompt and the parser.</summary>
public sealed class AnthropicMessagesClient : ILlmClient
{
    public const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public AnthropicMessagesClient(HttpClient http, IOptions<LlmOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public string Provider => LlmOptions.Anthropic;

    public string Model => _options.AnthropicModel;

    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var body = new AnthropicRequest
        {
            Model = _options.AnthropicModel,
            MaxTokens = request.MaxOutputTokens,
            System = request.SystemPrompt,
            Temperature = 0,
            Messages = request.Messages.Select(m => new AnthropicMessage { Role = m.Role, Content = m.Content }).ToList(),
        };

        using var response = await _http.PostAsJsonAsync("v1/messages", body, LlmJson.Options, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmProviderException($"Anthropic returned HTTP {(int)response.StatusCode}.");
        }

        AnthropicResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<AnthropicResponse>(LlmJson.Options, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new LlmProviderException("Anthropic returned an unreadable response body.", ex);
        }

        var text = string.Concat((payload?.Content ?? new List<AnthropicContentBlock>())
            .Where(b => b.Type == "text" && b.Text is not null)
            .Select(b => b.Text));
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new LlmProviderException("Anthropic returned no text content.");
        }

        return new LlmCompletion(text, payload!.Usage?.InputTokens, payload.Usage?.OutputTokens);
    }

    internal sealed class AnthropicRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("system")]
        public string? System { get; set; }

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }

        [JsonPropertyName("messages")]
        public List<AnthropicMessage> Messages { get; set; } = new();
    }

    internal sealed class AnthropicMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    internal sealed class AnthropicResponse
    {
        [JsonPropertyName("content")]
        public List<AnthropicContentBlock>? Content { get; set; }

        [JsonPropertyName("stop_reason")]
        public string? StopReason { get; set; }

        [JsonPropertyName("usage")]
        public AnthropicUsage? Usage { get; set; }
    }

    internal sealed class AnthropicContentBlock
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    internal sealed class AnthropicUsage
    {
        [JsonPropertyName("input_tokens")]
        public int? InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int? OutputTokens { get; set; }
    }
}
