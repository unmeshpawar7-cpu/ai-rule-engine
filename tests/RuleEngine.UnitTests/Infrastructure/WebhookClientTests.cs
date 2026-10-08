using System.Net;
using RuleEngine.Application.Execution;
using RuleEngine.Domain.Enums;
using RuleEngine.Infrastructure.Actions;

namespace RuleEngine.UnitTests.Infrastructure;

public class WebhookClientTests
{
    private static WebhookClient Client(HttpStatusCode status) =>
        new(new HttpClient(new LlmRuleGeneratorTests.StubHandler(_ => new HttpResponseMessage(status))));

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Retryable_statuses_raise_transient_errors(HttpStatusCode status)
    {
        var act = () => Client(status).PostAsync(new Uri("https://hooks.test/x"), new { a = 1 }, CancellationToken.None);

        await act.Should().ThrowAsync<TransientActionException>();
    }

    [Fact]
    public async Task Client_errors_are_permanent_failures()
    {
        var result = await Client(HttpStatusCode.BadRequest).PostAsync(new Uri("https://hooks.test/x"), new { a = 1 }, CancellationToken.None);

        result.Status.Should().Be(ActionExecutionStatus.Failed);
    }

    [Fact]
    public async Task Success_statuses_succeed()
    {
        var result = await Client(HttpStatusCode.Accepted).PostAsync(new Uri("https://hooks.test/x"), new { a = 1 }, CancellationToken.None);

        result.Status.Should().Be(ActionExecutionStatus.Succeeded);
    }

    [Fact]
    public void Only_configured_absolute_http_endpoints_resolve()
    {
        var endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["finance"] = "https://hooks.test/finance",
            ["bad"] = "file:///etc/passwd",
        };

        WebhookClient.ResolveEndpoint(endpoints, "FINANCE").Should().Be(new Uri("https://hooks.test/finance"));
        WebhookClient.ResolveEndpoint(endpoints, "bad").Should().BeNull();
        WebhookClient.ResolveEndpoint(endpoints, "unknown").Should().BeNull();
    }
}
