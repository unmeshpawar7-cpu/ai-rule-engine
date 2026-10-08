using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuleEngine.Domain.Enums;

namespace RuleEngine.Domain.Facts;

/// <summary>
/// Typed coercion of JSON values. Numbers are compared as <see cref="decimal"/>, dates as UTC
/// <see cref="DateTime"/>; numeric and boolean strings are accepted ("12", "true").
/// </summary>
public static class FactValueConverter
{
    public static JsonElement? ToElement(JsonNode? node) =>
        node is null ? null : JsonSerializer.SerializeToElement(node);

    public static bool IsNull(JsonElement? element) =>
        element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    public static bool TryGetDecimal(JsonElement element, out decimal value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDecimal(out value);
            case JsonValueKind.String:
                return decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
            default:
                value = 0m;
                return false;
        }
    }

    public static bool TryGetDate(JsonElement element, out DateTime value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!DateTime.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return false;
        }

        value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    public static bool TryGetBoolean(JsonElement element, out bool value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
                return bool.TryParse(element.GetString(), out value);
            default:
                value = false;
                return false;
        }
    }

    public static bool TryGetString(JsonElement element, out string value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = element.GetRawText();
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    public static bool TryGetStringList(JsonElement element, out List<string> values)
    {
        values = new List<string>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (!TryGetString(item, out var text))
            {
                return false;
            }

            values.Add(text);
        }

        return true;
    }

    /// <summary>Strict compatibility used for input validation and authoring-time value checks.</summary>
    public static bool IsCompatible(FactType type, JsonElement element) => type switch
    {
        FactType.Number => TryGetDecimal(element, out _),
        FactType.Date => TryGetDate(element, out _),
        FactType.Boolean => TryGetBoolean(element, out _),
        FactType.String => element.ValueKind == JsonValueKind.String,
        FactType.StringList => element.ValueKind == JsonValueKind.Array
            && element.EnumerateArray().All(i => i.ValueKind == JsonValueKind.String),
        _ => false,
    };
}
