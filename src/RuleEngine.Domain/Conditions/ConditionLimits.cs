namespace RuleEngine.Domain.Conditions;

/// <summary>Structural measurements of a condition tree used to enforce depth / size limits.</summary>
public static class ConditionLimits
{
    public static (int Depth, int Nodes) Measure(ConditionNode node)
    {
        var nodes = 0;
        var depth = Measure(node, 1, ref nodes);
        return (depth, nodes);
    }

    /// <summary>All field paths referenced by leaves of the tree.</summary>
    public static IReadOnlyCollection<string> CollectFields(ConditionNode node)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        Collect(node, fields);
        return fields;
    }

    private static int Measure(ConditionNode node, int level, ref int nodes)
    {
        nodes++;
        var max = level;
        foreach (var child in node.GetChildren())
        {
            max = Math.Max(max, Measure(child, level + 1, ref nodes));
        }

        return max;
    }

    private static void Collect(ConditionNode node, HashSet<string> fields)
    {
        if (node.Field is not null)
        {
            fields.Add(node.Field);
        }

        foreach (var child in node.GetChildren())
        {
            Collect(child, fields);
        }
    }
}
