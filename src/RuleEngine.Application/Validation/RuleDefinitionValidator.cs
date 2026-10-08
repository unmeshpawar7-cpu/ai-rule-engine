using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RuleEngine.Application.Common;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Common;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Application.Validation;

/// <summary>
/// Structural and semantic validation of a rule definition. Works on raw <see cref="JsonElement"/>s
/// so that it can report precise JSON paths and reject anything the evaluator would not understand
/// (unknown properties, operators, action types, fields). Used for manual and AI-generated rules alike.
/// </summary>
public sealed class RuleDefinitionValidator
{
    private static readonly HashSet<string> NodeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "any", "not", "field", "operator", "value", "ignoreCase",
    };

    private static readonly HashSet<string> ActionProperties = new(StringComparer.OrdinalIgnoreCase) { "type", "parameters" };

    private static readonly Regex TagPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_.:-]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Dictionary<RuleActionType, (string Name, bool Required, int MaxLength)[]> ActionParameterSpecs = new()
    {
        [RuleActionType.SetValue] = new[] { (ActionParameters.Field, true, 200), (ActionParameters.Value, true, 0) },
        [RuleActionType.RequireApproval] = new[] { (ActionParameters.Role, true, 100), (ActionParameters.Reason, false, 500) },
        [RuleActionType.AddTag] = new[] { (ActionParameters.Tag, true, 64) },
        [RuleActionType.Reject] = new[] { (ActionParameters.Reason, true, 500) },
        [RuleActionType.Notify] = new[] { (ActionParameters.Channel, true, 100), (ActionParameters.Template, true, 2000) },
        [RuleActionType.TriggerWorkflow] = new[] { (ActionParameters.Workflow, true, 200) },
    };

    private readonly RuleEngineOptions _options;

    public RuleDefinitionValidator(IOptions<RuleEngineOptions> options)
    {
        _options = options.Value;
    }

    public RuleDefinitionValidation Validate(FactSchema schema, string conditionJson, string actionsJson)
    {
        JsonDocument? condition = null;
        JsonDocument? actions = null;
        try
        {
            try
            {
                condition = JsonDocument.Parse(conditionJson);
                actions = JsonDocument.Parse(actionsJson);
            }
            catch (JsonException ex)
            {
                var invalid = new RuleDefinitionValidation();
                invalid.AddError(ValidationCodes.InvalidJson, "$", "Rule definition is not valid JSON: " + ex.Message);
                return invalid;
            }

            return Validate(schema, condition.RootElement, actions.RootElement);
        }
        finally
        {
            condition?.Dispose();
            actions?.Dispose();
        }
    }

    public RuleDefinitionValidation Validate(FactSchema schema, JsonElement condition, JsonElement actions)
    {
        var result = new RuleDefinitionValidation();
        ValidateCondition(schema, condition, result);
        ValidateActions(schema, actions, result);
        if (!result.IsValid)
        {
            return result;
        }

        try
        {
            var node = JsonSerializer.Deserialize<ConditionNode>(condition.GetRawText(), DomainJson.Options)!;
            NormalizeOperators(node);
            var actionList = RuleActionSerializer.DeserializeList(actions.GetRawText());
            foreach (var action in actionList)
            {
                action.Type = action.ActionType!.Value.ToString();
            }

            result.Condition = node;
            result.Actions = actionList;
        }
        catch (JsonException ex)
        {
            result.AddError(ValidationCodes.InvalidJson, "$", "Rule definition could not be read: " + ex.Message);
        }

        return result;
    }

    private void ValidateCondition(FactSchema schema, JsonElement condition, RuleDefinitionValidation result)
    {
        if (condition.ValueKind != JsonValueKind.Object)
        {
            result.AddError(ValidationCodes.ConditionNotObject, "condition", "Condition must be a JSON object.");
            return;
        }

        var nodes = 0;
        ValidateNode(schema, condition, "condition", 1, ref nodes, result);
        if (nodes > _options.MaxConditionNodes)
        {
            result.AddError(ValidationCodes.NodeLimit, "condition", $"Condition has {nodes} nodes; the limit is {_options.MaxConditionNodes}.");
        }
    }

    private void ValidateNode(FactSchema schema, JsonElement node, string path, int depth, ref int nodes, RuleDefinitionValidation result)
    {
        nodes++;
        if (node.ValueKind != JsonValueKind.Object)
        {
            result.AddError(ValidationCodes.NodeShape, path, "Each condition node must be a JSON object.");
            return;
        }

        if (depth > _options.MaxConditionDepth)
        {
            if (!result.Errors.Exists(e => e.Code == ValidationCodes.DepthLimit))
            {
                result.AddError(ValidationCodes.DepthLimit, path, $"Condition is nested deeper than the limit of {_options.MaxConditionDepth} levels.");
            }

            return;
        }

        foreach (var property in node.EnumerateObject())
        {
            if (!NodeProperties.Contains(property.Name))
            {
                result.AddError(ValidationCodes.UnknownProperty, $"{path}.{property.Name}", $"Unknown property '{property.Name}'.");
            }
        }

        var all = Get(node, "all");
        var any = Get(node, "any");
        var not = Get(node, "not");
        var field = Get(node, "field");
        var present = (all is null ? 0 : 1) + (any is null ? 0 : 1) + (not is null ? 0 : 1) + (field is null ? 0 : 1);
        if (present != 1)
        {
            result.AddError(ValidationCodes.NodeShape, path, "A node must contain exactly one of 'all', 'any', 'not' or 'field'.");
            return;
        }

        if (field is null && (Get(node, "operator") is not null || Get(node, "value") is not null || Get(node, "ignoreCase") is not null))
        {
            result.AddError(ValidationCodes.NodeShape, path, "Group nodes cannot carry 'operator', 'value' or 'ignoreCase'.");
        }

        if (all is not null || any is not null)
        {
            var name = all is not null ? "all" : "any";
            var group = (all ?? any)!.Value;
            if (group.ValueKind != JsonValueKind.Array)
            {
                result.AddError(ValidationCodes.NodeShape, $"{path}.{name}", $"'{name}' must be an array of nodes.");
                return;
            }

            if (group.GetArrayLength() == 0)
            {
                result.AddError(ValidationCodes.EmptyGroup, $"{path}.{name}", $"'{name}' must contain at least one node.");
            }

            var index = 0;
            foreach (var child in group.EnumerateArray())
            {
                ValidateNode(schema, child, $"{path}.{name}[{index}]", depth + 1, ref nodes, result);
                index++;
            }

            return;
        }

        if (not is not null)
        {
            ValidateNode(schema, not.Value, $"{path}.not", depth + 1, ref nodes, result);
            return;
        }

        ValidateLeaf(schema, node, field!.Value, path, result);
    }

    private void ValidateLeaf(FactSchema schema, JsonElement node, JsonElement fieldElement, string path, RuleDefinitionValidation result)
    {
        if (fieldElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(fieldElement.GetString()))
        {
            result.AddError(ValidationCodes.NodeShape, $"{path}.field", "'field' must be a non-empty string.");
            return;
        }

        var fieldPath = fieldElement.GetString()!;
        var fact = schema.Find(fieldPath);
        if (fact is null)
        {
            result.AddError(ValidationCodes.UnknownField, $"{path}.field", $"Field '{fieldPath}' is not declared in the rule set's fact schema.");
        }

        var operatorElement = Get(node, "operator");
        if (operatorElement is not { ValueKind: JsonValueKind.String }
            || !ConditionOperators.TryParse(operatorElement.Value.GetString(), out var op))
        {
            result.AddError(
                ValidationCodes.UnknownOperator,
                $"{path}.operator",
                "Operator is missing or unknown. Allowed: " + string.Join(", ", ConditionOperators.Names) + ".");
            return;
        }

        var ignoreCase = Get(node, "ignoreCase");
        if (ignoreCase is not null && ignoreCase.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            result.AddError(ValidationCodes.NodeShape, $"{path}.ignoreCase", "'ignoreCase' must be a boolean.");
        }

        if (fact is null)
        {
            return;
        }

        if (!ConditionOperators.Supports(op, fact.Type))
        {
            result.AddError(
                ValidationCodes.OperatorTypeMismatch,
                $"{path}.operator",
                $"Operator {op} cannot be applied to {fact.Type} field '{fieldPath}'. Supported types: {string.Join(", ", ConditionOperators.SupportedTypes(op))}.");
            return;
        }

        if (ignoreCase is { ValueKind: JsonValueKind.True } && fact.Type is not (FactType.String or FactType.StringList))
        {
            result.AddWarning(ValidationCodes.IgnoreCaseIgnored, $"{path}.ignoreCase", $"'ignoreCase' has no effect on {fact.Type} fields.");
        }

        var value = Get(node, "value");
        var hasValue = value is not null && value.Value.ValueKind != JsonValueKind.Null;
        if (!ConditionOperators.RequiresValue(op))
        {
            if (hasValue)
            {
                result.AddError(ValidationCodes.ValueNotAllowed, $"{path}.value", $"Operator {op} does not take a value.");
            }

            return;
        }

        if (!hasValue)
        {
            result.AddError(ValidationCodes.ValueRequired, $"{path}.value", $"Operator {op} requires a value (use IsNull to test for missing facts).");
            return;
        }

        ValidateValue(op, fact.Type, value!.Value, $"{path}.value", result);
    }

    private void ValidateValue(ConditionOperator op, FactType type, JsonElement value, string path, RuleDefinitionValidation result)
    {
        switch (op)
        {
            case ConditionOperator.In:
            case ConditionOperator.NotIn:
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                {
                    result.AddError(ValidationCodes.ValueArrayRequired, path, $"Operator {op} requires a non-empty array of {type} values.");
                    return;
                }

                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (!FactValueConverter.IsCompatible(type, item))
                    {
                        result.AddError(ValidationCodes.ValueType, $"{path}[{index}]", $"Value must be a {type}.");
                    }

                    index++;
                }

                return;
            }

            case ConditionOperator.Between:
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
                {
                    result.AddError(ValidationCodes.BetweenArity, path, "Between requires an array of exactly two values: [low, high].");
                    return;
                }

                var low = value[0];
                var high = value[1];
                if (!FactValueConverter.IsCompatible(type, low) || !FactValueConverter.IsCompatible(type, high))
                {
                    result.AddError(ValidationCodes.ValueType, path, $"Between bounds must both be {type} values.");
                    return;
                }

                if (LeafOperatorEvaluator.Compare(type, low, high) > 0)
                {
                    result.AddError(ValidationCodes.BetweenOrder, path, "Between lower bound must be less than or equal to the upper bound.");
                }

                return;
            }

            case ConditionOperator.Matches:
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    result.AddError(ValidationCodes.ValueType, path, "Matches requires a regular expression string.");
                    return;
                }

                var pattern = value.GetString()!;
                if (pattern.Length > _options.MaxRegexLength)
                {
                    result.AddError(ValidationCodes.RegexTooLong, path, $"Regular expression is longer than {_options.MaxRegexLength} characters.");
                    return;
                }

                try
                {
                    _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(_options.RegexTimeoutMs));
                }
                catch (ArgumentException ex)
                {
                    result.AddError(ValidationCodes.RegexInvalid, path, "Regular expression does not compile: " + ex.Message);
                }

                return;
            }

            case ConditionOperator.Contains:
            case ConditionOperator.StartsWith:
            case ConditionOperator.EndsWith:
                if (value.ValueKind != JsonValueKind.String)
                {
                    result.AddError(ValidationCodes.ValueType, path, $"Operator {op} requires a string value.");
                }

                return;

            default:
                if (!FactValueConverter.IsCompatible(type, value))
                {
                    result.AddError(ValidationCodes.ValueType, path, $"Value must be a {type}.");
                }

                return;
        }
    }

    private void ValidateActions(FactSchema schema, JsonElement actions, RuleDefinitionValidation result)
    {
        if (actions.ValueKind != JsonValueKind.Array)
        {
            result.AddError(ValidationCodes.ActionsNotArray, "actions", "Actions must be a JSON array.");
            return;
        }

        var count = actions.GetArrayLength();
        if (count == 0)
        {
            result.AddError(ValidationCodes.ActionsEmpty, "actions", "A rule needs at least one action.");
        }

        if (count > _options.MaxActionsPerRule)
        {
            result.AddError(ValidationCodes.ActionsLimit, "actions", $"A rule may have at most {_options.MaxActionsPerRule} actions.");
        }

        var index = 0;
        foreach (var action in actions.EnumerateArray())
        {
            ValidateAction(schema, action, $"actions[{index}]", result);
            index++;
        }
    }

    private static void ValidateAction(FactSchema schema, JsonElement action, string path, RuleDefinitionValidation result)
    {
        if (action.ValueKind != JsonValueKind.Object)
        {
            result.AddError(ValidationCodes.ActionNotObject, path, "Each action must be a JSON object.");
            return;
        }

        foreach (var property in action.EnumerateObject())
        {
            if (!ActionProperties.Contains(property.Name))
            {
                result.AddError(ValidationCodes.UnknownProperty, $"{path}.{property.Name}", $"Unknown property '{property.Name}'.");
            }
        }

        var typeElement = Get(action, "type");
        if (typeElement is not { ValueKind: JsonValueKind.String }
            || !RuleActionTypes.TryParse(typeElement.Value.GetString(), out var type))
        {
            result.AddError(
                ValidationCodes.UnknownActionType,
                $"{path}.type",
                "Action type is missing or unknown. Allowed: " + string.Join(", ", RuleActionTypes.Names) + ".");
            return;
        }

        var parameters = Get(action, "parameters");
        if (parameters is not null && parameters.Value.ValueKind != JsonValueKind.Object)
        {
            result.AddError(ValidationCodes.ActionParameterInvalid, $"{path}.parameters", "'parameters' must be a JSON object.");
            return;
        }

        var specs = ActionParameterSpecs[type];
        var supplied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (parameters is not null)
        {
            foreach (var property in parameters.Value.EnumerateObject())
            {
                supplied[property.Name] = property.Value;
                if (!specs.Any(s => s.Name == property.Name))
                {
                    result.AddWarning(ValidationCodes.UnknownActionParameter, $"{path}.parameters.{property.Name}", $"Parameter '{property.Name}' is not used by {type} and will be ignored.");
                }
            }
        }

        foreach (var (name, required, maxLength) in specs)
        {
            var parameterPath = $"{path}.parameters.{name}";
            if (!supplied.TryGetValue(name, out var value))
            {
                if (required)
                {
                    result.AddError(ValidationCodes.ActionParameterMissing, parameterPath, $"{type} requires parameter '{name}'.");
                }

                continue;
            }

            if (type == RuleActionType.SetValue && name == ActionParameters.Value)
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                result.AddError(ValidationCodes.ActionParameterInvalid, parameterPath, $"Parameter '{name}' must be a non-empty string.");
                continue;
            }

            if (value.GetString()!.Length > maxLength)
            {
                result.AddError(ValidationCodes.ActionParameterInvalid, parameterPath, $"Parameter '{name}' must be at most {maxLength} characters.");
            }
        }

        if (type == RuleActionType.AddTag
            && supplied.TryGetValue(ActionParameters.Tag, out var tag)
            && tag.ValueKind == JsonValueKind.String
            && !TagPattern.IsMatch(tag.GetString() ?? string.Empty))
        {
            result.AddError(ValidationCodes.ActionParameterInvalid, $"{path}.parameters.tag", "Tags may contain letters, digits and _ . : - only, and must not start with punctuation.");
        }

        if (type == RuleActionType.SetValue
            && supplied.TryGetValue(ActionParameters.Field, out var target)
            && target.ValueKind == JsonValueKind.String)
        {
            var field = schema.Find(target.GetString()!);
            if (field is null)
            {
                result.AddError(ValidationCodes.UnknownField, $"{path}.parameters.field", $"SetValue target '{target.GetString()}' is not declared in the fact schema.");
            }
            else if (supplied.TryGetValue(ActionParameters.Value, out var newValue)
                     && newValue.ValueKind != JsonValueKind.Null
                     && !FactValueConverter.IsCompatible(field.Type, newValue))
            {
                result.AddError(ValidationCodes.ValueType, $"{path}.parameters.value", $"SetValue value must be a {field.Type}.");
            }
        }
    }

    private static void NormalizeOperators(ConditionNode node)
    {
        if (node.Operator is not null && ConditionOperators.TryParse(node.Operator, out var op))
        {
            node.Operator = op.ToString();
        }

        foreach (var child in node.GetChildren())
        {
            NormalizeOperators(child);
        }
    }

    private static JsonElement? Get(JsonElement obj, string name)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
