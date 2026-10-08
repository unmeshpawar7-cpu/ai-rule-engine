using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RuleEngine.Application.Common;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Application.Execution;
using RuleEngine.Application.Generation;
using RuleEngine.Application.Rules;
using RuleEngine.Application.Testing;
using RuleEngine.Application.Validation;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;
using RuleEngine.Infrastructure.Persistence;

namespace RuleEngine.UnitTests.Application;

/// <summary>Wires real application services over an EF Core in-memory database.</summary>
internal sealed class ServiceFixture : IDisposable
{
    public ServiceFixture(RuleEngineOptions? options = null)
    {
        EngineOptions = options ?? new RuleEngineOptions { ActionBaseDelayMs = 0, ActionMaxDelayMs = 0 };
        Db = TestData.NewDb();
        User = TestData.User();
        Audit = new AuditTrail(Db, User, Clock);
        Evaluator = new RuleEvaluator(EngineOptions.ToEvaluationOptions());
        var optionsWrapper = Options.Create(EngineOptions);
        DefinitionValidator = new RuleDefinitionValidator(optionsWrapper);
        Validator = new RuleValidator(Db, DefinitionValidator, new ConflictDetector());
        TestRunner = new RuleTestRunner(Evaluator);
        var delay = Substitute.For<IRetryDelay>();
        delay.DelayAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        Dispatcher = new ActionDispatcher(
            new IActionExecutor[] { new SetValueActionExecutor(), new RequireApprovalActionExecutor(), new AddTagActionExecutor(), new RejectActionExecutor() },
            delay,
            optionsWrapper,
            Clock,
            NullLogger<ActionDispatcher>.Instance);
        Execution = new RuleExecutionService(Db, new EvaluableRuleLoader(Db), Evaluator, Dispatcher, Audit, User, Clock, NullLogger<RuleExecutionService>.Instance);
        Rules = new RuleService(Db, Validator, TestRunner, Audit, User, Clock, optionsWrapper);
    }

    public RuleEngineOptions EngineOptions { get; }

    public FixedClock Clock { get; } = new();

    public RuleEngineDbContext Db { get; }

    public ICurrentUser User { get; }

    public IAuditTrail Audit { get; }

    public RuleEvaluator Evaluator { get; }

    public RuleDefinitionValidator DefinitionValidator { get; }

    public RuleValidator Validator { get; }

    public RuleTestRunner TestRunner { get; }

    public ActionDispatcher Dispatcher { get; }

    public RuleExecutionService Execution { get; }

    public RuleService Rules { get; }

    public RuleGenerationService Generation(IRuleGenerator generator) =>
        new(Db, generator, Validator, Audit, User, Clock, NullLogger<RuleGenerationService>.Instance);

    public async Task<RuleSet> SeedRuleSetAsync(EvaluationStrategy strategy = EvaluationStrategy.AllMatches)
    {
        var ruleSet = RuleSet.Create("purchase-approval-" + Guid.NewGuid().ToString("N")[..8], null, strategy, TestData.PurchaseSchema(), "seed", Clock.GetUtcNow().UtcDateTime);
        Db.RuleSets.Add(ruleSet);
        await Db.SaveChangesAsync();
        return ruleSet;
    }

    /// <summary>Adds a rule; publishes and activates version 1 unless <paramref name="publish"/> is false.</summary>
    public async Task<Rule> SeedRuleAsync(Guid ruleSetId, string name, int priority, string conditionJson, string actionsJson, bool publish = true)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var rule = Rule.Create(ruleSetId, name, null, priority, new RuleVersionContent(conditionJson, actionsJson, RuleSource.Manual), "seed", now);
        if (publish)
        {
            rule.Publish(1, "seed", now);
            rule.Activate(now);
        }

        Db.Rules.Add(rule);
        await Db.SaveChangesAsync();
        return rule;
    }

    public void Dispose() => Db.Dispose();
}
