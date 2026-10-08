using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RuleEngine.Application.Common;
using RuleEngine.Application.Execution;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;

namespace RuleEngine.UnitTests.Application;

public class ActionDispatcherTests
{
    private readonly IRetryDelay _delay = Substitute.For<IRetryDelay>();

    public ActionDispatcherTests()
    {
        _delay.DelayAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private ActionDispatcher Dispatcher(params IActionExecutor[] executors) =>
        new(executors, _delay, Options.Create(new RuleEngineOptions { ActionMaxAttempts = 3, ActionBaseDelayMs = 100, ActionMaxDelayMs = 1000 }), new FixedClock(), NullLogger<ActionDispatcher>.Instance);

    private static SelectedAction Selected(int sequence, RuleActionDefinition action) => new()
    {
        Sequence = sequence,
        RuleId = Guid.NewGuid(),
        RuleName = "rule",
        VersionId = Guid.NewGuid(),
        VersionNumber = 1,
        Action = action,
    };

    private Task<IReadOnlyList<ActionOutcome>> Dispatch(ActionDispatcher dispatcher, params SelectedAction[] actions) =>
        dispatcher.DispatchAsync(Guid.NewGuid(), Guid.NewGuid(), "corr", actions, new JsonObject(), CancellationToken.None);

    [Fact]
    public async Task Transient_failures_are_retried_with_backoff_until_success()
    {
        var executor = new ScriptedExecutor(RuleActionType.Notify, failuresBeforeSuccess: 2);

        var outcomes = await Dispatch(Dispatcher(executor), Selected(1, RuleActionDefinition.Notify("ops", "t")));

        outcomes.Single().Status.Should().Be(ActionExecutionStatus.Succeeded);
        outcomes.Single().Attempts.Should().Be(3);
        await _delay.Received(2).DelayAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exhausted_retries_fail_the_action_but_not_the_others()
    {
        var flaky = new ScriptedExecutor(RuleActionType.Notify, failuresBeforeSuccess: 10);
        var tag = new ScriptedExecutor(RuleActionType.AddTag, failuresBeforeSuccess: 0);

        var outcomes = await Dispatch(
            Dispatcher(flaky, tag),
            Selected(1, RuleActionDefinition.Notify("ops", "t")),
            Selected(2, RuleActionDefinition.AddTag("x")));

        outcomes[0].Status.Should().Be(ActionExecutionStatus.Failed);
        outcomes[0].Attempts.Should().Be(3);
        outcomes[0].Message.Should().Contain("after 3 attempt");
        outcomes[1].Status.Should().Be(ActionExecutionStatus.Succeeded);
        ExecutionOutcomes.OverallStatus(outcomes.Select(o => o.Status).ToList()).Should().Be(ExecutionStatus.PartiallyFailed);
    }

    [Fact]
    public async Task Permanent_failures_are_not_retried()
    {
        var executor = Substitute.For<IActionExecutor>();
        executor.ActionType.Returns(RuleActionType.TriggerWorkflow);
        executor.ExecuteAsync(Arg.Any<ActionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<ActionExecutorResult>>(_ => throw new InvalidOperationException("boom"));

        var outcomes = await Dispatch(Dispatcher(executor), Selected(1, RuleActionDefinition.TriggerWorkflow("wf")));

        outcomes.Single().Status.Should().Be(ActionExecutionStatus.Failed);
        outcomes.Single().Attempts.Should().Be(1);
        await _delay.DidNotReceive().DelayAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_executor_fails_the_action()
    {
        var outcomes = await Dispatch(Dispatcher(), Selected(1, RuleActionDefinition.AddTag("x")));

        outcomes.Single().Status.Should().Be(ActionExecutionStatus.Failed);
        outcomes.Single().Message.Should().Contain("No executor");
    }

    [Theory]
    [InlineData(new[] { ActionExecutionStatus.Succeeded, ActionExecutionStatus.Skipped }, ExecutionStatus.Succeeded)]
    [InlineData(new[] { ActionExecutionStatus.Failed, ActionExecutionStatus.Failed }, ExecutionStatus.Failed)]
    [InlineData(new[] { ActionExecutionStatus.Failed, ActionExecutionStatus.Succeeded }, ExecutionStatus.PartiallyFailed)]
    [InlineData(new ActionExecutionStatus[0], ExecutionStatus.Succeeded)]
    public void Overall_status_is_derived_from_action_outcomes(ActionExecutionStatus[] statuses, ExecutionStatus expected)
    {
        ExecutionOutcomes.OverallStatus(statuses).Should().Be(expected);
    }

    [Fact]
    public void Backoff_is_exponential_capped_and_jittered()
    {
        var baseDelay = TimeSpan.FromMilliseconds(200);
        var max = TimeSpan.FromMilliseconds(5000);

        ExponentialBackoff.Compute(1, baseDelay, max, 0).TotalMilliseconds.Should().Be(100);
        ExponentialBackoff.Compute(1, baseDelay, max, 1).TotalMilliseconds.Should().Be(200);
        ExponentialBackoff.Compute(3, baseDelay, max, 0).TotalMilliseconds.Should().Be(400);
        ExponentialBackoff.Compute(20, baseDelay, max, 0.5).TotalMilliseconds.Should().BeInRange(2500, 5000);
    }

    /// <summary>Throws a transient error a fixed number of times, then succeeds.</summary>
    private sealed class ScriptedExecutor : IActionExecutor
    {
        private int _remainingFailures;

        public ScriptedExecutor(RuleActionType type, int failuresBeforeSuccess)
        {
            ActionType = type;
            _remainingFailures = failuresBeforeSuccess;
        }

        public RuleActionType ActionType { get; }

        public Task<ActionExecutorResult> ExecuteAsync(ActionExecutionContext context, CancellationToken cancellationToken)
        {
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new TransientActionException("HTTP 503");
            }

            return Task.FromResult(ActionExecutorResult.Success("done"));
        }
    }
}
