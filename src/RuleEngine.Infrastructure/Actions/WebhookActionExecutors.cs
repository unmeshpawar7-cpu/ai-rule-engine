using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuleEngine.Application.Execution;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Common;
using RuleEngine.Domain.Enums;

namespace RuleEngine.Infrastructure.Actions;

/// <summary>
/// Webhook endpoints for side-effecting actions, bound from the <c>Webhooks</c> configuration section.
/// Names not listed here are skipped (reported as <c>Skipped</c>), never sent to an arbitrary URL.
/// </summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>Notification channel name -> absolute http(s) URL.</summary>
    public Dictionary<string, string> NotificationChannels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Workflow name -> absolute http(s) URL.</summary>
    public Dictionary<string, string> Workflows { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Typed HttpClient that posts JSON to a webhook and classifies failures: network errors, timeouts,
/// HTTP 408/429/5xx throw <see cref="TransientActionException"/> (retried by the dispatcher);
/// other non-success codes are permanent failures.
/// </summary>
public sealed class WebhookClient
{
    private readonly HttpClient _http;

    public WebhookClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<ActionExecutorResult> PostAsync(Uri url, object payload, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(url, payload, DomainJson.Options, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new TransientActionException("Webhook request failed: " + ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientActionException("Webhook request timed out.", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || status >= 500)
            {
                throw new TransientActionException($"Webhook returned HTTP {status}.");
            }

            return response.IsSuccessStatusCode
                ? ActionExecutorResult.Success($"Webhook accepted (HTTP {status}).")
                : ActionExecutorResult.Fail($"Webhook rejected the request (HTTP {status}).");
        }
    }

    internal static Uri? ResolveEndpoint(IReadOnlyDictionary<string, string> endpoints, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !endpoints.TryGetValue(name, out var url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
    }
}

/// <summary>Notify(channel, template): posts a notification payload to the channel's configured webhook.</summary>
public sealed class NotifyActionExecutor : IActionExecutor
{
    private readonly WebhookClient _client;
    private readonly WebhookOptions _options;
    private readonly ILogger<NotifyActionExecutor> _logger;

    public NotifyActionExecutor(WebhookClient client, IOptions<WebhookOptions> options, ILogger<NotifyActionExecutor> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public RuleActionType ActionType => RuleActionType.Notify;

    public Task<ActionExecutorResult> ExecuteAsync(ActionExecutionContext context, CancellationToken cancellationToken)
    {
        var channel = context.Selected.Action.GetString(ActionParameters.Channel);
        var template = context.Selected.Action.GetString(ActionParameters.Template);
        var endpoint = WebhookClient.ResolveEndpoint(new Dictionary<string, string>(_options.NotificationChannels, StringComparer.OrdinalIgnoreCase), channel);
        if (endpoint is null)
        {
            _logger.LogWarning("Notify skipped: no webhook configured for channel {Channel}", channel);
            return Task.FromResult(ActionExecutorResult.Skip($"No webhook is configured for notification channel '{channel}'."));
        }

        var payload = new
        {
            Type = "rule-engine.notification",
            Channel = channel,
            Template = template,
            context.ExecutionId,
            context.RuleSetId,
            context.CorrelationId,
            context.Selected.RuleId,
            context.Selected.RuleName,
            context.Selected.VersionNumber,
            Facts = context.Facts,
        };
        return _client.PostAsync(endpoint, payload, cancellationToken);
    }
}

/// <summary>TriggerWorkflow(workflow): posts a start request to the workflow's configured webhook.</summary>
public sealed class TriggerWorkflowActionExecutor : IActionExecutor
{
    private readonly WebhookClient _client;
    private readonly WebhookOptions _options;
    private readonly ILogger<TriggerWorkflowActionExecutor> _logger;

    public TriggerWorkflowActionExecutor(WebhookClient client, IOptions<WebhookOptions> options, ILogger<TriggerWorkflowActionExecutor> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public RuleActionType ActionType => RuleActionType.TriggerWorkflow;

    public Task<ActionExecutorResult> ExecuteAsync(ActionExecutionContext context, CancellationToken cancellationToken)
    {
        var workflow = context.Selected.Action.GetString(ActionParameters.Workflow);
        var endpoint = WebhookClient.ResolveEndpoint(new Dictionary<string, string>(_options.Workflows, StringComparer.OrdinalIgnoreCase), workflow);
        if (endpoint is null)
        {
            _logger.LogWarning("TriggerWorkflow skipped: no webhook configured for workflow {Workflow}", workflow);
            return Task.FromResult(ActionExecutorResult.Skip($"No webhook is configured for workflow '{workflow}'."));
        }

        var payload = new
        {
            Type = "rule-engine.workflow-trigger",
            Workflow = workflow,
            context.ExecutionId,
            context.RuleSetId,
            context.CorrelationId,
            context.Selected.RuleId,
            context.Selected.RuleName,
            context.Selected.VersionNumber,
            Facts = context.Facts,
        };
        return _client.PostAsync(endpoint, payload, cancellationToken);
    }
}
