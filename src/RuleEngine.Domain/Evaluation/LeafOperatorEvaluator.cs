using System.Text.Json;
using System.Text.RegularExpressions;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Domain.Evaluation;

public readonly record struct LeafOutcome(bool Result, string? Note);

/// <summary>
/// Typed semantics for leaf operators. Rules of thumb:
/// a missing / null fact only satisfies <c>IsNull</c>; values that cannot be coerced to the
/// field type never match (and say so in the trace note); strings compare ordinally unless
/// <c>ignoreCase</c> is set.
/// </summary>
public static class LeafOperatorEvaluator
{
    public static LeafOutcome Evaluate(
        ConditionOperator op,
        FactType type,
        JsonElement? actual,
        JsonElement? expected,
        bool ignoreCase,
        TimeSpan regexTimeout)
    {
        if (op == ConditionOperator.IsNull)
        {
            return new LeafOutcome(FactValueConverter.IsNull(actual), null);
        }

        if (op == ConditionOperator.IsNotNull)
        {
            return new LeafOutcome(!FactValueConverter.IsNull(actual), null);
        }

        if (FactValueConverter.IsNull(actual))
        {
            return new LeafOutcome(false, "Fact is missing or null; only IsNull can match.");
        }

        if (FactValueConverter.IsNull(expected))
        {
            return new LeafOutcome(false, "Condition has no comparison value.");
        }

        var a = actual!.Value;
        var e = expected!.Value;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        switch (op)
        {
            case ConditionOperator.Equals:
            case ConditionOperator.NotEquals:
            {
                var equal = AreEqual(type, a, e, comparison);
                if (equal is null)
                {
                    return Uncoercible(type);
                }

                return new LeafOutcome(op == ConditionOperator.Equals ? equal.Value : !equal.Value, null);
            }

            case ConditionOperator.GreaterThan:
                return Ordered(type, a, e, c => c > 0);
            case ConditionOperator.GreaterThanOrEqual:
                return Ordered(type, a, e, c => c >= 0);
            case ConditionOperator.LessThan:
                return Ordered(type, a, e, c => c < 0);
            case ConditionOperator.LessThanOrEqual:
                return Ordered(type, a, e, c => c <= 0);

            case ConditionOperator.Between:
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2)
                {
                    return new LeafOutcome(false, "Between requires a [low, high] array.");
                }

                var low = Compare(type, a, e[0]);
                var high = Compare(type, a, e[1]);
                if (low is null || high is null)
                {
                    return Uncoercible(type);
                }

                return new LeafOutcome(low.Value >= 0 && high.Value <= 0, null);
            }

            case ConditionOperator.Contains:
            {
                if (!FactValueConverter.TryGetString(e, out var needle))
                {
                    return Uncoercible(type);
                }

                if (type == FactType.StringList)
                {
                    if (!FactValueConverter.TryGetStringList(a, out var items))
                    {
                        return Uncoercible(type);
                    }

                    return new LeafOutcome(items.Any(i => string.Equals(i, needle, comparison)), null);
                }

                if (!FactValueConverter.TryGetString(a, out var haystack))
                {
                    return Uncoercible(type);
                }

                return new LeafOutcome(haystack.Contains(needle, comparison), null);
            }

            case ConditionOperator.StartsWith:
            case ConditionOperator.EndsWith:
            {
                if (!FactValueConverter.TryGetString(a, out var text) || !FactValueConverter.TryGetString(e, out var affix))
                {
                    return Uncoercible(type);
                }

                var result = op == ConditionOperator.StartsWith
                    ? text.StartsWith(affix, comparison)
                    : text.EndsWith(affix, comparison);
                return new LeafOutcome(result, null);
            }

            case ConditionOperator.In:
            case ConditionOperator.NotIn:
            {
                if (e.ValueKind != JsonValueKind.Array)
                {
                    return new LeafOutcome(false, $"{op} requires an array of values.");
                }

                if (!CanCoerce(type, a))
                {
                    return Uncoercible(type);
                }

                var member = e.EnumerateArray().Any(item => AreEqual(type, a, item, comparison) == true);
                return new LeafOutcome(op == ConditionOperator.In ? member : !member, null);
            }

            case ConditionOperator.Matches:
            {
                if (!FactValueConverter.TryGetString(a, out var input) || e.ValueKind != JsonValueKind.String)
                {
                    return Uncoercible(type);
                }

                var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                try
                {
                    return new LeafOutcome(Regex.IsMatch(input, e.GetString()!, options, regexTimeout), null);
                }
                catch (RegexMatchTimeoutException)
                {
                    return new LeafOutcome(false, "Regular expression evaluation timed out.");
                }
                catch (ArgumentException ex)
                {
                    return new LeafOutcome(false, "Invalid regular expression: " + ex.Message);
                }
            }

            default:
                return new LeafOutcome(false, $"Operator {op} is not supported.");
        }
    }

    /// <summary>Typed equality; null when either side cannot be coerced to <paramref name="type"/>.</summary>
    public static bool? AreEqual(FactType type, JsonElement a, JsonElement e, StringComparison comparison)
    {
        switch (type)
        {
            case FactType.Number:
                if (FactValueConverter.TryGetDecimal(a, out var x) && FactValueConverter.TryGetDecimal(e, out var y))
                {
                    return x == y;
                }

                return null;
            case FactType.Date:
                if (FactValueConverter.TryGetDate(a, out var d1) && FactValueConverter.TryGetDate(e, out var d2))
                {
                    return d1 == d2;
                }

                return null;
            case FactType.Boolean:
                if (FactValueConverter.TryGetBoolean(a, out var b1) && FactValueConverter.TryGetBoolean(e, out var b2))
                {
                    return b1 == b2;
                }

                return null;
            case FactType.String:
                if (FactValueConverter.TryGetString(a, out var s1) && FactValueConverter.TryGetString(e, out var s2))
                {
                    return string.Equals(s1, s2, comparison);
                }

                return null;
            case FactType.StringList:
                if (FactValueConverter.TryGetStringList(a, out var l1) && FactValueConverter.TryGetStringList(e, out var l2))
                {
                    return l1.Count == l2.Count && l1.Zip(l2).All(p => string.Equals(p.First, p.Second, comparison));
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>Typed ordering for Number and Date; null when not comparable.</summary>
    public static int? Compare(FactType type, JsonElement a, JsonElement e)
    {
        switch (type)
        {
            case FactType.Number:
                if (FactValueConverter.TryGetDecimal(a, out var x) && FactValueConverter.TryGetDecimal(e, out var y))
                {
                    return x.CompareTo(y);
                }

                return null;
            case FactType.Date:
                if (FactValueConverter.TryGetDate(a, out var d1) && FactValueConverter.TryGetDate(e, out var d2))
                {
                    return d1.CompareTo(d2);
                }

                return null;
            default:
                return null;
        }
    }

    private static bool CanCoerce(FactType type, JsonElement value) => type switch
    {
        FactType.Number => FactValueConverter.TryGetDecimal(value, out _),
        FactType.Date => FactValueConverter.TryGetDate(value, out _),
        FactType.Boolean => FactValueConverter.TryGetBoolean(value, out _),
        FactType.StringList => FactValueConverter.TryGetStringList(value, out _),
        _ => FactValueConverter.TryGetString(value, out _),
    };

    private static LeafOutcome Ordered(FactType type, JsonElement a, JsonElement e, Func<int, bool> predicate)
    {
        var comparison = Compare(type, a, e);
        return comparison is null ? Uncoercible(type) : new LeafOutcome(predicate(comparison.Value), null);
    }

    private static LeafOutcome Uncoercible(FactType type) =>
        new(false, $"Value could not be interpreted as {type}.");
}
