using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RuleEngine.Infrastructure.Persistence;

namespace RuleEngine.IntegrationTests;

/// <summary>
/// Hosts the real API in-process (TestServer) with SQL Server swapped for an in-memory SQLite database that lives
/// as long as the factory (one open connection). Users are the configured demo users; tokens are minted through
/// the real POST /api/auth/token endpoint.
/// </summary>
public sealed class RuleEngineApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "Integration#Pass1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public RuleEngineApiFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Database:EnsureCreatedOnStartup"] = "true",
                ["Database:ApplyMigrationsOnStartup"] = "false",
                ["Jwt:SigningKey"] = "integration-tests-signing-key-0123456789abcdef",
                ["RateLimiting:GenerationPermitsPerMinute"] = "1000",
                ["RuleEngine:RequirePassingTestsToPublish"] = "true",
                ["DemoUsers:Enabled"] = "true",
            };
            var users = new[] { ("admin", "RuleAdmin"), ("author", "RuleAuthor"), ("executor", "Executor"), ("auditor", "Auditor") };
            for (var i = 0; i < users.Length; i++)
            {
                settings[$"DemoUsers:Users:{i}:UserName"] = users[i].Item1;
                settings[$"DemoUsers:Users:{i}:Password"] = Password;
                settings[$"DemoUsers:Users:{i}:Roles:0"] = users[i].Item2;
            }

            configuration.AddInMemoryCollection(settings);
        });

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<RuleEngineDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<RuleEngineDbContext>(options => options.UseSqlite(_connection));
        });
    }

    /// <summary>Creates a client authenticated as one of the demo users (admin, author, executor, auditor).</summary>
    public async Task<HttpClient> ClientForAsync(string userName)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/token", new { userName, password = Password });
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var token = body["data"]!["accessToken"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}

internal static class HttpExtensions
{
    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    /// <summary>Reads a string at a property path, e.g. <c>json.Str("data", "id")</c>.</summary>
    public static string Str(this JsonNode node, params string[] path)
    {
        var current = node;
        foreach (var segment in path)
        {
            current = current[segment] ?? throw new InvalidOperationException($"Missing '{segment}' in {node.ToJsonString()}");
        }

        return current.GetValue<string>();
    }

    public static async Task<JsonNode> PostOkAsync(this HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var json = await response.ReadJsonAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"POST {url} returned {(int)response.StatusCode}: {json.ToJsonString()}");
        return json;
    }
}

/// <summary>Request payloads shared by the tests (mirrors samples/purchase-approval).</summary>
internal static class Payloads
{
    public static object RuleSet(string name) => new
    {
        name,
        description = "Purchase approval rules",
        strategy = "AllMatches",
        factSchema = new
        {
            fields = new object[]
            {
                new { path = "document.quantity", type = "Number", required = true, aliases = new[] { "qty" } },
                new { path = "document.amount", type = "Number", required = true, aliases = new[] { "total", "order value" } },
                new { path = "document.department", type = "String" },
                new { path = "document.documentType", type = "String", aliases = new[] { "doc type" } },
                new { path = "supplier.riskRating", type = "String", aliases = new[] { "supplier risk" } },
            },
        },
    };

    public static object LargeQuantityRule(string name = "Large quantity needs manager approval", int priority = 10) => new
    {
        name,
        priority,
        condition = new { field = "document.quantity", @operator = "GreaterThan", value = 10 },
        actions = new object[]
        {
            new { type = "RequireApproval", parameters = new { role = "Manager", reason = "Quantity above 10" } },
            new { type = "AddTag", parameters = new { tag = "large-order" } },
        },
    };

    public static object Facts(int quantity, decimal amount = 1200m, string department = "IT") => new
    {
        facts = new { document = new { quantity, amount, department, documentType = "PurchaseOrder" }, supplier = new { riskRating = "Low" } },
    };
}
