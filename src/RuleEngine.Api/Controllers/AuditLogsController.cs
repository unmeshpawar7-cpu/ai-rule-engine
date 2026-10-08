using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RuleEngine.Api.Common;
using RuleEngine.Application.Audit;
using RuleEngine.Application.Common;
using RuleEngine.Domain.Security;

namespace RuleEngine.Api.Controllers;

/// <summary>Append-only audit trail of every change and execution.</summary>
[ApiController]
[Route("api/audit-logs")]
[Authorize(Roles = UserRoles.AdminOrAuditor)]
[Produces("application/json")]
public sealed class AuditLogsController : ControllerBase
{
    private readonly AuditQueryService _audit;

    public AuditLogsController(AuditQueryService audit)
    {
        _audit = audit;
    }

    /// <summary>Lists audit records (paged, newest first), filterable by entity, action, actor, correlation id and time.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AuditLogDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<PagedResult<AuditLogDto>>>> List([FromQuery] AuditLogQuery query, CancellationToken cancellationToken) =>
        Ok(ApiResponse<PagedResult<AuditLogDto>>.Ok(await _audit.ListAsync(query, cancellationToken)));
}
