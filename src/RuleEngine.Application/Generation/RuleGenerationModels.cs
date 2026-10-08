using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Application.Generation;

/// <summary>Input for rule generation: the natural-language text plus the schema it must be grounded in.</summary>
public sealed record RuleGenerationRequest(string Text, FactSchema Schema, string RuleSetName);

/// <summary>A proposed rule. Proposals are never executed or published automatically.</summary>
public sealed class GeneratedRuleProposal
{
    public string Name { get; init; } = string.Empty;

    public ConditionNode Condition { get; init; } = new();

    public List<RuleActionDefinition> Actions { get; init; } = new();

    /// <summary>Human-readable explanation of what the rule does.</summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>0..1; how much the generator had to infer.</summary>
    public double Confidence { get; init; }

    /// <summary>Interpretations the generator made that a reviewer should check.</summary>
    public List<string> Assumptions { get; init; } = new();

    public int? SuggestedPriority { get; init; }
}

public sealed class RuleGenerationResult
{
    public GeneratedRuleProposal? Proposal { get; init; }

    public List<string> Errors { get; init; } = new();

    /// <summary>"pattern" or "llm:&lt;provider&gt;".</summary>
    public string Generator { get; init; } = string.Empty;

    /// <summary>Prompt template / grammar version used to produce the proposal.</summary>
    public string? PromptVersion { get; init; }

    public string? Model { get; init; }

    public int Attempts { get; init; } = 1;

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public long? LatencyMs { get; init; }

    public bool Succeeded => Proposal is not null && Errors.Count == 0;

    public static RuleGenerationResult Failure(string generator, string? promptVersion, IEnumerable<string> errors, int attempts = 1) =>
        new() { Generator = generator, PromptVersion = promptVersion, Errors = errors.ToList(), Attempts = attempts };
}

/// <summary>Turns natural-language rule text into a proposed rule definition.</summary>
public interface IRuleGenerator
{
    Task<RuleGenerationResult> GenerateAsync(RuleGenerationRequest request, CancellationToken cancellationToken);
}
