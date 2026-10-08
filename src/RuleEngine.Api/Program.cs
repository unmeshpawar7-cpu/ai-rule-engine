using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using RuleEngine.Api.Common;
using RuleEngine.Api.Security;
using RuleEngine.Application;
using RuleEngine.Application.Auth;
using RuleEngine.Application.Common;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Infrastructure;
using RuleEngine.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// ---- options (validated at startup)
builder.Services.AddOptions<RuleEngineOptions>()
    .Bind(builder.Configuration.GetSection(RuleEngineOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<RateLimitingOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DemoUsersOptions>()
    .Bind(builder.Configuration.GetSection(DemoUsersOptions.SectionName));

// ---- application + infrastructure
builder.Services.AddRuleEngineApplication();
builder.Services.AddRuleEngineInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();

// ---- MVC, validation, error envelope
builder.Services
    .AddControllers(options => options.Filters.Add<ValidationFilter>())
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState
            .Where(entry => entry.Value is not null && entry.Value.Errors.Count > 0)
            .SelectMany(entry => entry.Value!.Errors.Select(error => new ErrorItem(
                GlobalExceptionHandler.ToCamelPath(entry.Key.TrimStart('$', '.')),
                string.IsNullOrEmpty(error.ErrorMessage) ? "The value is invalid." : error.ErrorMessage,
                "INVALID_REQUEST")))
            .ToList();
        return new BadRequestObjectResult(ApiErrorResponse.Create("Validation failed", errors, CorrelationIdMiddleware.Get(context.HttpContext)));
    });
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// ---- authentication / authorization (JWT bearer, role-based)
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwt.CreateKey(),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtOptions.NameClaim,
            RoleClaimType = JwtOptions.RoleClaim,
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(ApiErrorResponse.Create(
                    "Authentication is required. Obtain a token from POST /api/auth/token.",
                    null,
                    CorrelationIdMiddleware.Get(context.HttpContext)));
            },
            OnForbidden = async context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(ApiErrorResponse.Create(
                    "Your role is not allowed to perform this operation.",
                    null,
                    CorrelationIdMiddleware.Get(context.HttpContext)));
            },
        };
    });
builder.Services.AddAuthorization();

// ---- rate limiting (per user, AI generation only)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Generation, httpContext =>
    {
        var permits = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value.GenerationPermitsPerMinute;
        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.Identity?.Name ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(
            ApiErrorResponse.Create("Too many rule generation requests; try again in a minute.", null, CorrelationIdMiddleware.Get(context.HttpContext)),
            cancellationToken);
    };
});

// ---- health
builder.Services.AddHealthChecks()
    .AddDbContextCheck<RuleEngineDbContext>("database", tags: new[] { "ready" });

// ---- OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AI Rule Engine API",
        Version = "v1",
        Description = "Versioned business rules with a deterministic evaluator, explainable traces, simulation, " +
                      "rule tests and AI-assisted (never auto-published) rule authoring.",
    });
    var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }

    options.MapType<System.Text.Json.JsonElement>(() => new OpenApiSchema { Description = "Any JSON value." });
    options.MapType<JsonNode>(() => new OpenApiSchema { Description = "Any JSON value." });
    options.MapType<JsonObject>(() => new OpenApiSchema { Type = "object", AdditionalPropertiesAllowed = true });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT from POST /api/auth/token.",
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        },
    });
});

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

await InitializeDatabaseAsync(app);

app.Run();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RuleEngineDbContext>();
    if (app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
    {
        await db.Database.MigrateAsync();
    }
    else if (app.Configuration.GetValue<bool>("Database:EnsureCreatedOnStartup"))
    {
        await db.Database.EnsureCreatedAsync();
    }

    // Demo users exist only for local development and automated tests, never in other environments.
    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
    {
        var options = scope.ServiceProvider.GetRequiredService<IOptions<DemoUsersOptions>>().Value;
        await scope.ServiceProvider.GetRequiredService<DemoUserSeeder>().SeedAsync(options, CancellationToken.None);
    }
}

/// <summary>Entry point; partial so integration tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program
{
}
