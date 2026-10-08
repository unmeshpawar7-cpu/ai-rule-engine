using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RuleEngine.Api.Common;
using RuleEngine.Application.Common;
using RuleEngine.Application.Execution;
using RuleEngine.Domain.Security;

namespace RuleEngine.Api.Controllers;

/// <summary>Executing and simulating rule sets, and reading execution history.</summary>
[ApiController]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
public sealed class ExecutionsController : ControllerBase
{
    private readonly RuleExecutionService _execution;
    private readonly ExecutionQueryService _queries;

    public ExecutionsController(RuleExecutionService execution, ExecutionQueryService queries)
    {
        _execution = execution;
        _queries = queries;
    }

    /// <summary>Executes the active, published rules of a rule set against the input facts.</summary>
    /// <remarks>
    /// Pipeline: fact validation against the schema, deterministic evaluation, action execution with retries,
    /// persistence of the execution and an audit record. Send an <c>Idempotency-Key</c> header to make retries
    /// safe: the same key and payload return the stored result (200, <c>replayed: true</c>); the same key with a
    /// different payload returns 409.
    /// Example body: <c>{ "facts": { "document": { "quantity": 25, "amount": 12000 }, "department": "IT" } }</c>.
    /// </remarks>
    /// <response code="201">Executed and recorded.</response>
    /// <response code="200">Idempotent replay of a previous execution.</response>
    /// <response code="409">Idempotency-Key reused with a different payload.</response>
    /// <response code="422">Facts do not satisfy the schema, or the rule set is inactive.</response>
    [HttpPost("api/rulesets/{ruleSetId:guid}/execute")]
    [Authorize(Roles = UserRoles.AdminOrExecutor)]
    [ProducesResponseType(typeof(ApiResponse<ExecutionResultDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<ExecutionResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<ExecutionResultDto>>> Execute(
        Guid ruleSetId,
        [FromBody] ExecuteRuleSetRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await _execution.ExecuteAsync(ruleSetId, request.Facts!, idempotencyKey, cancellationToken);
        var envelope = ApiResponse<ExecutionResultDto>.Ok(result, result.Replayed ? "Idempotent replay of a previous execution." : null);
        if (result.Replayed)
        {
            return Ok(envelope);
        }

        return CreatedAtAction(nameof(Get), new { id = result.ExecutionId }, envelope);
    }

    /// <summary>Dry run: evaluates drafts and/or published versions and shows which actions would run. No side effects, nothing stored.</summary>
    [HttpPost("api/rulesets/{ruleSetId:guid}/simulate")]
    [Authorize(Roles = UserRoles.Simulators)]
    [ProducesResponseType(typeof(ApiResponse<ExecutionResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<ExecutionResultDto>>> Simulate(Guid ruleSetId, [FromBody] SimulateRuleSetRequest request, CancellationToken cancellationToken) =>
        Ok(ApiResponse<ExecutionResultDto>.Ok(await _execution.SimulateAsync(ruleSetId, request, cancellationToken), "Simulation only: no actions were executed and nothing was stored."));

    /// <summary>Lists executions (paged, newest first).</summary>
    [HttpGet("api/executions")]
    [Authorize(Roles = UserRoles.ExecutionReaders)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ExecutionSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<ExecutionSummaryDto>>>> List([FromQuery] ExecutionListQuery query, CancellationToken cancellationToken) =>
        Ok(ApiResponse<PagedResult<ExecutionSummaryDto>>.Ok(await _queries.ListAsync(query, cancellationToken)));

    /// <summary>Gets one execution with per-action outcomes and the evaluation trace.</summary>
    [HttpGet("api/executions/{id:guid}")]
    [Authorize(Roles = UserRoles.ExecutionReaders)]
    [ProducesResponseType(typeof(ApiResponse<ExecutionResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ExecutionResultDto>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<ExecutionResultDto>.Ok(await _queries.GetAsync(id, cancellationToken)));
}
