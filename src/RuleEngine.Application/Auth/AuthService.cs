using System.ComponentModel.DataAnnotations;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RuleEngine.Application.Common;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Application.Auth;

/// <summary>Credentials for <c>POST /api/auth/token</c>.</summary>
public sealed class TokenRequest
{
    public string UserName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}

public sealed record TokenResponse(string AccessToken, string TokenType, DateTime ExpiresAtUtc, IReadOnlyList<string> Roles);

public sealed record IssuedToken(string AccessToken, DateTime ExpiresAtUtc);

/// <summary>Creates signed access tokens (JWT) for authenticated users. Implemented in the API layer.</summary>
public interface ITokenIssuer
{
    IssuedToken Issue(User user);
}

public sealed class TokenRequestValidator : AbstractValidator<TokenRequest>
{
    public TokenRequestValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().MaximumLength(User.UserNameMaxLength);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
    }
}

public sealed class AuthService
{
    // Used to spend the same hashing time for unknown users as for known ones (no user-enumeration timing oracle).
    private static readonly User TimingDummy = User.Create("timing-dummy", Array.Empty<string>(), DateTime.UnixEpoch);

    private readonly IRuleEngineDbContext _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly ITokenIssuer _issuer;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IRuleEngineDbContext db, IPasswordHasher<User> hasher, ITokenIssuer issuer, ILogger<AuthService> logger)
    {
        _db = db;
        _hasher = hasher;
        _issuer = issuer;
        _logger = logger;
    }

    public async Task<TokenResponse> IssueTokenAsync(TokenRequest request, CancellationToken cancellationToken)
    {
        var userName = request.UserName.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken);
        if (user is null || !user.IsActive)
        {
            _hasher.HashPassword(TimingDummy, request.Password);
            _logger.LogWarning("Token request rejected: unknown or inactive user");
            throw new AuthenticationFailedException();
        }

        var verification = _hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("Token request rejected: wrong password for user {UserName}", user.UserName);
            throw new AuthenticationFailedException();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.SetPasswordHash(_hasher.HashPassword(user, request.Password));
            await _db.SaveChangesAsync(cancellationToken);
        }

        var token = _issuer.Issue(user);
        _logger.LogInformation("Issued access token for user {UserName}", user.UserName);
        return new TokenResponse(token.AccessToken, "Bearer", token.ExpiresAtUtc, user.GetRoles());
    }
}

/// <summary>One demo account, configured under <c>DemoUsers:Users</c> (development only).</summary>
public sealed class DemoUserOptions
{
    [Required]
    public string UserName { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    public List<string> Roles { get; set; } = new();
}

public sealed class DemoUsersOptions
{
    public const string SectionName = "DemoUsers";

    /// <summary>Seeding also requires the Development or Testing environment; see Program.cs.</summary>
    public bool Enabled { get; set; }

    public List<DemoUserOptions> Users { get; set; } = new();
}

/// <summary>Creates the configured demo users if they do not exist yet. Never runs in production.</summary>
public sealed class DemoUserSeeder
{
    private readonly IRuleEngineDbContext _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly TimeProvider _clock;
    private readonly ILogger<DemoUserSeeder> _logger;

    public DemoUserSeeder(IRuleEngineDbContext db, IPasswordHasher<User> hasher, TimeProvider clock, ILogger<DemoUserSeeder> logger)
    {
        _db = db;
        _hasher = hasher;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> SeedAsync(DemoUsersOptions options, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return 0;
        }

        var created = 0;
        foreach (var demo in options.Users.Where(u => !string.IsNullOrWhiteSpace(u.UserName) && !string.IsNullOrEmpty(u.Password)))
        {
            var userName = demo.UserName.Trim();
            if (await _db.Users.AnyAsync(u => u.UserName == userName, cancellationToken))
            {
                continue;
            }

            var user = User.Create(userName, demo.Roles, _clock.GetUtcNow().UtcDateTime);
            user.SetPasswordHash(_hasher.HashPassword(user, demo.Password));
            _db.Users.Add(user);
            created++;
        }

        if (created > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Seeded {Count} demo user(s)", created);
        }

        return created;
    }
}
