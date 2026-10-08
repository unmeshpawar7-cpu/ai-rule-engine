using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RuleEngine.Api.Common;
using RuleEngine.Application.Common;
using RuleEngine.Application.Generation;
using RuleEngine.Application.Rules;
using RuleEngine.Domain.Security;

namespace RuleEngine.Api.Controllers;

/// <summary>Rules and their immutable versions: authoring, AI-assisted drafting, publishing and activation.</summary>
[ApiController]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
public sealed class RulesController : ControllerBase
{
    private readonly RuleService _rules;
    private readonly RuleGenerationService _generation;

    public RulesController(RuleService rules, RuleGenerationService generation)
    {
        _rules = rules;
        _generation = generation;
    }

    /// <summary>Lists rules (paged, ordered by priority then name). Filter by rule set, activity, drafts or source.</summary>
    [HttpGet("api/rules")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<RuleSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<RuleSummaryDto>>>> List([FromQuery] RuleListQuery query, CancellationToken cancellationToken) =>
        Ok(ApiResponse<PagedResult<RuleSummaryDto>>.Ok(await _rules.ListAsync(query, cancellationToken)));

    /// <summary>Gets a rule with all of its versions (newest first).</summary>
    [HttpGet("api/rules/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<RuleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<RuleDetailDto>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RuleDetailDto>.Ok(await _rules.GetAsync(id, cancellationToken)));

    /// <summary>Creates a rule with its first Draft version (inactive until published and activated).</summary>
    /// <remarks>
    /// Example:
    /// <code>
    /// { "name": "Large orders need manager approval", "priority": 10,
    ///   "condition": { "field": "document.quantity", "operator": "GreaterThan", "value": 10 },
    ///   "actions": [ { "type": "RequireApproval", "parameters": { "role": "Manager" } } ] }
    /// </code>
    /// The response carries non-blocking warnings such as conflicts with active rules.
    /// </remarks>
    /// <response code="201">Created as Draft.</response>
    /// <response code="409">Name already used in this rule set.</response>
    /// <response code="422">The definition failed validation (errors carry JSON paths and codes).</response>
    [HttpPost("api/rulesets/{ruleSetId:guid}/rules")]
    [Authorize(Roles = UserRoles.AdminOrAuthor)]
    [ProducesResponseType(typeof(ApiResponse<RuleWriteResultDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<RuleWriteResultDto>>> Create(Guid ruleSetId, [FromBody] CreateRuleRequest request, CancellationToken cancellationToken)
    {
        var result = await _rules.CreateAsync(ruleSetId, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = result.Rule.Id }, ApiResponse<RuleWriteResultDto>.Ok(result));
    }

    /// <summary>Generates a Draft rule from natural language. Never publishes, activates or executes it.</summary>
    /// <remarks>
    /// Example: <c>{ "text": "Documents with quantity greater than 10 should require manager approval." }</c>.
    /// The deterministic pattern grammar runs first; an LLM is consulted only if configured (<c>LLM_PROVIDER</c>)
    /// and the grammar cannot interpret the text. The proposal must pass the same validation gate as manual rules.
    /// Outcomes: <c>DraftSaved</c> (201), <c>ProposalOnly</c> (200, when saveAsDraft = false),
    /// <c>RejectedByValidation</c> / <c>NotUnderstood</c> (422, nothing stored, proposal and errors returned).
    /// Rate limited per user.
    /// </remarks>
    [HttpPost("api/rulesets/{ruleSetId:guid}/rules/generate")]
    [Authorize(Roles = UserRoles.AdminOrAuthor)]
    [EnableRateLimiting(RateLimitPolicies.Generation)]
    [ProducesResponseType(typeof(ApiResponse<GenerateRuleResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<GenerateRuleResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<GenerateRuleResponse>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<GenerateRuleResponse>>> Generate(Guid ruleSetId, [FromBody] GenerateRuleRequest request, CancellationToken cancellationToken)
    {
        var result = await _generation.GenerateAsync(ruleSetId, request, cancellationToken);
        switch (result.Outcome)
        {
            case GenerationOutcomes.DraftSaved:
                return CreatedAtAction(nameof(Get), new { id = result.RuleId }, ApiResponse<GenerateRuleResponse>.Ok(result, "Draft created. Review, test and publish it explicitly."));
            case GenerationOutcomes.ProposalOnly:
                return Ok(ApiResponse<GenerateRuleResponse>.Ok(result, "Proposal is valid; nothing was stored."));
            default:
                var message = result.Outcome == GenerationOutcomes.NotUnderstood
                    ? "The rule text could not be interpreted."
                    : "The generated rule failed validation; nothing was stored.";
                return UnprocessableEntity(new ApiResponse<GenerateRuleResponse>
                {
                    Success = false,
                    Data = result,
                    Message = message,
                    Errors = RuleMapper.ToErrorItems(result.Errors),
                    TraceId = HttpContext.TraceIdentifier,
                });
        }
    }

    /// <summary>Adds a new Draft version to a rule.</summary>
    [HttpPost("api/rules/{id:guid}/versions")]
    [Authorize(Roles = UserRoles.AdminOrAuthor)]
    [ProducesResponseType(typeof(ApiResponse<RuleWriteResultDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<RuleWriteResultDto>>> AddVersion(Guid id, [FromBody] AddRuleVersionRequest request, CancellationToken cancellationToken)
    {
        var result = await _rules.AddDraftVersionAsync(id, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id }, ApiResponse<RuleWriteResultDto>.Ok(result));
    }

    /// <summary>Publishes a Draft version (the previous published version is retired).</summary>
    /// <remarks>When <c>RuleEngine:RequirePassingTestsToPublish</c> is on, all test cases must pass against the version (else 422).</remarks>
    [HttpPost("api/rules/{id:guid}/publish")]
    [Authorize(Roles = UserRoles.RuleAdmin)]
    [ProducesResponseType(typeof(ApiResponse<RuleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<RuleDetailDto>>> Publish(Guid id, [FromBody] PublishRuleRequest request, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RuleDetailDto>.Ok(await _rules.PublishAsync(id, request, cancellationToken)));

    /// <summary>Activates a rule; its published version takes part in executions.</summary>
    [HttpPost("api/rules/{id:guid}/activate")]
    [Authorize(Roles = UserRoles.RuleAdmin)]
    [ProducesResponseType(typeof(ApiResponse<RuleDetailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<RuleDetailDto>>> Activate(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RuleDetailDto>.Ok(await _rules.ActivateAsync(id, cancellationToken)));

    /// <summary>Deactivates a rule.</summary>
    [HttpPost("api/rules/{id:guid}/deactivate")]
    [Authorize(Roles = UserRoles.RuleAdmin)]
    [ProducesResponseType(typeof(ApiResponse<RuleDetailDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RuleDetailDto>>> Deactivate(Guid id, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RuleDetailDto>.Ok(await _rules.DeactivateAsync(id, cancellationToken)));

    /// <summary>Deletes a Draft version; deleting the only version deletes the rule.</summary>
    [HttpDelete("api/rules/{id:guid}/versions/{versionNumber:int}")]
    [Authorize(Roles = UserRoles.AdminOrAuthor)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> DeleteDraft(Guid id, int versionNumber, CancellationToken cancellationToken)
    {
        await _rules.DeleteDraftAsync(id, versionNumber, cancellationToken);
        return NoContent();
    }
}
