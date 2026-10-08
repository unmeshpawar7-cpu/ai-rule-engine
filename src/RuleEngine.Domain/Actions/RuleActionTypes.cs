using RuleEngine.Domain.Enums;

namespace RuleEngine.Domain.Actions;

public static class RuleActionTypes
{
    private static readonly Dictionary<string, RuleActionType> ByName =
        Enum.GetValues<RuleActionType>().ToDictionary(t => t.ToString(), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Names { get; } = Enum.GetNames<RuleActionType>();

    /// <summary>Parses an action type name (case-insensitive). Numeric strings are rejected.</summary>
    public static bool TryParse(string? name, out RuleActionType type)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            type = default;
            return false;
        }

        return ByName.TryGetValue(name.Trim(), out type);
    }
}
