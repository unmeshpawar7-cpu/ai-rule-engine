using System.Text.Json;
using RuleEngine.Domain.Common;

namespace RuleEngine.Domain.Conditions;

public static class ConditionSerializer
{
    public static ConditionNode Deserialize(string json) =>
        JsonSerializer.Deserialize<ConditionNode>(json, DomainJson.Options)
        ?? throw new JsonException("Condition JSON must be an object.");

    public static string Serialize(ConditionNode node) => JsonSerializer.Serialize(node, DomainJson.Options);
}
