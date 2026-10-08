using RuleEngine.Domain.Enums;

namespace RuleEngine.Domain.Facts;

/// <summary>One field of a rule set's fact schema, addressed by a dotted path such as <c>document.quantity</c>.</summary>
public sealed class FactField
{
    public string Path { get; init; } = string.Empty;

    public FactType Type { get; init; }

    public bool Required { get; init; }

    public string? Description { get; init; }

    /// <summary>Alternative names used when mapping natural-language phrases to this field.</summary>
    public List<string> Aliases { get; init; } = new();
}
