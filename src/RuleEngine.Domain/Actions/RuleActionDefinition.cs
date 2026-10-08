using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RuleEngine.Domain.Enums;

namespace RuleEngine.Domain.Actions;

/// <summary>
/// An action attached to a rule version, e.g.
/// <c>{ "type": "RequireApproval", "parameters": { "role": "Manager", "reason": "Large order" } }</c>.
/// </summary>
public sealed class RuleActionDefinition
{
    public string Type { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Parameters { get; set; }

    [JsonIgnore]
    public RuleActionType? ActionType => RuleActionTypes.TryParse(Type, out var type) ? type : null;

    public string? GetString(string name) =>
        Parameters is not null && Parameters.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public bool TryGetParameter(string name, out JsonElement value)
    {
        if (Parameters is not null && Parameters.TryGetValue(name, out value) && value.ValueKind != JsonValueKind.Undefined)
        {
            return true;
        }

        value = default;
        return false;
    }

    public static RuleActionDefinition SetValue(string field, JsonNode? value) =>
        Build(RuleActionType.SetValue, (ActionParameters.Field, field), (ActionParameters.Value, value));

    public static RuleActionDefinition RequireApproval(string role, string? reason = null) =>
        reason is null
            ? Build(RuleActionType.RequireApproval, (ActionParameters.Role, role))
            : Build(RuleActionType.RequireApproval, (ActionParameters.Role, role), (ActionParameters.Reason, reason));

    public static RuleActionDefinition AddTag(string tag) => Build(RuleActionType.AddTag, (ActionParameters.Tag, tag));

    public static RuleActionDefinition Reject(string reason) => Build(RuleActionType.Reject, (ActionParameters.Reason, reason));

    public static RuleActionDefinition Notify(string channel, string template) =>
        Build(RuleActionType.Notify, (ActionParameters.Channel, channel), (ActionParameters.Template, template));

    public static RuleActionDefinition TriggerWorkflow(string workflow) =>
        Build(RuleActionType.TriggerWorkflow, (ActionParameters.Workflow, workflow));

    private static RuleActionDefinition Build(RuleActionType type, params (string Key, object? Value)[] parameters)
    {
        var dictionary = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            dictionary[key] = JsonSerializer.SerializeToElement(value);
        }

        return new RuleActionDefinition { Type = type.ToString(), Parameters = dictionary };
    }
}
