using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RuleEngine.Domain.Enums;

namespace RuleEngine.Domain.Conditions;

public enum ConditionNodeKind
{
    Invalid,
    All,
    Any,
    Not,
    Leaf,
}

/// <summary>
/// A node of the JSON condition tree. Exactly one of <see cref="All"/>, <see cref="Any"/>,
/// <see cref="Not"/> or <see cref="Field"/> is set:
/// <code>
/// { "all": [ { "field": "document.quantity", "operator": "GreaterThan", "value": 10 },
///            { "not": { "field": "department", "operator": "Equals", "value": "IT" } } ] }
/// </code>
/// </summary>
public sealed class ConditionNode
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ConditionNode>? All { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ConditionNode>? Any { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConditionNode? Not { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Field { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operator { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Value { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IgnoreCase { get; set; }

    [JsonIgnore]
    public ConditionNodeKind Kind
    {
        get
        {
            var count = (All is not null ? 1 : 0) + (Any is not null ? 1 : 0) + (Not is not null ? 1 : 0) + (Field is not null ? 1 : 0);
            if (count != 1)
            {
                return ConditionNodeKind.Invalid;
            }

            if (All is not null)
            {
                return ConditionNodeKind.All;
            }

            if (Any is not null)
            {
                return ConditionNodeKind.Any;
            }

            return Not is not null ? ConditionNodeKind.Not : ConditionNodeKind.Leaf;
        }
    }

    /// <summary>Child nodes of a composite node (empty for leaves).</summary>
    public IReadOnlyList<ConditionNode> GetChildren()
    {
        if (All is not null)
        {
            return All.Where(c => c is not null).ToList();
        }

        if (Any is not null)
        {
            return Any.Where(c => c is not null).ToList();
        }

        return Not is not null ? new[] { Not } : Array.Empty<ConditionNode>();
    }

    public static ConditionNode Leaf(string field, ConditionOperator op, JsonNode? value = null, bool? ignoreCase = null) =>
        new() { Field = field, Operator = op.ToString(), Value = value, IgnoreCase = ignoreCase };

    public static ConditionNode AllOf(params ConditionNode[] children) => new() { All = children.ToList() };

    public static ConditionNode AnyOf(params ConditionNode[] children) => new() { Any = children.ToList() };

    public static ConditionNode Negate(ConditionNode child) => new() { Not = child };
}
