using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RuleEngine.Application.Audit;
using RuleEngine.Application.Auth;
using RuleEngine.Application.Common;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Application.Execution;
using RuleEngine.Application.Generation;
using RuleEngine.Application.Rules;
using RuleEngine.Application.RuleSets;
using RuleEngine.Application.Testing;
using RuleEngine.Application.Validation;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Evaluation;

namespace RuleEngine.Application;

public static class DependencyInjection
{
    /// <summary>Registers use cases, the evaluator, validators and built-in action executors.</summary>
    public static IServiceCollection AddRuleEngineApplication(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(sp => new RuleEvaluator(sp.GetRequiredService<IOptions<RuleEngineOptions>>().Value.ToEvaluationOptions()));
        services.AddSingleton<RuleDefinitionValidator>();
        services.AddSingleton<ConflictDetector>();
        services.AddSingleton<RuleTestRunner>();
        services.AddSingleton<PatternRuleGenerator>();
        services.AddSingleton<IRetryDelay, TaskRetryDelay>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<RuleValidator>();
        services.AddScoped<EvaluableRuleLoader>();
        services.AddScoped<IActionDispatcher, ActionDispatcher>();
        services.AddScoped<IActionExecutor, SetValueActionExecutor>();
        services.AddScoped<IActionExecutor, RequireApprovalActionExecutor>();
        services.AddScoped<IActionExecutor, AddTagActionExecutor>();
        services.AddScoped<IActionExecutor, RejectActionExecutor>();
        services.AddScoped<IRuleGenerator, CompositeRuleGenerator>();

        services.AddScoped<RuleSetService>();
        services.AddScoped<RuleService>();
        services.AddScoped<RuleTestService>();
        services.AddScoped<RuleExecutionService>();
        services.AddScoped<ExecutionQueryService>();
        services.AddScoped<RuleGenerationService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<AuthService>();
        services.AddScoped<DemoUserSeeder>();

        services.AddValidatorsFromAssemblyContaining<CreateRuleSetRequestValidator>();
        return services;
    }
}
