using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RuleEngine.Application.Common;
using RuleEngine.Application.Generation;
using RuleEngine.Application.Validation;
using RuleEngine.Domain.Enums;
using RuleEngine.Infrastructure.Ai;

namespace RuleEngine.UnitTests.Infrastructure;

public class LlmRuleGeneratorTests
{
    private const string ValidRule = """
        { "name": "Large quantity approval",
          "condition": { "field": "document.quantity", "operator": "GreaterThan", "value": 10 },
          "actions": [ { "type": "RequireApproval", "parameters": { "role": "Manager" } } ],
          "explanation": "Quantities above 10 need a manager.",
          "confidence": 0.92,
          "assumptions": ["'quantity' means document.quantity"] }
        """;

    private const string HallucinatedRule = """
        { "name": "Colour", "condition": { "field": "document.colour", "operator": "Equals", "value": "red" },
          "actions": [ { "type": "Reject", "parameters": { "reason": "red" } } ], "confidence": 0.8 }
        """;

    private static readonly LlmOptions LlmSettings = new() { Provider = LlmOptions.OpenAi, OpenAiApiKey = "test-key", MaxAttempts = 2 };

    private static RuleGenerationRequest Request() =>
        new("Documents with lots of items need a manager", TestData.PurchaseSchema(), "purchase-approval");

    private static LlmRuleGenerator OpenAiGenerator(StubHandler handler) =>
        new(
            new OpenAiChatClient(new HttpClient(handler) { BaseAddress = new Uri("https://openai.test/") }, Microsoft.Extensions.Options.Options.Create(LlmSettings)),
            new RuleDefinitionValidator(Microsoft.Extensions.Options.Options.Create(new RuleEngineOptions())),
            Microsoft.Extensions.Options.Options.Create(LlmSettings),
            NullLogger<LlmRuleGenerator>.Instance);

    private static HttpResponseMessage OpenAiReply(string content, int promptTokens = 100, int completionTokens = 50)
    {
        var body = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
            usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens },
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task Valid_json_becomes_a_validated_proposal_with_metadata()
    {
        var handler = new StubHandler(_ => OpenAiReply(ValidRule));

        var result = await OpenAiGenerator(handler).GenerateAsync(Request(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Generator.Should().Be("llm:openai");
        result.Model.Should().Be("gpt-4o-mini");
        result.PromptVersion.Should().Be(PromptLibrary.RuleGenerationVersion);
        result.Attempts.Should().Be(1);
        result.InputTokens.Should().Be(100);
        result.OutputTokens.Should().Be(50);
        result.Proposal!.Condition.Field.Should().Be("document.quantity");
        result.Proposal.Confidence.Should().Be(0.92);
        result.Proposal.Assumptions.Should().Contain(a => a.Contains("language model"));

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/v1/chat/completions");
        handler.Requests[0].Authorization.Should().BeNull("the key is set on the named client by DI, not per request");
        handler.Requests[0].Body.Should().Contain("json_object").And.Contain("document.quantity").And.Contain("Fact schema");
    }

    [Fact]
    public async Task Validation_errors_are_fed_back_for_one_retry()
    {
        var replies = new Queue<HttpResponseMessage>(new[] { OpenAiReply(HallucinatedRule), OpenAiReply(ValidRule) });
        var handler = new StubHandler(_ => replies.Dequeue());

        var result = await OpenAiGenerator(handler).GenerateAsync(Request(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Attempts.Should().Be(2);
        result.InputTokens.Should().Be(200);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Body.Should().Contain("document.colour").And.Contain("rejected by the validator");
    }

    [Fact]
    public async Task Garbage_output_fails_clearly_after_bounded_retries()
    {
        var handler = new StubHandler(_ => OpenAiReply("Sorry, I cannot help with that."));

        var result = await OpenAiGenerator(handler).GenerateAsync(Request(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Attempts.Should().Be(2);
        result.Errors.Should().Contain(e => e.Contains("did not contain a JSON object"));
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Model_refusal_stops_without_retry()
    {
        var handler = new StubHandler(_ => OpenAiReply("""{ "error": "There is no field for colour." }"""));

        var result = await OpenAiGenerator(handler).GenerateAsync(Request(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Attempts.Should().Be(1);
        result.Errors.Single().Should().Contain("no field for colour");
    }

    [Fact]
    public async Task Provider_errors_fail_without_leaking_details()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("upstream secret stack trace") });

        var result = await OpenAiGenerator(handler).GenerateAsync(Request(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Errors.Single().Should().Be("OpenAI returned HTTP 500.");
    }

    [Fact]
    public async Task Anthropic_text_blocks_with_markdown_fences_are_parsed()
    {
        var body = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "```json\n" + ValidRule + "\n```" } },
            stop_reason = "end_turn",
            usage = new { input_tokens = 80, output_tokens = 40 },
        });
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var options = new LlmOptions { Provider = LlmOptions.Anthropic, AnthropicApiKey = "k" };
        var generator = new LlmRuleGenerator(
            new AnthropicMessagesClient(new HttpClient(handler) { BaseAddress = new Uri("https://anthropic.test/") }, Microsoft.Extensions.Options.Options.Create(options)),
            new RuleDefinitionValidator(Microsoft.Extensions.Options.Options.Create(new RuleEngineOptions())),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<LlmRuleGenerator>.Instance);

        var result = await generator.GenerateAsync(Request(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Generator.Should().Be("llm:anthropic");
        result.Model.Should().Be("claude-sonnet-4-5");
        result.Proposal!.Actions.Single().ActionType.Should().Be(RuleActionType.RequireApproval);
        handler.Requests.Single().Path.Should().Be("/v1/messages");
        handler.Requests.Single().Body.Should().Contain("\"system\"").And.Contain("\"max_tokens\"");
    }

    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("Here you go: {\"a\":{\"b\":2}} thanks", "{\"a\":{\"b\":2}}")]
    [InlineData("no json", null)]
    public void Json_object_is_extracted_from_wrapped_output(string content, string? expected)
    {
        LlmRuleGenerator.ExtractJsonObject(content).Should().Be(expected);
    }

    [Fact]
    public void Prompt_lists_schema_operators_and_actions()
    {
        var prompt = PromptLibrary.RenderRuleGeneration(PromptLibrary.RuleGeneration, TestData.PurchaseSchema(), "purchase-approval");

        prompt.Should().Contain("purchase-approval")
            .And.Contain("- document.quantity (Number, required) aliases: qty, units, number of items")
            .And.Contain("- Between: Number, Date")
            .And.Contain("TriggerWorkflow")
            .And.NotContain("{{");
    }

    internal sealed record CapturedRequest(string Path, string? Authorization, string Body);

    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return _respond(request);
        }
    }
}
