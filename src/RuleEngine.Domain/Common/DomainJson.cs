using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuleEngine.Domain.Common;

/// <summary>
/// The single JSON contract used for everything the domain persists as JSON
/// (fact schemas, condition trees, action lists, traces). camelCase, enums as strings.
/// </summary>
public static class DomainJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
