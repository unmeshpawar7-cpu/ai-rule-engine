using System.Text.Json;
using System.Text.RegularExpressions;
using RuleEngine.Domain.Common;

namespace RuleEngine.Domain.Facts;

/// <summary>
/// Declares the facts a rule set understands. Conditions may only reference declared fields,
/// and execution input is validated against it before evaluation.
/// </summary>
public sealed class FactSchema
{
    public const int MaxFields = 200;
    public const int MaxPathLength = 200;

    private static readonly Regex PathPattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public List<FactField> Fields { get; init; } = new();

    public FactField? Find(string path) =>
        Fields.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Fields.Count == 0)
        {
            errors.Add("Fact schema must declare at least one field.");
        }

        if (Fields.Count > MaxFields)
        {
            errors.Add($"Fact schema may declare at most {MaxFields} fields.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            if (field is null)
            {
                errors.Add("Fact schema contains a null field.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(field.Path))
            {
                errors.Add("Every field needs a path.");
                continue;
            }

            if (field.Path.Length > MaxPathLength || !PathPattern.IsMatch(field.Path))
            {
                errors.Add($"Field path '{field.Path}' is invalid; use dotted identifiers such as 'document.quantity'.");
            }

            if (!seen.Add(field.Path))
            {
                errors.Add($"Field path '{field.Path}' is declared more than once.");
            }

            if (!Enum.IsDefined(field.Type))
            {
                errors.Add($"Field '{field.Path}' has an unknown type.");
            }

            if (field.Aliases is not null && field.Aliases.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 100))
            {
                errors.Add($"Field '{field.Path}' has an empty or overly long alias.");
            }
        }

        var paths = Fields.Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Path)).Select(f => f.Path).ToList();
        foreach (var path in paths)
        {
            if (paths.Any(other => other.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Field '{path}' is both a value and a parent of other fields.");
            }
        }

        return errors;
    }

    public string ToJson() => JsonSerializer.Serialize(this, DomainJson.Options);

    public static FactSchema FromJson(string json) =>
        JsonSerializer.Deserialize<FactSchema>(json, DomainJson.Options) ?? new FactSchema();
}
