using RuleEngine.Domain.Common;
using RuleEngine.Domain.Security;

namespace RuleEngine.Domain.Entities;

/// <summary>An API user. Passwords are stored only as ASP.NET Core Identity password hashes.</summary>
public sealed class User
{
    public const int UserNameMaxLength = 100;

    private User()
    {
    }

    public Guid Id { get; private set; }

    public string UserName { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>Comma-separated role names.</summary>
    public string Roles { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public static User Create(string userName, IEnumerable<string> roles, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > UserNameMaxLength)
        {
            throw new DomainException("User name is required.");
        }

        var roleList = roles.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var unknown = roleList.Where(r => !UserRoles.IsKnown(r)).ToList();
        if (unknown.Count > 0)
        {
            throw new DomainException("Unknown role(s): " + string.Join(", ", unknown));
        }

        return new User
        {
            Id = Guid.NewGuid(),
            UserName = userName.Trim(),
            Roles = string.Join(",", roleList),
            IsActive = true,
            CreatedAtUtc = nowUtc,
        };
    }

    public IReadOnlyList<string> GetRoles() =>
        Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void SetPasswordHash(string passwordHash) => PasswordHash = passwordHash;

    public void Deactivate() => IsActive = false;
}
