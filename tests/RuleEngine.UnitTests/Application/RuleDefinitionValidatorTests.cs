using Microsoft.Extensions.Options;
using RuleEngine.Application.Common;
using RuleEngine.Application.Validation;

namespace RuleEngine.UnitTests.Application;

public class RuleDefinitionValidatorTests
{
    private const string ValidCondition = """{ "field": "document.quantity", "operator": "GreaterThan", "value": 10 }""";
    private const string ValidActions = """[ { "type": "RequireApproval", "parameters": { "role": "Manager" } } ]""";

    private static RuleDefinitionValidator Validator(RuleEngineOptions? options = null) => new(Options.Create(options ?? new RuleEngineOptions()));

    [Theory]
    [InlineData("""{ "field": "document.colour", "operator": "Equals", "value": "red" }""", ValidationCodes.UnknownField)]
    [InlineData("""{ "field": "document.quantity", "operator": "Bigger", "value": 1 }""", ValidationCodes.UnknownOperator)]
    [InlineData("""{ "field": "document.department", "operator": "GreaterThan", "value": 1 }""", ValidationCodes.OperatorTypeMismatch)]
    [InlineData("""{ "field": "document.quantity", "operator": "Equals", "value": "ten" }""", ValidationCodes.ValueType)]
    [InlineData("""{ "field": "document.quantity", "operator": "Between", "value": [20, 10] }""", ValidationCodes.BetweenOrder)]
    [InlineData("""{ "field": "document.quantity", "operator": "Between", "value": [1] }""", ValidationCodes.BetweenArity)]
    [InlineData("""{ "field": "document.documentType", "operator": "Matches", "value": "(unclosed" }""", ValidationCodes.RegexInvalid)]
    [InlineData("""{ "field": "document.quantity", "operator": "In", "value": [] }""", ValidationCodes.ValueArrayRequired)]
    [InlineData("""{ "field": "document.quantity", "operator": "IsNull", "value": 1 }""", ValidationCodes.ValueNotAllowed)]
    [InlineData("""{ "field": "document.quantity", "operator": "Equals" }""", ValidationCodes.ValueRequired)]
    [InlineData("""{ "all": [] }""", ValidationCodes.EmptyGroup)]
    [InlineData("""{ "all": [], "any": [] }""", ValidationCodes.NodeShape)]
    [InlineData("""{ "field": "document.quantity", "operator": "Equals", "value": 1, "extra": true }""", ValidationCodes.UnknownProperty)]
    [InlineData("""[ 1, 2 ]""", ValidationCodes.ConditionNotObject)]
    [InlineData("""{ "field": "document.dueDate", "operator": "GreaterThan", "value": "not-a-date" }""", ValidationCodes.ValueType)]
    public void Invalid_conditions_are_rejected_with_a_code(string condition, string expectedCode)
    {
        var result = Validator().Validate(TestData.PurchaseSchema(), condition, ValidActions);

        result.IsValid.Should().BeFalse();
        result.Errors.Select(e => e.Code).Should().Contain(expectedCode);
    }

    [Theory]
    [InlineData("""[]""", ValidationCodes.ActionsEmpty)]
    [InlineData("""{}""", ValidationCodes.ActionsNotArray)]
    [InlineData("""[ { "type": "Explode" } ]""", ValidationCodes.UnknownActionType)]
    [InlineData("""[ { "type": "RequireApproval", "parameters": {} } ]""", ValidationCodes.ActionParameterMissing)]
    [InlineData("""[ { "type": "Reject", "parameters": { "reason": 42 } } ]""", ValidationCodes.ActionParameterInvalid)]
    [InlineData("""[ { "type": "SetValue", "parameters": { "field": "document.unknown", "value": 1 } } ]""", ValidationCodes.UnknownField)]
    [InlineData("""[ { "type": "SetValue", "parameters": { "field": "document.quantity", "value": "many" } } ]""", ValidationCodes.ValueType)]
    [InlineData("""[ { "type": "AddTag", "parameters": { "tag": "-bad tag" } } ]""", ValidationCodes.ActionParameterInvalid)]
    [InlineData("""[ "RequireApproval" ]""", ValidationCodes.ActionNotObject)]
    public void Invalid_actions_are_rejected_with_a_code(string actions, string expectedCode)
    {
        var result = Validator().Validate(TestData.PurchaseSchema(), ValidCondition, actions);

        result.IsValid.Should().BeFalse();
        result.Errors.Select(e => e.Code).Should().Contain(expectedCode);
    }

    [Fact]
    public void Errors_carry_json_paths()
    {
        var condition = """{ "all": [ { "field": "document.quantity", "operator": "GreaterThan", "value": 1 }, { "field": "nope", "operator": "Equals", "value": 1 } ] }""";

        var result = Validator().Validate(TestData.PurchaseSchema(), condition, ValidActions);

        result.Errors.Should().ContainSingle().Which.Path.Should().Be("condition.all[1].field");
    }

    [Fact]
    public void Depth_and_node_limits_are_enforced()
    {
        var deep = """{ "not": { "not": { "field": "document.quantity", "operator": "GreaterThan", "value": 1 } } }""";
        Validator(new RuleEngineOptions { MaxConditionDepth = 2 }).Validate(TestData.PurchaseSchema(), deep, ValidActions)
            .Errors.Select(e => e.Code).Should().Contain(ValidationCodes.DepthLimit);

        var wide = """{ "all": [ { "field": "document.quantity", "operator": "GreaterThan", "value": 1 }, { "field": "document.amount", "operator": "GreaterThan", "value": 1 } ] }""";
        Validator(new RuleEngineOptions { MaxConditionNodes = 2 }).Validate(TestData.PurchaseSchema(), wide, ValidActions)
            .Errors.Select(e => e.Code).Should().Contain(ValidationCodes.NodeLimit);
    }

    [Fact]
    public void Overlong_regex_is_rejected()
    {
        var condition = $$"""{ "field": "document.documentType", "operator": "Matches", "value": "{{new string('a', 300)}}" }""";

        Validator().Validate(TestData.PurchaseSchema(), condition, ValidActions)
            .Errors.Select(e => e.Code).Should().Contain(ValidationCodes.RegexTooLong);
    }

    [Fact]
    public void Valid_definition_is_normalised()
    {
        var condition = """{ "field": "document.quantity", "operator": "greaterthan", "value": 10 }""";
        var actions = """[ { "type": "requireapproval", "parameters": { "role": "Manager" } } ]""";

        var result = Validator().Validate(TestData.PurchaseSchema(), condition, actions);

        result.IsValid.Should().BeTrue();
        result.NormalizedConditionJson.Should().Contain("\"GreaterThan\"");
        result.NormalizedActionsJson.Should().Contain("\"RequireApproval\"");
    }

    [Fact]
    public void Unknown_action_parameters_only_warn()
    {
        var actions = """[ { "type": "AddTag", "parameters": { "tag": "bulk", "colour": "red" } } ]""";

        var result = Validator().Validate(TestData.PurchaseSchema(), ValidCondition, actions);

        result.IsValid.Should().BeTrue();
        result.Warnings.Select(w => w.Code).Should().Contain(ValidationCodes.UnknownActionParameter);
    }
}
