using System.ComponentModel.DataAnnotations;

namespace RuleEngine.Api.Common;

public static class RateLimitPolicies
{
    /// <summary>Per-user fixed window applied to AI rule generation.</summary>
    public const string Generation = "generation";
}

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    [Range(1, 10_000)]
    public int GenerationPermitsPerMinute { get; set; } = 10;
}
