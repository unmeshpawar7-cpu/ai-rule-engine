namespace RuleEngine.Domain.Common;

public static class Text
{
    /// <summary>Truncates <paramref name="value"/> to <paramref name="maxLength"/> characters, appending an ellipsis marker.</summary>
    public static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        const string marker = "...";
        return maxLength <= marker.Length ? value[..maxLength] : value[..(maxLength - marker.Length)] + marker;
    }
}
