using System.Text;
using System.Text.RegularExpressions;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Application.Generation;

/// <summary>
/// Maps a natural-language phrase ("quantity", "supplier risk rating", "the document type") to a
/// declared fact field using normalised path, aliases and the last path segment. Never invents fields.
/// </summary>
public static class FieldResolver
{
    private static readonly Regex LeadingNoise = new(
        "^(?:(?:the|a|an|its|their|his|her|our|any|all|every|each)\\s+)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public sealed record Resolution(FactField? Field, string MatchKind, string? Error);

    public static Resolution Resolve(string phrase, FactSchema schema)
    {
        var cleaned = LeadingNoise.Replace(phrase.Trim().Replace("'s ", " ", StringComparison.OrdinalIgnoreCase), string.Empty);
        var key = Normalize(cleaned);
        if (key.Length == 0)
        {
            return new Resolution(null, "none", $"Missing field name before the comparison.");
        }

        var byPath = schema.Fields.Where(f => Normalize(f.Path) == key).ToList();
        if (byPath.Count == 1)
        {
            return new Resolution(byPath[0], "path", null);
        }

        var byAlias = schema.Fields.Where(f => f.Aliases.Any(a => Normalize(a) == key)).ToList();
        if (byAlias.Count == 1)
        {
            return new Resolution(byAlias[0], "alias", null);
        }

        var byName = schema.Fields.Where(f => Normalize(LastSegment(f.Path)) == key).ToList();
        if (byName.Count == 1)
        {
            return new Resolution(byName[0], "name", null);
        }

        if (byAlias.Count > 1 || byName.Count > 1)
        {
            var candidates = byAlias.Concat(byName).Select(f => f.Path).Distinct(StringComparer.Ordinal);
            return new Resolution(null, "ambiguous", $"'{cleaned}' is ambiguous; it could mean {string.Join(" or ", candidates)}.");
        }

        return new Resolution(
            null,
            "unknown",
            $"Unknown field '{cleaned}'. Known fields: {string.Join(", ", schema.Fields.Select(f => f.Path))}.");
    }

    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static string LastSegment(string path)
    {
        var index = path.LastIndexOf('.');
        return index < 0 ? path : path[(index + 1)..];
    }
}
