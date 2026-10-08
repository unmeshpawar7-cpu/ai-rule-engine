using System.Text.Json;
using System.Text.Json.Nodes;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Application.Testing;

/// <summary>Runs a rule's test cases against one specific version, in isolation from other rules. Pure.</summary>
public sealed class RuleTestRunner
{
    private readonly RuleEvaluator _evaluator;

    public RuleTestRunner(RuleEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    public RuleTestRunReport Run(FactSchema schema, EvaluableRule rule, IReadOnlyList<RuleTestCase> testCases)
    {
        var results = testCases
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => RunCase(schema, rule, t))
            .ToList();
        var passed = results.Count(r => r.Passed);
        return new RuleTestRunReport(rule.RuleId, rule.VersionNumber, rule.Status, results.Count, passed, results.Count - passed, results);
    }

    private RuleTestCaseResultDto RunCase(FactSchema schema, EvaluableRule rule, RuleTestCase testCase)
    {
        var expectedActions = testCase.GetExpectedActionTypes();
        JsonObject? facts;
        try
        {
            facts = JsonNode.Parse(testCase.InputFactsJson) as JsonObject;
        }
        catch (JsonException)
        {
            facts = null;
        }

        if (facts is null)
        {
            return Failed(testCase, expectedActions, "Input facts are not a JSON object.");
        }

        var factErrors = FactValidator.Validate(schema, facts);
        if (factErrors.Count > 0)
        {
            return Failed(
                testCase,
                expectedActions,
                "Input facts do not satisfy the fact schema: " + string.Join("; ", factErrors.Select(e => $"{e.Field}: {e.Message}")));
        }

        var evaluation = _evaluator.Evaluate(facts, new[] { rule }, EvaluationStrategy.AllMatches, schema);
        var trace = evaluation.Rules[0];
        var actualActions = evaluation.Actions
            .Select(a => a.Action.ActionType?.ToString() ?? a.Action.Type)
            .ToList();

        var failures = new List<string>();
        if (trace.Matched != testCase.ExpectedMatch)
        {
            failures.Add($"Expected match = {testCase.ExpectedMatch} but was {trace.Matched}.");
        }

        if (expectedActions is not null)
        {
            var expectedSorted = expectedActions
                .Select(a => RuleActionTypes.TryParse(a, out var t) ? t.ToString() : a)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();
            var actualSorted = actualActions.OrderBy(a => a, StringComparer.Ordinal).ToList();
            if (!expectedSorted.SequenceEqual(actualSorted, StringComparer.Ordinal))
            {
                failures.Add($"Expected actions [{string.Join(", ", expectedSorted)}] but got [{string.Join(", ", actualSorted)}].");
            }
        }

        return new RuleTestCaseResultDto(
            testCase.Id,
            testCase.Name,
            failures.Count == 0,
            testCase.ExpectedMatch,
            trace.Matched,
            expectedActions,
            actualActions,
            failures,
            trace);
    }

    private static RuleTestCaseResultDto Failed(RuleTestCase testCase, IReadOnlyList<string>? expectedActions, string failure) =>
        new(testCase.Id, testCase.Name, false, testCase.ExpectedMatch, false, expectedActions, Array.Empty<string>(), new[] { failure }, null);
}
