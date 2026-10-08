using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RuleEngine.Api.Common;
using RuleEngine.Application.Common;
using RuleEngine.Application.Testing;
using RuleEngine.Domain.Security;

namespace RuleEngine.Api.Controllers;

/// <summary>Regression test cases per rule and the test runner.</summary>
[ApiController]
[Route("api/rules/{ruleId:guid}/tests")]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
public sealed class RuleTestsController : ControllerBase
{
    private readonly RuleTestService _tests;

    public RuleTestsController(RuleTestService tests)
    {
        _tests = tests;
    }

    /// <summary>Lists the rule's test cases (paged).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<RuleTestCaseDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedResult<RuleTestCaseDto>>>> List(Guid ruleId, [FromQuery] RuleTestCaseListQuery query, CancellationToken cancellationToken) =>
        Ok(ApiResponse<PagedResult<RuleTestCaseDto>>.Ok(await _tests.ListAsync(ruleId, query, cancellationToken)));

    /// <summary>Gets one test case.</summary>
    [HttpGet("{testCaseId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<RuleTestCaseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<RuleTestCaseDto>>> Get(Guid ruleId, Guid testCaseId, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RuleTestCaseDto>.Ok(await _tests.GetAsync(ruleId, testCaseId, cancellationToken)));

    /// <summary>Creates a test case: input facts plus the expected match and (optionally) action types.</summary>
    [HttpPost]
    [Authorize(Roles = UserRoles.AdminOrAuthor)]
    [ProducesResponseType(typeof(ApiResponse<RuleTestCaseDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<RuleTestCaseDto>>> Create(Guid ruleId, [FromBody] RuleTestCaseRequest request, CancellationToken cancellationToken)
    {
        var created = await _tests.CreateAsync(ruleId, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { ruleId, testCaseId = created.Id }, ApiResponse<RuleTestCaseDto>.Ok(created));
    }

    /// <summary>Replaces a test case.</summary>
    [HttpPut("{testCaseId:guid}")]
    [Authorize(Roles = UserRoles.AdminOrAuthor)]
    [ProducesResponseType(typeof(ApiResponse<RuleTestCaseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<RuleTestCaseDto>>> Update(Guid ruleId, Guid testCaseId, [FromBody] RuleTestCaseRequest request, CancellationToken cancellationToken) =>
        Ok(ApiResponse<RuleTestCaseDto>.Ok(await _tests.UpdateAsync(ruleId, testCaseId, request, cancellationToken)));

    /// <summary>Deletes a test case.</summary>
    [HttpDelete("{testCaseId:guid}")]
    [Authorize(Roles = UserRoles.AdminOrAuthor)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid ruleId, Guid testCaseId, CancellationToken cancellationToken)
    {
        await _tests.DeleteAsync(ruleId, testCaseId, cancellationToken);
        return NoContent();
    }

    /// <summary>Runs all test cases against a version (default: latest draft, else the published version).</summary>
    [HttpPost("run")]
    [Authorize(Roles = UserRoles.AdminOrAuthor)]
    [ProducesResponseType(typeof(ApiResponse<RuleTestRunReport>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<RuleTestRunReport>>> Run(Guid ruleId, [FromQuery] int? versionNumber, CancellationToken cancellationToken)
    {
        var report = await _tests.RunAsync(ruleId, versionNumber, cancellationToken);
        return Ok(ApiResponse<RuleTestRunReport>.Ok(report, report.AllPassed ? "All test cases passed." : $"{report.Failed} test case(s) failed."));
    }
}
