using Microsoft.Extensions.Logging;

namespace RuleEngine.Application.Generation;

/// <summary>A generator consulted only when the deterministic grammar cannot interpret the text (e.g. an LLM).</summary>
public interface IFallbackRuleGenerator : IRuleGenerator
{
    /// <summary>Generator identifier, e.g. <c>llm:openai</c>.</summary>
    string Name { get; }
}

/// <summary>
/// Deterministic-first generation: the <see cref="PatternRuleGenerator"/> is always tried first because it is
/// free, instant and reproducible. Only when it cannot interpret the text are the configured fallbacks
/// (LLM providers) consulted. With <c>LLM_PROVIDER=offline</c> there are no fallbacks.
/// </summary>
public sealed class CompositeRuleGenerator : IRuleGenerator
{
    private readonly PatternRuleGenerator _pattern;
    private readonly IReadOnlyList<IFallbackRuleGenerator> _fallbacks;
    private readonly ILogger<CompositeRuleGenerator> _logger;

    public CompositeRuleGenerator(
        PatternRuleGenerator pattern,
        IEnumerable<IFallbackRuleGenerator> fallbacks,
        ILogger<CompositeRuleGenerator> logger)
    {
        _pattern = pattern;
        _fallbacks = fallbacks.ToList();
        _logger = logger;
    }

    public async Task<RuleGenerationResult> GenerateAsync(RuleGenerationRequest request, CancellationToken cancellationToken)
    {
        var patternResult = await _pattern.GenerateAsync(request, cancellationToken);
        if (patternResult.Succeeded || _fallbacks.Count == 0)
        {
            return patternResult;
        }

        var errors = patternResult.Errors.Select(e => "pattern: " + e).ToList();
        foreach (var fallback in _fallbacks)
        {
            _logger.LogInformation("Pattern grammar could not interpret the rule text; falling back to {Generator}", fallback.Name);
            var result = await fallback.GenerateAsync(request, cancellationToken);
            if (result.Succeeded)
            {
                return result;
            }

            errors.AddRange(result.Errors.Select(e => $"{result.Generator}: {e}"));
            patternResult = result;
        }

        return new RuleGenerationResult
        {
            Proposal = null,
            Errors = errors,
            Generator = patternResult.Generator,
            PromptVersion = patternResult.PromptVersion,
            Model = patternResult.Model,
            Attempts = patternResult.Attempts,
            InputTokens = patternResult.InputTokens,
            OutputTokens = patternResult.OutputTokens,
            LatencyMs = patternResult.LatencyMs,
        };
    }
}
