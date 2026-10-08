using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuleEngine.Application.Generation;
using RuleEngine.Application.Validation;
using RuleEngine.Domain.Common;

namespace RuleEngine.Infrastructure.Ai;

/// <summary>
/// LLM-backed fallback generator. The model only returns JSON; the JSON is parsed defensively and run through
/// the same <see cref="RuleDefinitionValidator"/> as hand-written rules (unknown fields, operators, action types
/// and malformed values are rejected — the main hallucination guard). On validation failure the errors are fed
/// back for one bounded retry. Nothing produced here is executed or published.
/// </summary>
public sealed class LlmRuleGenerator : IFallbackRuleGenerator
{
    private const int MaxNameLength = 120;
    private const int MaxListItems = 10;

    private readonly ILlmClient _client;
    private readonly RuleDefinitionValidator _validator;
    private readonly LlmOptions _options;
    private readonly ILogger<LlmRuleGenerator> _logger;

    public LlmRuleGenerator(ILlmClient client, RuleDefinitionValidator validator, IOptions<LlmOptions> options, ILogger<LlmRuleGenerator> logger)
    {
        _client = client;
        _validator = validator;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "llm:" + _client.Provider;

    public async Task<RuleGenerationResult> GenerateAsync(RuleGenerationRequest request, CancellationToken cancellationToken)
    {
        var template = PromptLibrary.RuleGeneration;
        var systemPrompt = PromptLibrary.RenderRuleGeneration(template, request.Schema, request.RuleSetName);
        var messages = new List<LlmMessage> { new("user", request.Text) };
        var maxAttempts = Math.Max(1, _options.MaxAttempts);
        var stopwatch = Stopwatch.StartNew();
        var inputTokens = 0;
        var outputTokens = 0;
        var errors = new List<string>();
        var attempt = 0;

        while (attempt < maxAttempts)
        {
            attempt++;
            LlmCompletion completion;
            try
            {
                completion = await _client.CompleteAsync(new LlmRequest(systemPrompt, messages, _options.MaxOutputTokens), cancellationToken);
            }
            catch (LlmProviderException ex)
            {
                return Finish(Failure(new[] { ex.Message }, attempt), stopwatch, inputTokens, outputTokens, template.Version);
            }
            catch (HttpRequestException)
            {
                return Finish(Failure(new[] { $"The {_client.Provider} API could not be reached." }, attempt), stopwatch, inputTokens, outputTokens, template.Version);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Finish(Failure(new[] { $"The {_client.Provider} API did not answer within {_options.TimeoutSeconds} s." }, attempt), stopwatch, inputTokens, outputTokens, template.Version);
            }

            // Usage is reported by both providers; fall back to a ~4 chars/token estimate when absent.
            inputTokens += completion.InputTokens ?? EstimateTokens(systemPrompt) + messages.Sum(m => EstimateTokens(m.Content));
            outputTokens += completion.OutputTokens ?? EstimateTokens(completion.Content);

            var parsed = Parse(completion.Content, request, out errors, out var refused);
            if (parsed is not null)
            {
                var success = new RuleGenerationResult { Proposal = parsed, Attempts = attempt };
                return Finish(success, stopwatch, inputTokens, outputTokens, template.Version);
            }

            if (refused)
            {
                break;
            }

            messages.Add(new LlmMessage("assistant", completion.Content));
            messages.Add(new LlmMessage(
                "user",
                "Your previous JSON was rejected by the validator:\n- " + string.Join("\n- ", errors) +
                "\nReturn a corrected JSON object only, using only the fields, operators and action types listed."));
        }

        return Finish(Failure(errors, attempt), stopwatch, inputTokens, outputTokens, template.Version);
    }

    private GeneratedRuleProposal? Parse(string content, RuleGenerationRequest request, out List<string> errors, out bool refused)
    {
        errors = new List<string>();
        refused = false;
        var json = ExtractJsonObject(content);
        if (json is null)
        {
            errors.Add("The model response did not contain a JSON object.");
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            errors.Add("The model response was not valid JSON.");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("The model response must be a JSON object.");
                return null;
            }

            if (root.TryGetProperty("error", out var refusal) && refusal.ValueKind == JsonValueKind.String)
            {
                refused = true;
                errors.Add("The model could not express the rule with this fact schema: " + Text.Truncate(refusal.GetString(), 300));
                return null;
            }

            if (!root.TryGetProperty("condition", out var condition) || !root.TryGetProperty("actions", out var actions))
            {
                errors.Add("The JSON must contain 'condition' and 'actions'.");
                return null;
            }

            var validation = _validator.Validate(request.Schema, condition, actions);
            if (!validation.IsValid)
            {
                errors.AddRange(validation.Errors.Select(e => $"{e.Path}: {e.Message}"));
                return null;
            }

            var confidence = root.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.ValueKind == JsonValueKind.Number && confidenceElement.TryGetDouble(out var value)
                ? Math.Clamp(value, 0d, 1d)
                : 0.5d;
            var assumptions = new List<string>();
            if (root.TryGetProperty("assumptions", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                assumptions.AddRange(list.EnumerateArray()
                    .Where(i => i.ValueKind == JsonValueKind.String)
                    .Select(i => Text.Truncate(i.GetString(), 300)!)
                    .Where(i => i.Length > 0)
                    .Take(MaxListItems));
            }

            assumptions.Add("Generated by a language model; review the condition and actions before publishing.");
            var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(nameElement.GetString())
                ? Text.Truncate(nameElement.GetString()!.Trim(), MaxNameLength)!
                : PatternRuleGenerator.Describe(validation.Condition!);
            var explanation = root.TryGetProperty("explanation", out var explanationElement) && explanationElement.ValueKind == JsonValueKind.String
                ? Text.Truncate(explanationElement.GetString(), 1000) ?? string.Empty
                : string.Empty;

            return new GeneratedRuleProposal
            {
                Name = name,
                Condition = validation.Condition!,
                Actions = validation.Actions!,
                Explanation = explanation,
                Confidence = confidence,
                Assumptions = assumptions,
            };
        }
    }

    /// <summary>Accepts bare JSON or JSON wrapped in prose / markdown fences: takes the outermost {...}.</summary>
    internal static string? ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : null;
    }

    private static int EstimateTokens(string text) => (int)Math.Ceiling(Encoding.UTF8.GetByteCount(text) / 4d);

    private RuleGenerationResult Failure(IEnumerable<string> errors, int attempts) =>
        new() { Errors = errors.ToList(), Attempts = attempts };

    private RuleGenerationResult Finish(RuleGenerationResult result, Stopwatch stopwatch, int inputTokens, int outputTokens, string promptVersion)
    {
        stopwatch.Stop();
        var final = new RuleGenerationResult
        {
            Proposal = result.Proposal,
            Errors = result.Errors,
            Generator = Name,
            PromptVersion = promptVersion,
            Model = _client.Model,
            Attempts = result.Attempts,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            LatencyMs = stopwatch.ElapsedMilliseconds,
        };

        // Metadata only: never the prompt, the user's text, the model output or API keys.
        _logger.LogInformation(
            "LLM rule generation {Outcome}: provider {Provider}, model {Model}, prompt {PromptVersion}, attempts {Attempts}, latency {LatencyMs} ms, tokens in {InputTokens} / out {OutputTokens}",
            final.Succeeded ? "succeeded" : "failed",
            _client.Provider,
            _client.Model,
            promptVersion,
            final.Attempts,
            final.LatencyMs,
            inputTokens,
            outputTokens);
        return final;
    }
}
