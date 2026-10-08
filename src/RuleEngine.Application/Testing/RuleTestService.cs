using Microsoft.EntityFrameworkCore;
using RuleEngine.Application.Common;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Application.Rules;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Application.Testing;

public sealed class RuleTestService
{
    private const string EntityType = "RuleTestCase";

    private readonly IRuleEngineDbContext _db;
    private readonly RuleTestRunner _runner;
    private readonly IAuditTrail _audit;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;

    public RuleTestService(IRuleEngineDbContext db, RuleTestRunner runner, IAuditTrail audit, ICurrentUser currentUser, TimeProvider clock)
    {
        _db = db;
        _runner = runner;
        _audit = audit;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<PagedResult<RuleTestCaseDto>> ListAsync(Guid ruleId, RuleTestCaseListQuery query, CancellationToken cancellationToken)
    {
        await EnsureRuleExistsAsync(ruleId, cancellationToken);
        var page = await _db.RuleTestCases.AsNoTracking()
            .Where(t => t.RuleId == ruleId)
            .OrderBy(t => t.Name)
            .ToPagedResultAsync(query, cancellationToken);
        return page.Map(RuleTestCaseDto.From);
    }

    public async Task<RuleTestCaseDto> GetAsync(Guid ruleId, Guid testCaseId, CancellationToken cancellationToken)
    {
        var testCase = await _db.RuleTestCases.AsNoTracking()
            .FirstOrDefaultAsync(t => t.RuleId == ruleId && t.Id == testCaseId, cancellationToken)
            ?? throw new NotFoundException("Test case", testCaseId);
        return RuleTestCaseDto.From(testCase);
    }

    public async Task<RuleTestCaseDto> CreateAsync(Guid ruleId, RuleTestCaseRequest request, CancellationToken cancellationToken)
    {
        await EnsureRuleExistsAsync(ruleId, cancellationToken);
        var name = request.Name.Trim();
        if (await _db.RuleTestCases.AnyAsync(t => t.RuleId == ruleId && t.Name == name, cancellationToken))
        {
            throw new ConflictException($"The rule already has a test case named '{name}'.");
        }

        var testCase = RuleTestCase.Create(
            ruleId,
            name,
            request.InputFacts!.ToJsonString(),
            request.ExpectedMatch,
            NormalizeActionTypes(request.ExpectedActionTypes),
            _currentUser.UserName,
            Now);
        _db.RuleTestCases.Add(testCase);
        _audit.Record(EntityType, testCase.Id, "Created", null, new { RuleId = ruleId, testCase.Name, testCase.ExpectedMatch });
        await _db.SaveChangesAsync(cancellationToken);
        return RuleTestCaseDto.From(testCase);
    }

    public async Task<RuleTestCaseDto> UpdateAsync(Guid ruleId, Guid testCaseId, RuleTestCaseRequest request, CancellationToken cancellationToken)
    {
        var testCase = await _db.RuleTestCases.FirstOrDefaultAsync(t => t.RuleId == ruleId && t.Id == testCaseId, cancellationToken)
            ?? throw new NotFoundException("Test case", testCaseId);
        var name = request.Name.Trim();
        if (await _db.RuleTestCases.AnyAsync(t => t.RuleId == ruleId && t.Name == name && t.Id != testCaseId, cancellationToken))
        {
            throw new ConflictException($"The rule already has a test case named '{name}'.");
        }

        var before = new { testCase.Name, testCase.ExpectedMatch };
        testCase.Update(name, request.InputFacts!.ToJsonString(), request.ExpectedMatch, NormalizeActionTypes(request.ExpectedActionTypes), Now);
        _audit.Record(EntityType, testCase.Id, "Updated", before, new { testCase.Name, testCase.ExpectedMatch });
        await _db.SaveChangesAsync(cancellationToken);
        return RuleTestCaseDto.From(testCase);
    }

    public async Task DeleteAsync(Guid ruleId, Guid testCaseId, CancellationToken cancellationToken)
    {
        var testCase = await _db.RuleTestCases.FirstOrDefaultAsync(t => t.RuleId == ruleId && t.Id == testCaseId, cancellationToken)
            ?? throw new NotFoundException("Test case", testCaseId);
        _db.RuleTestCases.Remove(testCase);
        _audit.Record(EntityType, testCase.Id, "Deleted", new { RuleId = ruleId, testCase.Name }, null);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Runs all test cases of a rule against a version: the requested one, else the latest draft,
    /// else the published version. Records the last-run outcome on each test case.
    /// </summary>
    public async Task<RuleTestRunReport> RunAsync(Guid ruleId, int? versionNumber, CancellationToken cancellationToken)
    {
        var rule = await _db.Rules.AsNoTracking().Include(r => r.Versions).FirstOrDefaultAsync(r => r.Id == ruleId, cancellationToken)
            ?? throw new NotFoundException("Rule", ruleId);

        var version = versionNumber is not null
            ? rule.FindVersion(versionNumber.Value) ?? throw new NotFoundException("Rule version", $"{ruleId}/v{versionNumber}")
            : rule.LatestDraft()
              ?? (rule.CurrentPublishedVersionNumber is { } published ? rule.FindVersion(published) : null)
              ?? throw new BusinessRuleException("The rule has no draft or published version to test.");

        var ruleSet = await _db.RuleSets.AsNoTracking().FirstAsync(s => s.Id == rule.RuleSetId, cancellationToken);
        var testCases = await _db.RuleTestCases.Where(t => t.RuleId == ruleId).ToListAsync(cancellationToken);
        var report = _runner.Run(ruleSet.GetFactSchema(), EvaluableRuleLoader.ToEvaluable(rule, version), testCases);

        foreach (var testCase in testCases)
        {
            testCase.RecordRun(report.Results.First(r => r.TestCaseId == testCase.Id).Passed, Now);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return report;
    }

    private static List<string>? NormalizeActionTypes(List<string>? types) =>
        types?.Select(t => RuleActionTypes.TryParse(t, out var parsed) ? parsed.ToString() : t).ToList();

    private async Task EnsureRuleExistsAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        if (!await _db.Rules.AnyAsync(r => r.Id == ruleId, cancellationToken))
        {
            throw new NotFoundException("Rule", ruleId);
        }
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
}
