using System.Text.Json;
using RuleEngine.Domain.Common;

namespace RuleEngine.Domain.Actions;

public static class RuleActionSerializer
{
    public static List<RuleActionDefinition> DeserializeList(string json) =>
        JsonSerializer.Deserialize<List<RuleActionDefinition>>(json, DomainJson.Options)
        ?? throw new JsonException("Actions JSON must be an array.");

    public static string Serialize(IEnumerable<RuleActionDefinition> actions) =>
        JsonSerializer.Serialize(actions.ToList(), DomainJson.Options);
}
