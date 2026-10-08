using RuleEngine.Domain.Enums;

namespace RuleEngine.Domain.Conditions;

/// <summary>Operator metadata: parsing, value requirements and operator/type compatibility.</summary>
public static class ConditionOperators
{
    private static readonly Dictionary<string, ConditionOperator> ByName =
        Enum.GetValues<ConditionOperator>().ToDictionary(o => o.ToString(), StringComparer.OrdinalIgnoreCase);

    private static readonly FactType[] Scalars = { FactType.String, FactType.Number, FactType.Boolean, FactType.Date };
    private static readonly FactType[] Ordered = { FactType.Number, FactType.Date };
    private static readonly FactType[] TextOnly = { FactType.String };
    private static readonly FactType[] AnyType = Enum.GetValues<FactType>();

    public static IReadOnlyCollection<string> Names { get; } = Enum.GetNames<ConditionOperator>();

    /// <summary>Parses an operator name (case-insensitive). Numeric strings are rejected.</summary>
    public static bool TryParse(string? name, out ConditionOperator op)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            op = default;
            return false;
        }

        return ByName.TryGetValue(name.Trim(), out op);
    }

    public static bool RequiresValue(ConditionOperator op) =>
        op is not (ConditionOperator.IsNull or ConditionOperator.IsNotNull);

    public static bool RequiresArray(ConditionOperator op) =>
        op is ConditionOperator.In or ConditionOperator.NotIn or ConditionOperator.Between;

    public static IReadOnlyCollection<FactType> SupportedTypes(ConditionOperator op) => op switch
    {
        ConditionOperator.Equals or ConditionOperator.NotEquals => Scalars,
        ConditionOperator.GreaterThan or ConditionOperator.GreaterThanOrEqual
            or ConditionOperator.LessThan or ConditionOperator.LessThanOrEqual
            or ConditionOperator.Between => Ordered,
        ConditionOperator.Contains => new[] { FactType.String, FactType.StringList },
        ConditionOperator.StartsWith or ConditionOperator.EndsWith or ConditionOperator.Matches => TextOnly,
        ConditionOperator.In or ConditionOperator.NotIn => new[] { FactType.String, FactType.Number },
        ConditionOperator.IsNull or ConditionOperator.IsNotNull => AnyType,
        _ => Array.Empty<FactType>(),
    };

    public static bool Supports(ConditionOperator op, FactType type) => SupportedTypes(op).Contains(type);
}
