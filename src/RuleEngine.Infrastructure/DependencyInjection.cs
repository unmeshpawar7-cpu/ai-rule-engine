using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Application.Execution;
using RuleEngine.Application.Generation;
using RuleEngine.Infrastructure.Actions;
using RuleEngine.Infrastructure.Ai;
using RuleEngine.Infrastructure.Persistence;

namespace RuleEngine.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "RuleEngine";

    /// <summary>EF Core (SQL Server), webhook action executors and the optional LLM fallback generator.</summary>
    public static IServiceCollection AddRuleEngineInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");
        services.AddDbContext<RuleEngineDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3)));
        services.AddScoped<IRuleEngineDbContext>(sp => sp.GetRequiredService<RuleEngineDbContext>());

        services.Configure<WebhookOptions>(configuration.GetSection(WebhookOptions.SectionName));
        services.AddHttpClient<WebhookClient>((sp, client) =>
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, sp.GetRequiredService<IOptions<WebhookOptions>>().Value.TimeoutSeconds)));
        services.AddScoped<IActionExecutor, NotifyActionExecutor>();
        services.AddScoped<IActionExecutor, TriggerWorkflowActionExecutor>();

        AddLlm(services, LlmOptions.FromConfiguration(configuration));
        return services;
    }

    private static void AddLlm(IServiceCollection services, LlmOptions llm)
    {
        services.AddSingleton(Options.Create(llm));
        switch (llm.Provider)
        {
            case LlmOptions.OpenAi:
                if (string.IsNullOrWhiteSpace(llm.OpenAiApiKey))
                {
                    throw new InvalidOperationException("OPENAI_API_KEY must be set when LLM_PROVIDER=openai.");
                }

                services.AddHttpClient<ILlmClient, OpenAiChatClient>(client =>
                {
                    client.BaseAddress = new Uri(llm.OpenAiBaseUrl);
                    client.Timeout = TimeSpan.FromSeconds(llm.TimeoutSeconds);
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", llm.OpenAiApiKey);
                });
                services.AddScoped<IFallbackRuleGenerator, LlmRuleGenerator>();
                break;

            case LlmOptions.Anthropic:
                if (string.IsNullOrWhiteSpace(llm.AnthropicApiKey))
                {
                    throw new InvalidOperationException("ANTHROPIC_API_KEY must be set when LLM_PROVIDER=anthropic.");
                }

                services.AddHttpClient<ILlmClient, AnthropicMessagesClient>(client =>
                {
                    client.BaseAddress = new Uri(llm.AnthropicBaseUrl);
                    client.Timeout = TimeSpan.FromSeconds(llm.TimeoutSeconds);
                    client.DefaultRequestHeaders.Add("x-api-key", llm.AnthropicApiKey);
                    client.DefaultRequestHeaders.Add("anthropic-version", AnthropicMessagesClient.ApiVersion);
                });
                services.AddScoped<IFallbackRuleGenerator, LlmRuleGenerator>();
                break;

            case LlmOptions.Offline:
                // Deterministic pattern grammar only; no fallback generator is registered.
                break;

            default:
                throw new InvalidOperationException($"Unknown LLM_PROVIDER '{llm.Provider}'. Use offline, openai or anthropic.");
        }
    }
}
