using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;
using RuleEngine.Domain.Facts;
using RuleEngine.Infrastructure.Persistence;

namespace RuleEngine.UnitTests;

/// <summary>Deterministic clock for tests.</summary>
internal sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class TestData
{
    /// <summary>The purchase-approval schema used across tests (mirrors samples/purchase-approval).</summary>
    public static FactSchema PurchaseSchema() => new()
    {
        Fields =
        {
            new FactField { Path = "document.quantity", Type = FactType.Number, Required = true, Aliases = { "qty", "units", "number of items" } },
            new FactField { Path = "document.amount", Type = FactType.Number, Required = true, Aliases = { "total", "order value", "order amount", "value" } },
            new FactField { Path = "document.department", Type = FactType.String, Aliases = { "dept", "cost center" } },
            new FactField { Path = "document.documentType", Type = FactType.String, Aliases = { "doc type", "type" } },
            new FactField { Path = "document.tags", Type = FactType.StringList },
            new FactField { Path = "document.dueDate", Type = FactType.Date, Aliases = { "due date", "deadline" } },
            new FactField { Path = "supplier.riskRating", Type = FactType.String, Aliases = { "supplier risk", "risk" } },
            new FactField { Path = "supplier.preferred", Type = FactType.Boolean, Aliases = { "preferred supplier" } },
            new FactField { Path = "routing.priority", Type = FactType.String },
        },
    };

    public static JsonObject Facts(string json) => JsonNode.Parse(json)!.AsObject();

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static EvaluableRule Rule(string name, int priority, ConditionNode condition, params RuleActionDefinition[] actions) => new()
    {
        RuleId = Guid.NewGuid(),
        RuleName = name,
        VersionId = Guid.NewGuid(),
        VersionNumber = 1,
        Priority = priority,
        Condition = condition,
        Actions = actions,
    };

    public static ConditionNode Leaf(string field, ConditionOperator op, JsonNode? value = null) => ConditionNode.Leaf(field, op, value);

    public static RuleEngineDbContext NewDb() =>
        new(new DbContextOptionsBuilder<RuleEngineDbContext>().UseInMemoryDatabase("unit-" + Guid.NewGuid().ToString("N")).Options);

    public static ICurrentUser User(string name = "tester", string correlationId = "corr-test")
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserName.Returns(name);
        user.CorrelationId.Returns(correlationId);
        return user;
    }
}
