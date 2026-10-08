using System.Globalization;
using System.Text.Json;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Application.Validation;

/// <summary>A rule (or candidate rule) as seen by the conflict detector.</summary>
public sealed record RuleCandidate(
    Guid? RuleId,
    string Name,
    int Priority,
    ConditionNode Condition,
    IReadOnlyList<RuleActionDefinition> Actions);

/// <summary>
/// Heuristic static analysis that warns when a candidate rule can match the same facts as an
/// active rule at the same priority while prescribing contradictory actions (Reject vs
/// RequireApproval, or SetValue of one field to different values).
/// <para>
/// Only conjunctive conditions (leaves and nested <c>all</c> groups) are analysed. For each field
/// the numeric/date ranges (comparison operators, Equals, Between) and the allowed value sets
/// (Equals/In on strings and booleans) are intersected; two rules may overlap unless some
/// shared field has disjoint constraints. Conditions using <c>any</c>/<c>not</c> are skipped, so
/// the detector can miss conflicts but does not invent them for analysed rules.
/// </para>
/// </summary>
public sealed class ConflictDetector
{
    public IReadOnlyList<ValidationIssue> Detect(RuleCandidate candidate, IEnumerable<RuleCandidate> existing, FactSchema schema)
    {
        var issues = new List<ValidationIssue>();
        var candidateConstraints = BuildConstraints(candidate.Condition, schema);
        if (candidateConstraints is null || candidateConstraints.Values.Any(c => c.IsEmpty))
        {
            return issues;
        }

        foreach (var other in existing)
        {
            if (candidate.RuleId is not null && other.RuleId == candidate.RuleId)
            {
                continue;
            }

            if (other.Priority != candidate.Priority)
            {
                continue;
            }

            var otherConstraints = BuildConstraints(other.Condition, schema);
            if (otherConstraints is null || !MayOverlap(candidateConstraints, otherConstraints))
            {
                continue;
            }

            var contradiction = FindContradiction(candidate.Actions, other.Actions);
            if (contradiction is not null)
            {
                issues.Add(new ValidationIssue(
                    ValidationCodes.Conflict,
                    "condition",
                    $"Conditions overlap with active rule '{other.Name}' at the same priority ({other.Priority}) and {contradiction}. Give one rule a different priority or make the conditions disjoint."));
            }
        }

        return issues;
    }

    internal static Dictionary<string, FieldConstraint>? BuildConstraints(ConditionNode condition, FactSchema schema)
    {
        var leaves = new List<ConditionNode>();
        if (!CollectConjunctiveLeaves(condition, leaves))
        {
            return null;
        }

        var constraints = new Dictionary<string, FieldConstraint>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            var field = schema.Find(leaf.Field!);
            if (field is null || !ConditionOperators.TryParse(leaf.Operator, out var op))
            {
                continue;
            }

            if (!constraints.TryGetValue(field.Path, out var constraint))
            {
                constraint = new FieldConstraint();
                constraints[field.Path] = constraint;
            }

            constraint.Apply(op, field.Type, FactValueConverter.ToElement(leaf.Value));
        }

        return constraints;
    }

    private static bool CollectConjunctiveLeaves(ConditionNode node, List<ConditionNode> leaves)
    {
        switch (node.Kind)
        {
            case ConditionNodeKind.Leaf:
                leaves.Add(node);
                return true;
            case ConditionNodeKind.All:
                return node.GetChildren().All(child => CollectConjunctiveLeaves(child, leaves));
            default:
                return false;
        }
    }

    private static bool MayOverlap(Dictionary<string, FieldConstraint> a, Dictionary<string, FieldConstraint> b)
    {
        if (b.Values.Any(c => c.IsEmpty))
        {
            return false;
        }

        foreach (var (field, constraint) in a)
        {
            if (b.TryGetValue(field, out var other) && !constraint.Intersects(other))
            {
                return false;
            }
        }

        return true;
    }

    private static string? FindContradiction(IReadOnlyList<RuleActionDefinition> a, IReadOnlyList<RuleActionDefinition> b)
    {
        static bool Has(IReadOnlyList<RuleActionDefinition> actions, RuleActionType type) =>
            actions.Any(x => x.ActionType == type);

        if ((Has(a, RuleActionType.Reject) && Has(b, RuleActionType.RequireApproval))
            || (Has(b, RuleActionType.Reject) && Has(a, RuleActionType.RequireApproval)))
        {
            return "one rule rejects while the other routes for approval";
        }

        foreach (var setA in a.Where(x => x.ActionType == RuleActionType.SetValue))
        {
            var field = setA.GetString(ActionParameters.Field);
            var valueA = setA.TryGetParameter(ActionParameters.Value, out var va) ? va.GetRawText() : "null";
            foreach (var setB in b.Where(x => x.ActionType == RuleActionType.SetValue && x.GetString(ActionParameters.Field) == field))
            {
                var valueB = setB.TryGetParameter(ActionParameters.Value, out var vb) ? vb.GetRawText() : "null";
                if (!string.Equals(valueA, valueB, StringComparison.Ordinal))
                {
                    return $"both set '{field}' to different values ({valueA} vs {valueB})";
                }
            }
        }

        return null;
    }

    /// <summary>Accumulated constraint for one field within one conjunctive condition.</summary>
    internal sealed class FieldConstraint
    {
        public decimal? Low { get; private set; }

        public bool LowInclusive { get; private set; } = true;

        public decimal? High { get; private set; }

        public bool HighInclusive { get; private set; } = true;

        public HashSet<string>? Allowed { get; private set; }

        public bool IsEmpty =>
            (Allowed is not null && Allowed.Count == 0)
            || (Low is not null && High is not null && (Low > High || (Low == High && !(LowInclusive && HighInclusive))));

        public void Apply(ConditionOperator op, FactType type, JsonElement? value)
        {
            if (value is null)
            {
                return;
            }

            var element = value.Value;
            if (type is FactType.Number or FactType.Date)
            {
                switch (op)
                {
                    case ConditionOperator.GreaterThan when TryNumeric(type, element, out var gt):
                        RaiseLow(gt, inclusive: false);
                        break;
                    case ConditionOperator.GreaterThanOrEqual when TryNumeric(type, element, out var gte):
                        RaiseLow(gte, inclusive: true);
                        break;
                    case ConditionOperator.LessThan when TryNumeric(type, element, out var lt):
                        LowerHigh(lt, inclusive: false);
                        break;
                    case ConditionOperator.LessThanOrEqual when TryNumeric(type, element, out var lte):
                        LowerHigh(lte, inclusive: true);
                        break;
                    case ConditionOperator.Equals when TryNumeric(type, element, out var eq):
                        RaiseLow(eq, inclusive: true);
                        LowerHigh(eq, inclusive: true);
                        break;
                    case ConditionOperator.Between when element.ValueKind == JsonValueKind.Array
                                                        && element.GetArrayLength() == 2
                                                        && TryNumeric(type, element[0], out var low)
                                                        && TryNumeric(type, element[1], out var high):
                        RaiseLow(low, inclusive: true);
                        LowerHigh(high, inclusive: true);
                        break;
                }

                return;
            }

            if (type is FactType.String or FactType.Boolean)
            {
                if (op == ConditionOperator.Equals && FactValueConverter.TryGetString(element, out var single))
                {
                    Restrict(new[] { single });
                }
                else if (op == ConditionOperator.In && element.ValueKind == JsonValueKind.Array)
                {
                    var items = new List<string>();
                    foreach (var item in element.EnumerateArray())
                    {
                        if (FactValueConverter.TryGetString(item, out var text))
                        {
                            items.Add(text);
                        }
                    }

                    Restrict(items);
                }
            }
        }

        public bool Intersects(FieldConstraint other)
        {
            if (Allowed is not null && other.Allowed is not null && !Allowed.Overlaps(other.Allowed))
            {
                return false;
            }

            var merged = new FieldConstraint
            {
                Low = Low,
                LowInclusive = LowInclusive,
                High = High,
                HighInclusive = HighInclusive,
            };
            if (other.Low is not null)
            {
                merged.RaiseLow(other.Low.Value, other.LowInclusive);
            }

            if (other.High is not null)
            {
                merged.LowerHigh(other.High.Value, other.HighInclusive);
            }

            return !merged.IsEmpty;
        }

        private static bool TryNumeric(FactType type, JsonElement element, out decimal value)
        {
            if (type == FactType.Date)
            {
                if (FactValueConverter.TryGetDate(element, out var date))
                {
                    value = date.Ticks;
                    return true;
                }

                value = 0;
                return false;
            }

            return FactValueConverter.TryGetDecimal(element, out value);
        }

        private void RaiseLow(decimal candidate, bool inclusive)
        {
            if (Low is null || candidate > Low || (candidate == Low && !inclusive))
            {
                Low = candidate;
                LowInclusive = inclusive;
            }
        }

        private void LowerHigh(decimal candidate, bool inclusive)
        {
            if (High is null || candidate < High || (candidate == High && !inclusive))
            {
                High = candidate;
                HighInclusive = inclusive;
            }
        }

        private void Restrict(IEnumerable<string> values)
        {
            // Case is folded so that the detector errs on the side of reporting a possible overlap.
            var set = new HashSet<string>(values.Select(v => v.ToLower(CultureInfo.InvariantCulture)), StringComparer.Ordinal);
            if (Allowed is null)
            {
                Allowed = set;
            }
            else
            {
                Allowed.IntersectWith(set);
            }
        }
    }
}
