using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace RuleEngine.IntegrationTests;

public class RuleLifecycleTests : IClassFixture<RuleEngineApiFactory>
{
    private readonly RuleEngineApiFactory _factory;

    public RuleLifecycleTests(RuleEngineApiFactory factory)
    {
        _factory = factory;
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<(HttpClient Admin, string RuleSetId)> NewRuleSetAsync()
    {
        var admin = await _factory.ClientForAsync("admin");
        var created = await admin.PostOkAsync("/api/rulesets", Payloads.RuleSet(Unique("purchase-approval")));
        return (admin, created.Str("data", "id"));
    }

    [Fact]
    public async Task Ruleset_to_execution_end_to_end_with_audit_trail()
    {
        var (admin, ruleSetId) = await NewRuleSetAsync();
        var author = await _factory.ClientForAsync("author");
        var executor = await _factory.ClientForAsync("executor");
        var auditor = await _factory.ClientForAsync("auditor");

        // Author drafts the rule and a regression test.
        var createRule = await author.PostAsJsonAsync($"/api/rulesets/{ruleSetId}/rules", Payloads.LargeQuantityRule());
        createRule.StatusCode.Should().Be(HttpStatusCode.Created);
        createRule.Headers.Location.Should().NotBeNull();
        var ruleId = (await createRule.ReadJsonAsync()).Str("data", "rule", "id");
        await author.PostOkAsync($"/api/rules/{ruleId}/tests", new
        {
            name = "25 units need approval",
            inputFacts = new { document = new { quantity = 25, amount = 100 } },
            expectedMatch = true,
            expectedActionTypes = new[] { "RequireApproval", "AddTag" },
        });
        var run = await author.PostOkAsync($"/api/rules/{ruleId}/tests/run", new { });
        run["data"]!["failed"]!.GetValue<int>().Should().Be(0);

        // Admin publishes (tests are re-run as a gate) and activates.
        var published = await admin.PostOkAsync($"/api/rules/{ruleId}/publish", new { versionNumber = 1 });
        published["data"]!["currentPublishedVersionNumber"]!.GetValue<int>().Should().Be(1);
        await admin.PostOkAsync($"/api/rules/{ruleId}/activate", new { });

        // Executor runs the rule set.
        var execute = await executor.PostAsJsonAsync($"/api/rulesets/{ruleSetId}/execute", Payloads.Facts(quantity: 25));
        execute.StatusCode.Should().Be(HttpStatusCode.Created);
        var execution = (await execute.ReadJsonAsync())["data"]!;
        execution["decision"]!.GetValue<string>().Should().Be("ApprovalRequired");
        execution["status"]!.GetValue<string>().Should().Be("Succeeded");
        execution["tags"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("large-order");
        execution["actions"]!.AsArray().Should().HaveCount(2);
        var leaf = execution["trace"]![0]!["condition"]!;
        leaf["actual"]!.GetValue<string>().Should().Be("25");
        leaf["expected"]!.GetValue<string>().Should().Be("10");
        var executionId = execution["executionId"]!.GetValue<string>();

        // The execution is persisted and readable.
        var stored = await executor.GetAsync($"/api/executions/{executionId}");
        stored.StatusCode.Should().Be(HttpStatusCode.OK);
        (await stored.ReadJsonAsync())["data"]!["matchedRules"]!.AsArray().Should().ContainSingle();

        // Every step is audited.
        var audit = await auditor.GetAsync($"/api/audit-logs?entityId={ruleId}&pageSize=50");
        var actions = (await audit.ReadJsonAsync())["data"]!["items"]!.AsArray().Select(a => a!["action"]!.GetValue<string>()).ToList();
        actions.Should().Contain(new[] { "Created", "Published", "Activated" });
        var executionAudit = await auditor.GetAsync($"/api/audit-logs?entityType=RuleExecution&entityId={executionId}");
        (await executionAudit.ReadJsonAsync())["data"]!["totalCount"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Idempotency_key_replays_and_detects_payload_changes()
    {
        var (admin, ruleSetId) = await NewRuleSetAsync();
        var ruleId = (await admin.PostOkAsync($"/api/rulesets/{ruleSetId}/rules", Payloads.LargeQuantityRule())).Str("data", "rule", "id");
        await admin.PostOkAsync($"/api/rules/{ruleId}/publish", new { versionNumber = 1 });
        await admin.PostOkAsync($"/api/rules/{ruleId}/activate", new { });
        var executor = await _factory.ClientForAsync("executor");

        async Task<HttpResponseMessage> Execute(int quantity)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/rulesets/{ruleSetId}/execute") { Content = JsonContent.Create(Payloads.Facts(quantity)) };
            request.Headers.Add("Idempotency-Key", "order-4711");
            return await executor.SendAsync(request);
        }

        var first = await Execute(25);
        var replay = await Execute(25);
        var changed = await Execute(26);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayData = (await replay.ReadJsonAsync())["data"]!;
        replayData["replayed"]!.GetValue<bool>().Should().BeTrue();
        replayData["executionId"]!.GetValue<string>().Should().Be((await first.ReadJsonAsync()).Str("data", "executionId"));
        changed.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var list = await executor.GetAsync($"/api/executions?ruleSetId={ruleSetId}");
        (await list.ReadJsonAsync())["data"]!["totalCount"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Simulation_evaluates_drafts_without_side_effects()
    {
        var (admin, ruleSetId) = await NewRuleSetAsync();
        await admin.PostOkAsync($"/api/rulesets/{ruleSetId}/rules", Payloads.LargeQuantityRule());
        var author = await _factory.ClientForAsync("author");

        var simulation = await author.PostOkAsync($"/api/rulesets/{ruleSetId}/simulate", Payloads.Facts(quantity: 25));

        var data = simulation["data"]!;
        data["isSimulation"]!.GetValue<bool>().Should().BeTrue();
        data["decision"]!.GetValue<string>().Should().Be("ApprovalRequired");
        data["matchedRules"]![0]!["versionStatus"]!.GetValue<string>().Should().Be("Draft");
        data["actions"]!.AsArray().Select(a => a!["status"]!.GetValue<string>()).Should().OnlyContain(s => s == "NotExecuted");

        var executions = await admin.GetAsync($"/api/executions?ruleSetId={ruleSetId}");
        (await executions.ReadJsonAsync())["data"]!["totalCount"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Generated_rules_are_saved_as_drafts_and_never_executed()
    {
        var (admin, ruleSetId) = await NewRuleSetAsync();
        var author = await _factory.ClientForAsync("author");

        var generate = await author.PostAsJsonAsync(
            $"/api/rulesets/{ruleSetId}/rules/generate",
            new { text = "Documents with quantity greater than 10 should require manager approval." });

        generate.StatusCode.Should().Be(HttpStatusCode.Created);
        var data = (await generate.ReadJsonAsync())["data"]!;
        data["outcome"]!.GetValue<string>().Should().Be("DraftSaved");
        data["versionStatus"]!.GetValue<string>().Should().Be("Draft");
        data["generator"]!.GetValue<string>().Should().Be("pattern");
        data["proposal"]!["condition"]!["field"]!.GetValue<string>().Should().Be("document.quantity");

        var rule = (await (await author.GetAsync($"/api/rules/{data["ruleId"]!.GetValue<string>()}")).ReadJsonAsync())["data"]!;
        rule["isActive"]!.GetValue<bool>().Should().BeFalse();
        rule["versions"]![0]!["status"]!.GetValue<string>().Should().Be("Draft");
        rule["versions"]![0]!["source"]!.GetValue<string>().Should().Be("AiGenerated");
        rule["versions"]![0]!["promptVersion"]!.GetValue<string>().Should().Be("pattern-grammar/v1");

        var executor = await _factory.ClientForAsync("executor");
        var execution = await executor.PostOkAsync($"/api/rulesets/{ruleSetId}/execute", Payloads.Facts(quantity: 25));
        execution["data"]!["decision"]!.GetValue<string>().Should().Be("NoMatch");
    }

    [Fact]
    public async Task Text_the_grammar_cannot_interpret_returns_422_and_stores_nothing()
    {
        var (admin, ruleSetId) = await NewRuleSetAsync();

        var response = await admin.PostAsJsonAsync($"/api/rulesets/{ruleSetId}/rules/generate", new { text = "If colour is red then reject" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var json = await response.ReadJsonAsync();
        json["success"]!.GetValue<bool>().Should().BeFalse();
        json["data"]!["outcome"]!.GetValue<string>().Should().Be("NotUnderstood");
        json["errors"]!.AsArray().Should().NotBeEmpty();
        var rules = await admin.GetAsync($"/api/rules?ruleSetId={ruleSetId}");
        (await rules.ReadJsonAsync())["data"]!["totalCount"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Invalid_requests_return_the_400_envelope()
    {
        var admin = await _factory.ClientForAsync("admin");

        var invalid = await admin.PostAsJsonAsync("/api/rulesets", new { name = "", strategy = "AllMatches", factSchema = new { fields = Array.Empty<object>() } });
        var malformed = await admin.PostAsync("/api/rulesets", new StringContent("{ not json", Encoding.UTF8, "application/json"));

        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await invalid.ReadJsonAsync();
        json["success"]!.GetValue<bool>().Should().BeFalse();
        json["message"]!.GetValue<string>().Should().Be("Validation failed");
        json["errors"]!.AsArray().Select(e => e!["field"]!.GetValue<string>()).Should().Contain("name");
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await malformed.ReadJsonAsync())["success"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_rule_definitions_return_422_with_codes()
    {
        var (admin, ruleSetId) = await NewRuleSetAsync();

        var response = await admin.PostAsJsonAsync($"/api/rulesets/{ruleSetId}/rules", new
        {
            name = "bad rule",
            condition = new { field = "document.colour", @operator = "Equals", value = "red" },
            actions = new[] { new { type = "Reject", parameters = new { reason = "red" } } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var error = (await response.ReadJsonAsync())["errors"]![0]!;
        error["code"]!.GetValue<string>().Should().Be("UNKNOWN_FIELD");
        error["field"]!.GetValue<string>().Should().Be("condition.field");
    }

    [Fact]
    public async Task Duplicate_names_and_stale_concurrency_stamps_return_409()
    {
        var admin = await _factory.ClientForAsync("admin");
        var name = Unique("duplicate");
        var created = await admin.PostOkAsync("/api/rulesets", Payloads.RuleSet(name));

        var duplicate = await admin.PostAsJsonAsync("/api/rulesets", Payloads.RuleSet(name));
        var stale = await admin.PutAsJsonAsync($"/api/rulesets/{created.Str("data", "id")}", new
        {
            description = "changed",
            strategy = "FirstMatch",
            expectedConcurrencyStamp = Guid.NewGuid(),
        });

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await duplicate.ReadJsonAsync())["success"]!.GetValue<bool>().Should().BeFalse();
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Failing_tests_block_publishing()
    {
        var (admin, ruleSetId) = await NewRuleSetAsync();
        var ruleId = (await admin.PostOkAsync($"/api/rulesets/{ruleSetId}/rules", Payloads.LargeQuantityRule())).Str("data", "rule", "id");
        await admin.PostOkAsync($"/api/rules/{ruleId}/tests", new
        {
            name = "wrong expectation",
            inputFacts = new { document = new { quantity = 50, amount = 1 } },
            expectedMatch = false,
        });

        var publish = await admin.PostAsJsonAsync($"/api/rules/{ruleId}/publish", new { versionNumber = 1 });

        publish.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await publish.ReadJsonAsync())["errors"]![0]!["code"]!.GetValue<string>().Should().Be("TEST_FAILED");
    }

    [Fact]
    public async Task Lists_are_paginated_and_page_size_is_capped()
    {
        var admin = await _factory.ClientForAsync("admin");
        var prefix = Unique("paging");
        foreach (var suffix in new[] { "a", "b", "c" })
        {
            await admin.PostOkAsync("/api/rulesets", Payloads.RuleSet($"{prefix}-{suffix}"));
        }

        var page2 = (await (await admin.GetAsync($"/api/rulesets?search={prefix}&page=2&pageSize=2")).ReadJsonAsync())["data"]!;
        var capped = (await (await admin.GetAsync($"/api/rulesets?search={prefix}&pageSize=1000")).ReadJsonAsync())["data"]!;

        page2["items"]!.AsArray().Should().ContainSingle();
        page2["items"]![0]!["name"]!.GetValue<string>().Should().Be($"{prefix}-c");
        page2["page"]!.GetValue<int>().Should().Be(2);
        page2["totalCount"]!.GetValue<int>().Should().Be(3);
        page2["totalPages"]!.GetValue<int>().Should().Be(2);
        capped["pageSize"]!.GetValue<int>().Should().Be(100);
    }

    [Fact]
    public async Task Unknown_resources_return_404_envelope()
    {
        var admin = await _factory.ClientForAsync("admin");

        var response = await admin.GetAsync($"/api/rules/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.ReadJsonAsync())["success"]!.GetValue<bool>().Should().BeFalse();
    }
}
