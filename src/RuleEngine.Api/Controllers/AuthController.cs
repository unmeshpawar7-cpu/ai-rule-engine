using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RuleEngine.Api.Common;
using RuleEngine.Application.Auth;

namespace RuleEngine.Api.Controllers;

/// <summary>Authentication.</summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthService _auth;

    public AuthController(AuthService auth)
    {
        _auth = auth;
    }

    /// <summary>Exchanges user name and password for a JWT access token.</summary>
    /// <remarks>
    /// Demo users (RuleAdmin, RuleAuthor, Executor, Auditor) are seeded from configuration in the Development
    /// environment only. Example: <c>{ "userName": "admin", "password": "..." }</c>.
    /// </remarks>
    /// <response code="200">Token issued.</response>
    /// <response code="400">Malformed request.</response>
    /// <response code="401">Unknown user or wrong password.</response>
    [HttpPost("token")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<TokenResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<TokenResponse>>> IssueToken([FromBody] TokenRequest request, CancellationToken cancellationToken)
    {
        var token = await _auth.IssueTokenAsync(request, cancellationToken);
        return Ok(ApiResponse<TokenResponse>.Ok(token));
    }
}
