using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Conditions;

namespace RuleEngine.Application.Validation;

/// <summary>A validation finding with a stable machine-readable code and a JSON path.</summary>
public sealed record ValidationIssue(string Code, string Path, string Message);

public static class ValidationCodes
{
    public const string InvalidJson = "INVALID_JSON";
    public const string ConditionNotObject = "CONDITION_NOT_OBJECT";
    public const string NodeShape = "NODE_SHAPE";
    public const string EmptyGroup = "EMPTY_GROUP";
    public const string UnknownProperty = "UNKNOWN_PROPERTY";
    public const string DepthLimit = "DEPTH_LIMIT";
    public const string NodeLimit = "NODE_LIMIT";
    public const string UnknownField = "UNKNOWN_FIELD";
    public const string UnknownOperator = "UNKNOWN_OPERATOR";
    public const string OperatorTypeMismatch = "OPERATOR_TYPE_MISMATCH";
    public const string ValueRequired = "VALUE_REQUIRED";
    public const string ValueNotAllowed = "VALUE_NOT_ALLOWED";
    public const string ValueType = "VALUE_TYPE";
    public const string ValueArrayRequired = "VALUE_ARRAY_REQUIRED";
    public const string BetweenArity = "BETWEEN_ARITY";
    public const string BetweenOrder = "BETWEEN_ORDER";
    public const string RegexInvalid = "REGEX_INVALID";
    public const string RegexTooLong = "REGEX_TOO_LONG";
    public const string IgnoreCaseIgnored = "IGNORECASE_IGNORED";
    public const string ActionsNotArray = "ACTIONS_NOT_ARRAY";
    public const string ActionsEmpty = "ACTIONS_EMPTY";
    public const string ActionsLimit = "ACTIONS_LIMIT";
    public const string ActionNotObject = "ACTION_NOT_OBJECT";
    public const string UnknownActionType = "UNKNOWN_ACTION_TYPE";
    public const string ActionParameterMissing = "ACTION_PARAMETER_MISSING";
    public const string ActionParameterInvalid = "ACTION_PARAMETER_INVALID";
    public const string UnknownActionParameter = "UNKNOWN_ACTION_PARAMETER";
    public const string Conflict = "CONFLICT";
}

/// <summary>Result of validating a condition tree + action list against a fact schema.</summary>
public sealed class RuleDefinitionValidation
{
    public List<ValidationIssue> Errors { get; } = new();

    public List<ValidationIssue> Warnings { get; } = new();

    /// <summary>Parsed and normalised condition (only when valid).</summary>
    public ConditionNode? Condition { get; set; }

    /// <summary>Parsed and normalised actions (only when valid).</summary>
    public List<RuleActionDefinition>? Actions { get; set; }

    public bool IsValid => Errors.Count == 0;

    public string NormalizedConditionJson =>
        ConditionSerializer.Serialize(Condition ?? throw new InvalidOperationException("Validation failed; no condition available."));

    public string NormalizedActionsJson =>
        RuleActionSerializer.Serialize(Actions ?? throw new InvalidOperationException("Validation failed; no actions available."));

    public void AddError(string code, string path, string message) => Errors.Add(new ValidationIssue(code, path, message));

    public void AddWarning(string code, string path, string message) => Warnings.Add(new ValidationIssue(code, path, message));
}
