using System.Net;
using System.Net.Http.Json;

namespace RuleEngine.IntegrationTests;

public class SecurityTests : IClassFixture<RuleEngineApiFactory>
{
    private readonly RuleEngineApiFactory _factory;

    public SecurityTests(RuleEngineApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_requests_get_401_envelope()
    {
        var response = await _factory.CreateClient().GetAsync("/api/rulesets");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var json = await response.ReadJsonAsync();
        json["success"]!.GetValue<bool>().Should().BeFalse();
        json["traceId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Wrong_password_is_rejected_with_401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/token", new { userName = "admin", password = "wrong-password" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.ReadJsonAsync())["message"]!.GetValue<string>().Should().Contain("Invalid user name or password");
    }

    [Fact]
    public async Task Token_contains_roles()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/token", new { userName = "author", password = RuleEngineApiFactory.Password });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await response.ReadJsonAsync())["data"]!;
        data["tokenType"]!.GetValue<string>().Should().Be("Bearer");
        data["roles"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Equal("RuleAuthor");
    }

    [Theory]
    [InlineData("auditor", "POST", "/api/rulesets")]
    [InlineData("executor", "POST", "/api/rulesets")]
    [InlineData("author", "GET", "/api/audit-logs")]
    [InlineData("author", "GET", "/api/executions")]
    public async Task Roles_are_enforced_with_403(string user, string method, string url)
    {
        var client = await _factory.ClientForAsync(user);

        var response = method == "POST"
            ? await client.PostAsJsonAsync(url, Payloads.RuleSet("forbidden-" + Guid.NewGuid().ToString("N")))
            : await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.ReadJsonAsync())["success"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Authors_cannot_publish_rules()
    {
        var admin = await _factory.ClientForAsync("admin");
        var ruleSet = await admin.PostOkAsync("/api/rulesets", Payloads.RuleSet("publish-guard-" + Guid.NewGuid().ToString("N")));
        var rule = await admin.PostOkAsync($"/api/rulesets/{ruleSet.Str("data", "id")}/rules", Payloads.LargeQuantityRule());
        var author = await _factory.ClientForAsync("author");

        var response = await author.PostAsJsonAsync($"/api/rules/{rule.Str("data", "rule", "id")}/publish", new { versionNumber = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_are_anonymous(string url)
    {
        var response = await _factory.CreateClient().GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
