using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RuleEngine.Api.Common;
using RuleEngine.Application.Common;
using RuleEngine.Application.RuleSets;
using RuleEngine.Domain.Security;

namespace RuleEngine.Api.Controllers;

/// <summary>Rule sets: a fact schema, an evaluation strategy and the rules evaluated together.</summary>
[ApiController]
[Route("api/rulesets")]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
public sealed class RuleSetsController : ControllerBase
{
    private readonly RuleSetService _service;

    public RuleSetsController(RuleSetService service)
    {
        _service = service;
    }

    /// <summary>Lists rule sets (paged, ordered by name).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<RuleSetDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<RuleSetDto>>>> List([FromQuery] RuleSetListQuery query, CancellationToken cancellationToken) =>
        Ok(ApiResponse<PagedResult<RuleSetDto>>.Ok(await _service.ListAsync(query, cancellationToken)));

    /// <summary>Gets one rule set including its fact schema.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<RuleSetDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<RuleSetDto>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RuleSetDto>.Ok(await _service.GetAsync(id, cancellationToken)));

    /// <summary>Creates a rule set with its fact schema.</summary>
    /// <remarks>
    /// Example:
    /// <code>
    /// { "name": "purchase-approval", "strategy": "AllMatches",
    ///   "factSchema": { "fields": [ { "path": "document.quantity", "type": "Number", "required": true, "aliases": ["quantity", "qty"] } ] } }
    /// </code>
    /// </remarks>
    /// <response code="201">Created.</response>
    /// <response code="400">Validation failed.</response>
    /// <response code="409">A rule set with this name exists.</response>
    /// <response code="422">The fact schema is invalid.</response>
    [HttpPost]
    [Authorize(Roles = UserRoles.AdminOrAuthor)]
    [ProducesResponseType(typeof(ApiResponse<RuleSetDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<RuleSetDto>>> Create([FromBody] CreateRuleSetRequest request, CancellationToken cancellationToken)
    {
        var created = await _service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, ApiResponse<RuleSetDto>.Ok(created));
    }

    /// <summary>Updates description and evaluation strategy.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = UserRoles.AdminOrAuthor)]
    [ProducesResponseType(typeof(ApiResponse<RuleSetDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RuleSetDto>>> Update(Guid id, [FromBody] UpdateRuleSetRequest request, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RuleSetDto>.Ok(await _service.UpdateAsync(id, request, cancellationToken)));

    /// <summary>Replaces the fact schema. Fields still referenced by draft or published rule versions cannot be removed (422).</summary>
    [HttpPut("{id:guid}/fact-schema")]
    [Authorize(Roles = UserRoles.AdminOrAuthor)]
    [ProducesResponseType(typeof(ApiResponse<RuleSetDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<RuleSetDto>>> UpdateFactSchema(Guid id, [FromBody] UpdateFactSchemaRequest request, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RuleSetDto>.Ok(await _service.UpdateSchemaAsync(id, request, cancellationToken)));

    /// <summary>Activates the rule set (it can be executed again).</summary>
    [HttpPost("{id:guid}/activate")]
    [Authorize(Roles = UserRoles.RuleAdmin)]
    [ProducesResponseType(typeof(ApiResponse<RuleSetDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<RuleSetDto>>> Activate(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RuleSetDto>.Ok(await _service.ActivateAsync(id, cancellationToken)));

    /// <summary>Deactivates the rule set (execution is refused with 422; simulation still works).</summary>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Roles = UserRoles.RuleAdmin)]
    [ProducesResponseType(typeof(ApiResponse<RuleSetDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<RuleSetDto>>> Deactivate(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RuleSetDto>.Ok(await _service.DeactivateAsync(id, cancellationToken)));

    /// <summary>Deletes an empty rule set (no rules, no execution history); otherwise 409.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = UserRoles.RuleAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
