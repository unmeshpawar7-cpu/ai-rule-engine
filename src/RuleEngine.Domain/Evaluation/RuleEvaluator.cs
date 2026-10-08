using System.Text.Json;
using System.Text.Json.Nodes;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Domain.Evaluation;

/// <summary>
/// Pure, deterministic evaluator: same facts + same rule versions => same result. No I/O,
/// no clock, no randomness. Rules are ordered by priority (lower first), then name, then id.
/// SetValue actions of a matched rule are applied to the working facts immediately, so rules
/// with a lower priority see the values set by earlier rules (chaining).
/// </summary>
public sealed class RuleEvaluator
{
    private readonly EvaluationOptions _options;

    public RuleEvaluator(EvaluationOptions? options = null)
    {
        _options = options ?? new EvaluationOptions();
    }

    public EvaluationResult Evaluate(
        JsonObject facts,
        IEnumerable<EvaluableRule> rules,
        EvaluationStrategy strategy,
        FactSchema? schema = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(rules);

        var working = JsonNode.Parse(facts.ToJsonString())!.AsObject();
        var ordered = rules
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.RuleName, StringComparer.Ordinal)
            .ThenBy(r => r.RuleId)
            .ToList();

        var traces = new List<RuleEvaluationTrace>(ordered.Count);
        var selected = new List<SelectedAction>();
        var stopped = false;
        var sequence = 0;

        foreach (var rule in ordered)
        {
            if (stopped)
            {
                traces.Add(CreateTrace(rule, evaluated: false, matched: false, error: null, condition: null));
                continue;
            }

            var (depth, nodes) = ConditionLimits.Measure(rule.Condition);
            if (depth > _options.MaxDepth || nodes > _options.MaxNodes)
            {
                var error = $"Condition exceeds limits (depth {depth}/{_options.MaxDepth}, nodes {nodes}/{_options.MaxNodes}).";
                traces.Add(CreateTrace(rule, evaluated: true, matched: false, error, condition: null));
                continue;
            }

            var trace = EvaluateNode(rule.Condition, working, schema);
            traces.Add(CreateTrace(rule, evaluated: true, matched: trace.Result, error: null, condition: trace));
            if (!trace.Result)
            {
                continue;
            }

            foreach (var action in rule.Actions)
            {
                sequence++;
                selected.Add(new SelectedAction
                {
                    Sequence = sequence,
                    RuleId = rule.RuleId,
                    RuleName = rule.RuleName,
                    VersionId = rule.VersionId,
                    VersionNumber = rule.VersionNumber,
                    Action = action,
                });

                if (action.ActionType == RuleActionType.SetValue)
                {
                    ApplySetValue(working, action);
                }
            }

            if (strategy == EvaluationStrategy.FirstMatch)
            {
                stopped = true;
            }
        }

        return new EvaluationResult
        {
            Strategy = strategy,
            Rules = traces,
            Actions = selected,
            OutputFacts = working,
        };
    }

    /// <summary>Evaluates a single condition tree against facts, returning the full trace.</summary>
    public TraceNode EvaluateCondition(ConditionNode condition, JsonObject facts, FactSchema? schema = null) =>
        EvaluateNode(condition, facts, schema);

    private TraceNode EvaluateNode(ConditionNode node, JsonObject facts, FactSchema? schema)
    {
        switch (node.Kind)
        {
            case ConditionNodeKind.All:
            {
                var children = node.GetChildren().Select(c => EvaluateNode(c, facts, schema)).ToList();
                return new TraceNode
                {
                    Kind = "all",
                    Result = children.Count > 0 && children.TrueForAll(c => c.Result),
                    Children = children,
                    Note = children.Count == 0 ? "Empty 'all' group never matches." : null,
                };
            }

            case ConditionNodeKind.Any:
            {
                var children = node.GetChildren().Select(c => EvaluateNode(c, facts, schema)).ToList();
                return new TraceNode
                {
                    Kind = "any",
                    Result = children.Exists(c => c.Result),
                    Children = children,
                };
            }

            case ConditionNodeKind.Not:
            {
                var child = EvaluateNode(node.Not!, facts, schema);
                return new TraceNode { Kind = "not", Result = !child.Result, Children = new List<TraceNode> { child } };
            }

            case ConditionNodeKind.Leaf:
                return EvaluateLeaf(node, facts, schema);

            default:
                return new TraceNode
                {
                    Kind = "invalid",
                    Result = false,
                    Note = "Node must contain exactly one of 'all', 'any', 'not' or 'field'.",
                };
        }
    }

    private TraceNode EvaluateLeaf(ConditionNode node, JsonObject facts, FactSchema? schema)
    {
        var field = node.Field!;
        var expectedText = node.Value?.ToJsonString();
        if (!ConditionOperators.TryParse(node.Operator, out var op))
        {
            return new TraceNode
            {
                Kind = "leaf",
                Field = field,
                Operator = node.Operator,
                Expected = expectedText,
                Result = false,
                Note = $"Unknown operator '{node.Operator}'.",
            };
        }

        var found = FactPath.TryGet(facts, field, out var actualNode);
        var actual = FactValueConverter.ToElement(actualNode);
        var expected = FactValueConverter.ToElement(node.Value);
        var type = schema?.Find(field)?.Type ?? InferType(actual, expected);
        var outcome = LeafOperatorEvaluator.Evaluate(op, type, actual, expected, node.IgnoreCase ?? false, _options.RegexTimeout);

        return new TraceNode
        {
            Kind = "leaf",
            Field = field,
            Operator = op.ToString(),
            Expected = expectedText,
            Actual = found ? actualNode?.ToJsonString() ?? "null" : null,
            Result = outcome.Result,
            Note = found ? outcome.Note : "Fact not present in input." + (outcome.Note is null ? string.Empty : " " + outcome.Note),
        };
    }

    private static FactType InferType(JsonElement? actual, JsonElement? expected)
    {
        if (actual is { ValueKind: JsonValueKind.Array })
        {
            return FactType.StringList;
        }

        var sample = !FactValueConverter.IsNull(actual) ? actual : expected;
        if (sample is { ValueKind: JsonValueKind.Array } array)
        {
            sample = array.GetArrayLength() > 0 ? array[0] : null;
        }

        if (sample is null)
        {
            return FactType.String;
        }

        return sample.Value.ValueKind switch
        {
            JsonValueKind.Number => FactType.Number,
            JsonValueKind.True or JsonValueKind.False => FactType.Boolean,
            _ => FactType.String,
        };
    }

    private static void ApplySetValue(JsonObject facts, RuleActionDefinition action)
    {
        var field = action.GetString(ActionParameters.Field);
        if (string.IsNullOrWhiteSpace(field))
        {
            return;
        }

        JsonNode? value = action.TryGetParameter(ActionParameters.Value, out var element)
            ? JsonNode.Parse(element.GetRawText())
            : null;
        FactPath.Set(facts, field, value);
    }

    private static RuleEvaluationTrace CreateTrace(EvaluableRule rule, bool evaluated, bool matched, string? error, TraceNode? condition) =>
        new()
        {
            RuleId = rule.RuleId,
            RuleName = rule.RuleName,
            VersionId = rule.VersionId,
            VersionNumber = rule.VersionNumber,
            VersionStatus = rule.Status,
            Priority = rule.Priority,
            Evaluated = evaluated,
            Matched = matched,
            Error = error,
            Condition = condition,
        };
}
