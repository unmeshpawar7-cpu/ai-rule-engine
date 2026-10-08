using System.Text.Json;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;

namespace RuleEngine.UnitTests.Domain;

public class LeafOperatorEvaluatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    [Theory]
    [InlineData(ConditionOperator.Equals, FactType.Number, "10", "10.0", true)]
    [InlineData(ConditionOperator.Equals, FactType.Number, "\"10\"", "10", true)]
    [InlineData(ConditionOperator.Equals, FactType.String, "\"IT\"", "\"it\"", false)]
    [InlineData(ConditionOperator.Equals, FactType.Boolean, "true", "\"true\"", true)]
    [InlineData(ConditionOperator.Equals, FactType.Date, "\"2026-03-01T00:00:00Z\"", "\"2026-03-01\"", true)]
    [InlineData(ConditionOperator.NotEquals, FactType.String, "\"IT\"", "\"HR\"", true)]
    [InlineData(ConditionOperator.GreaterThan, FactType.Number, "11", "10", true)]
    [InlineData(ConditionOperator.GreaterThan, FactType.Number, "10", "10", false)]
    [InlineData(ConditionOperator.GreaterThanOrEqual, FactType.Number, "10", "10", true)]
    [InlineData(ConditionOperator.LessThan, FactType.Number, "9.99", "10", true)]
    [InlineData(ConditionOperator.LessThanOrEqual, FactType.Number, "10", "10", true)]
    [InlineData(ConditionOperator.GreaterThan, FactType.Date, "\"2026-03-01\"", "\"2026-02-28\"", true)]
    [InlineData(ConditionOperator.Contains, FactType.String, "\"urgent order\"", "\"urgent\"", true)]
    [InlineData(ConditionOperator.Contains, FactType.StringList, "[\"a\",\"b\"]", "\"b\"", true)]
    [InlineData(ConditionOperator.Contains, FactType.StringList, "[\"a\",\"b\"]", "\"c\"", false)]
    [InlineData(ConditionOperator.StartsWith, FactType.String, "\"PO-123\"", "\"PO-\"", true)]
    [InlineData(ConditionOperator.EndsWith, FactType.String, "\"report.pdf\"", "\".pdf\"", true)]
    [InlineData(ConditionOperator.In, FactType.String, "\"HR\"", "[\"IT\",\"HR\"]", true)]
    [InlineData(ConditionOperator.In, FactType.Number, "5", "[1,5]", true)]
    [InlineData(ConditionOperator.NotIn, FactType.String, "\"Ops\"", "[\"IT\",\"HR\"]", true)]
    [InlineData(ConditionOperator.NotIn, FactType.String, "\"IT\"", "[\"IT\",\"HR\"]", false)]
    [InlineData(ConditionOperator.Between, FactType.Number, "15", "[10,20]", true)]
    [InlineData(ConditionOperator.Between, FactType.Number, "10", "[10,20]", true)]
    [InlineData(ConditionOperator.Between, FactType.Number, "20.01", "[10,20]", false)]
    [InlineData(ConditionOperator.Matches, FactType.String, "\"PO-1234\"", "\"^PO-\\\\d{4}$\"", true)]
    [InlineData(ConditionOperator.Matches, FactType.String, "\"PO-12\"", "\"^PO-\\\\d{4}$\"", false)]
    public void Operators_follow_typed_semantics(ConditionOperator op, FactType type, string actual, string expected, bool result)
    {
        var outcome = LeafOperatorEvaluator.Evaluate(op, type, TestData.Json(actual), TestData.Json(expected), ignoreCase: false, Timeout);

        outcome.Result.Should().Be(result);
    }

    [Fact]
    public void Missing_fact_only_satisfies_IsNull()
    {
        LeafOperatorEvaluator.Evaluate(ConditionOperator.IsNull, FactType.String, null, null, false, Timeout).Result.Should().BeTrue();
        LeafOperatorEvaluator.Evaluate(ConditionOperator.IsNotNull, FactType.String, null, null, false, Timeout).Result.Should().BeFalse();

        var outcome = LeafOperatorEvaluator.Evaluate(ConditionOperator.NotEquals, FactType.String, null, TestData.Json("\"IT\""), false, Timeout);
        outcome.Result.Should().BeFalse();
        outcome.Note.Should().Contain("missing");
    }

    [Fact]
    public void Json_null_counts_as_missing()
    {
        LeafOperatorEvaluator.Evaluate(ConditionOperator.IsNull, FactType.Number, TestData.Json("null"), null, false, Timeout).Result.Should().BeTrue();
    }

    [Fact]
    public void Uncoercible_values_never_match_and_say_why()
    {
        var outcome = LeafOperatorEvaluator.Evaluate(ConditionOperator.GreaterThan, FactType.Number, TestData.Json("\"abc\""), TestData.Json("10"), false, Timeout);

        outcome.Result.Should().BeFalse();
        outcome.Note.Should().Contain("Number");
    }

    [Fact]
    public void IgnoreCase_applies_to_string_comparisons()
    {
        LeafOperatorEvaluator.Evaluate(ConditionOperator.Equals, FactType.String, TestData.Json("\"IT\""), TestData.Json("\"it\""), true, Timeout).Result.Should().BeTrue();
        LeafOperatorEvaluator.Evaluate(ConditionOperator.In, FactType.String, TestData.Json("\"hr\""), TestData.Json("[\"IT\",\"HR\"]"), true, Timeout).Result.Should().BeTrue();
    }

    [Fact]
    public void Between_requires_a_two_element_array()
    {
        var outcome = LeafOperatorEvaluator.Evaluate(ConditionOperator.Between, FactType.Number, TestData.Json("5"), TestData.Json("[1]"), false, Timeout);

        outcome.Result.Should().BeFalse();
        outcome.Note.Should().Contain("[low, high]");
    }

    [Fact]
    public void Invalid_regex_does_not_throw()
    {
        var outcome = LeafOperatorEvaluator.Evaluate(ConditionOperator.Matches, FactType.String, TestData.Json("\"x\""), TestData.Json("\"(unclosed\""), false, Timeout);

        outcome.Result.Should().BeFalse();
        outcome.Note.Should().Contain("Invalid regular expression");
    }

    [Fact]
    public void Catastrophic_regex_times_out_instead_of_hanging()
    {
        var input = JsonSerializer.SerializeToElement(new string('a', 40) + "!");
        var outcome = LeafOperatorEvaluator.Evaluate(
            ConditionOperator.Matches,
            FactType.String,
            input,
            TestData.Json("\"^(a+)+$\""),
            false,
            TimeSpan.FromMilliseconds(5));

        outcome.Result.Should().BeFalse();
        outcome.Note.Should().Contain("timed out");
    }
}
