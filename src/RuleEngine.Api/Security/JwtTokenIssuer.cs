using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RuleEngine.Application.Auth;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Api.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Claim types written to and read from tokens (inbound claim mapping is disabled).</summary>
    public const string NameClaim = "name";
    public const string RoleClaim = "role";

    [Required]
    public string Issuer { get; set; } = "ai-rule-engine";

    [Required]
    public string Audience { get; set; } = "ai-rule-engine-clients";

    /// <summary>HMAC-SHA256 key; at least 32 characters. Supply via the Jwt__SigningKey environment variable.</summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 60;

    public SymmetricSecurityKey CreateKey() => new(Encoding.UTF8.GetBytes(SigningKey));
}

public sealed class JwtTokenIssuer : ITokenIssuer
{
    private readonly JwtOptions _options;
    private readonly TimeProvider _clock;

    public JwtTokenIssuer(IOptions<JwtOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public IssuedToken Issue(User user)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtOptions.NameClaim, user.UserName),
        };
        claims.AddRange(user.GetRoles().Select(role => new Claim(JwtOptions.RoleClaim, role)));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(_options.CreateKey(), SecurityAlgorithms.HmacSha256));
        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
