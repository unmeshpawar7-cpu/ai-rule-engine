using System.Reflection;
using System.Text;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Infrastructure.Ai;

/// <summary>A versioned prompt template embedded in the assembly (Ai/Prompts/*.md).</summary>
public sealed record PromptTemplate(string Version, string Text);

public static class PromptLibrary
{
    /// <summary>Stored on every AI-generated rule version as <c>PromptVersion</c>.</summary>
    public const string RuleGenerationVersion = "rule-generation/v1";

    private const string RuleGenerationResource = "RuleEngine.Prompts.rule-generation-v1.md";

    private static readonly Lazy<PromptTemplate> RuleGenerationTemplate = new(() => new PromptTemplate(RuleGenerationVersion, Load(RuleGenerationResource)));

    public static PromptTemplate RuleGeneration => RuleGenerationTemplate.Value;

    /// <summary>Renders the system prompt for a rule set: schema, operators and action catalogue.</summary>
    public static string RenderRuleGeneration(PromptTemplate template, FactSchema schema, string ruleSetName)
    {
        var fields = new StringBuilder();
        foreach (var field in schema.Fields.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            fields.Append("- ").Append(field.Path).Append(" (").Append(field.Type).Append(field.Required ? ", required" : string.Empty).Append(')');
            if (field.Aliases.Count > 0)
            {
                fields.Append(" aliases: ").Append(string.Join(", ", field.Aliases));
            }

            if (!string.IsNullOrWhiteSpace(field.Description))
            {
                fields.Append(" -- ").Append(field.Description);
            }

            fields.AppendLine();
        }

        var operators = new StringBuilder();
        foreach (var op in Enum.GetValues<ConditionOperator>())
        {
            operators.Append("- ").Append(op).Append(": ").AppendLine(string.Join(", ", ConditionOperators.SupportedTypes(op)));
        }

        const string actions =
            "- SetValue {\"field\": \"<schema path>\", \"value\": <value of the field's type>}\n" +
            "- RequireApproval {\"role\": \"<approver role>\", \"reason\": \"<optional>\"}\n" +
            "- AddTag {\"tag\": \"<letters, digits, _ . : ->\"}\n" +
            "- Reject {\"reason\": \"<why>\"}\n" +
            "- Notify {\"channel\": \"<channel name>\", \"template\": \"<template name>\"}\n" +
            "- TriggerWorkflow {\"workflow\": \"<workflow name>\"}";

        return template.Text
            .Replace("{{RULE_SET_NAME}}", ruleSetName, StringComparison.Ordinal)
            .Replace("{{FACT_SCHEMA}}", fields.ToString().TrimEnd(), StringComparison.Ordinal)
            .Replace("{{OPERATORS}}", operators.ToString().TrimEnd(), StringComparison.Ordinal)
            .Replace("{{ACTIONS}}", actions, StringComparison.Ordinal);
    }

    private static string Load(string resourceName)
    {
        var assembly = typeof(PromptLibrary).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
