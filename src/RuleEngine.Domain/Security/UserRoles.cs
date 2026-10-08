namespace RuleEngine.Domain.Security;

/// <summary>Role names used in JWT role claims and authorization attributes.</summary>
public static class UserRoles
{
    public const string RuleAdmin = "RuleAdmin";
    public const string RuleAuthor = "RuleAuthor";
    public const string Executor = "Executor";
    public const string Auditor = "Auditor";

    public const string AdminOrAuthor = RuleAdmin + "," + RuleAuthor;
    public const string AdminOrExecutor = RuleAdmin + "," + Executor;
    public const string AdminOrAuditor = RuleAdmin + "," + Auditor;
    public const string Simulators = RuleAdmin + "," + RuleAuthor + "," + Executor;
    public const string ExecutionReaders = RuleAdmin + "," + Executor + "," + Auditor;

    public static IReadOnlyList<string> All { get; } = new[] { RuleAdmin, RuleAuthor, Executor, Auditor };

    public static bool IsKnown(string role) => All.Contains(role, StringComparer.Ordinal);
}
