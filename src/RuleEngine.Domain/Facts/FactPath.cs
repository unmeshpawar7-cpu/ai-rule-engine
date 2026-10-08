using System.Text.Json.Nodes;

namespace RuleEngine.Domain.Facts;

/// <summary>Reads and writes values in a JSON fact document using dotted paths.</summary>
public static class FactPath
{
    public static bool TryGet(JsonObject root, string path, out JsonNode? value)
    {
        value = null;
        JsonNode? current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj)
            {
                return false;
            }

            var key = FindKey(obj, segment);
            if (key is null)
            {
                return false;
            }

            current = obj[key];
        }

        value = current;
        return true;
    }

    public static void Set(JsonObject root, string path, JsonNode? value)
    {
        var segments = path.Split('.');
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var key = FindKey(current, segments[i]) ?? segments[i];
            if (current[key] is JsonObject child)
            {
                current = child;
            }
            else
            {
                var created = new JsonObject();
                current[key] = created;
                current = created;
            }
        }

        var leafKey = FindKey(current, segments[^1]) ?? segments[^1];
        current[leafKey] = value;
    }

    private static string? FindKey(JsonObject obj, string name)
    {
        if (obj.ContainsKey(name))
        {
            return name;
        }

        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Key;
            }
        }

        return null;
    }
}
