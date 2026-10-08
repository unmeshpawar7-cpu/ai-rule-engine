using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RuleEngine.Application.Common;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Application.Rules;
using RuleEngine.Domain.Common;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Application.Execution;

/// <summary>
/// Execution pipeline: idempotency check -> fact validation against the schema -> deterministic
/// evaluation -> action execution (with retries) -> persistence of the execution, its action
/// outcomes and an audit record in one unit of work.
/// </summary>
public sealed class RuleExecutionService
{
    private readonly IRuleEngineDbContext _db;
    private readonly EvaluableRuleLoader _loader;
    private readonly RuleEvaluator _evaluator;
    private readonly IActionDispatcher _dispatcher;
    private readonly IAuditTrail _audit;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;
    private readonly ILogger<RuleExecutionService> _logger;

    public RuleExecutionService(
        IRuleEngineDbContext db,
        EvaluableRuleLoader loader,
        RuleEvaluator evaluator,
        IActionDispatcher dispatcher,
        IAuditTrail audit,
        ICurrentUser currentUser,
        TimeProvider clock,
        ILogger<RuleExecutionService> logger)
    {
        _db = db;
        _loader = loader;
        _evaluator = evaluator;
        _dispatcher = dispatcher;
        _audit = audit;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ExecutionResultDto> ExecuteAsync(
        Guid ruleSetId,
        JsonObject facts,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (key is not null && key.Length > RuleExecution.IdempotencyKeyMaxLength)
        {
            throw new ValidationException(new[]
            {
                new ValidationFailure("Idempotency-Key", $"Idempotency-Key must be at most {RuleExecution.IdempotencyKeyMaxLength} characters."),
            });
        }

        var inputHash = CanonicalJson.Sha256(facts);
        if (key is not null)
        {
            var previous = await FindByIdempotencyKeyAsync(ruleSetId, key, cancellationToken);
            if (previous is not null)
            {
                return Replay(previous, inputHash);
            }
        }

        var ruleSet = await _db.RuleSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == ruleSetId, cancellationToken)
            ?? throw new NotFoundException("Rule set", ruleSetId);
        if (!ruleSet.IsActive)
        {
            throw new BusinessRuleException($"Rule set '{ruleSet.Name}' is inactive and cannot be executed.");
        }

        var schema = ruleSet.GetFactSchema();
        EnsureFactsValid(schema, facts);

        var stopwatch = Stopwatch.StartNew();
        var startedAt = Now;
        var rules = await _loader.LoadPublishedAsync(ruleSetId, cancellationToken);
        var evaluation = _evaluator.Evaluate(facts, rules, ruleSet.Strategy, schema);

        var execution = RuleExecution.Start(ruleSetId, _currentUser.CorrelationId, key, inputHash, _currentUser.UserName, startedAt);
        var outcomes = await _dispatcher.DispatchAsync(
            execution.Id,
            ruleSetId,
            execution.CorrelationId,
            evaluation.Actions,
            evaluation.OutputFacts,
            cancellationToken);

        foreach (var outcome in outcomes)
        {
            execution.AddAction(ActionExecution.Create(
                execution.Id,
                outcome.Selected.Sequence,
                outcome.Selected.RuleId,
                outcome.Selected.VersionId,
                outcome.Selected.Action.ActionType?.ToString() ?? outcome.Selected.Action.Type,
                ExecutionOutcomes.SerializeParameters(outcome.Selected.Action),
                outcome.Status,
                outcome.Attempts,
                outcome.Message,
                outcome.StartedAtUtc,
                outcome.CompletedAtUtc));
        }

        var status = ExecutionOutcomes.OverallStatus(outcomes.Select(o => o.Status).ToList());
        var decision = ExecutionOutcomes.Decision(evaluation.MatchedRules.Count > 0, evaluation.Actions.Select(a => a.Action.ActionType));
        stopwatch.Stop();
        execution.Complete(
            status,
            decision,
            JsonSerializer.Serialize(ExecutionOutcomes.MatchedRules(evaluation), DomainJson.Options),
            JsonSerializer.Serialize(evaluation.Rules, DomainJson.Options),
            evaluation.OutputFacts.ToJsonString(),
            Now,
            stopwatch.ElapsedMilliseconds);

        _db.RuleExecutions.Add(execution);
        _audit.Record(
            "RuleExecution",
            execution.Id,
            "Executed",
            null,
            new
            {
                RuleSetId = ruleSetId,
                Status = status,
                Decision = decision,
                MatchedVersions = evaluation.MatchedRules.Select(r => $"{r.RuleName}@v{r.VersionNumber}").ToList(),
                InputHash = inputHash,
            });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (key is not null)
        {
            // A concurrent request with the same Idempotency-Key committed first; the unique index
            // (RuleSetId, IdempotencyKey) rejected this one. Return the winner's stored result.
            _logger.LogWarning(ex, "Idempotency-Key collision for rule set {RuleSetId}; returning the stored execution", ruleSetId);
            var winner = await FindByIdempotencyKeyAsync(ruleSetId, key, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return Replay(winner, inputHash);
        }

        _logger.LogInformation(
            "Executed rule set {RuleSetId}: {MatchedCount} rule(s) matched, {ActionCount} action(s), status {Status}, decision {Decision}, {DurationMs} ms",
            ruleSetId,
            evaluation.MatchedRules.Count,
            outcomes.Count,
            status,
            decision,
            execution.DurationMs);

        return ExecutionOutcomes.FromEntity(execution, replayed: false);
    }

    /// <summary>
    /// Dry run: evaluates drafts and/or published versions and reports which actions would run.
    /// Nothing is executed, persisted or audited.
    /// </summary>
    public async Task<ExecutionResultDto> SimulateAsync(Guid ruleSetId, SimulateRuleSetRequest request, CancellationToken cancellationToken)
    {
        var ruleSet = await _db.RuleSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == ruleSetId, cancellationToken)
            ?? throw new NotFoundException("Rule set", ruleSetId);
        var facts = request.Facts!;
        var schema = ruleSet.GetFactSchema();
        EnsureFactsValid(schema, facts);

        var stopwatch = Stopwatch.StartNew();
        var startedAt = Now;
        var rules = await _loader.LoadForSimulationAsync(ruleSetId, request.IncludeDrafts, request.IncludeInactive, cancellationToken);
        var evaluation = _evaluator.Evaluate(facts, rules, ruleSet.Strategy, schema);
        stopwatch.Stop();

        return new ExecutionResultDto
        {
            ExecutionId = null,
            RuleSetId = ruleSetId,
            CorrelationId = _currentUser.CorrelationId,
            IsSimulation = true,
            Replayed = false,
            Status = null,
            Decision = ExecutionOutcomes.Decision(evaluation.MatchedRules.Count > 0, evaluation.Actions.Select(a => a.Action.ActionType)),
            StartedAtUtc = startedAt,
            DurationMs = stopwatch.ElapsedMilliseconds,
            MatchedRules = ExecutionOutcomes.MatchedRules(evaluation),
            Actions = evaluation.Actions.Select(a => new ActionOutcomeDto(
                a.Sequence,
                a.RuleId,
                a.VersionId,
                a.Action.ActionType?.ToString() ?? a.Action.Type,
                a.Action.Parameters,
                ActionExecutionStatus.NotExecuted,
                0,
                "Simulation: action selected but not executed.",
                0)).ToList(),
            Tags = ExecutionOutcomes.Tags(evaluation.Actions.Select(a => a.Action)),
            OutputFacts = evaluation.OutputFacts,
            Trace = evaluation.Rules,
        };
    }

    private static void EnsureFactsValid(FactSchema schema, JsonObject facts)
    {
        var errors = FactValidator.Validate(schema, facts);
        if (errors.Count > 0)
        {
            throw new BusinessRuleException(
                "Input facts do not satisfy the rule set's fact schema.",
                errors.Select(e => new ErrorItem("facts." + e.Field, e.Message, "FACT_INVALID")));
        }
    }

    private ExecutionResultDto Replay(RuleExecution previous, string inputHash)
    {
        if (!string.Equals(previous.InputHash, inputHash, StringComparison.Ordinal))
        {
            throw new ConflictException("This Idempotency-Key was already used with a different request payload.");
        }

        _logger.LogInformation("Idempotent replay of execution {ExecutionId}", previous.Id);
        return ExecutionOutcomes.FromEntity(previous, replayed: true);
    }

    private Task<RuleExecution?> FindByIdempotencyKeyAsync(Guid ruleSetId, string key, CancellationToken cancellationToken) =>
        _db.RuleExecutions.AsNoTracking()
            .Include(e => e.Actions)
            .FirstOrDefaultAsync(e => e.RuleSetId == ruleSetId && e.IdempotencyKey == key, cancellationToken);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
}
