using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Development;
using atos.AIFarm.Infrastructure.Persistence;
using atos.AIFarm.Infrastructure.Providers.OpenAI;
using atos.AIFarm.Web;
using atos.AIFarm.Web.Components;
using atos.AIFarm.Web.Components.Account;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
{
    LoadLocalNeonEnvironment(builder.Configuration, builder.Environment.ContentRootPath);
}
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider,
    IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.AddAuthorization();
if (builder.Environment.IsDevelopment())
{
    var developmentKeys = Path.Combine(builder.Environment.ContentRootPath, ".aifarm-keys");
    builder.Services.AddDataProtection()
        .SetApplicationName("atos.AIFarm.Development")
        .PersistKeysToFileSystem(new DirectoryInfo(developmentKeys));
}
var executionLimits = new ChatExecutionLimits(
    builder.Configuration.GetValue(
        "AIFarm:Execution:MaximumPromptCharacters",
        ChatExecutionLimits.Default.MaximumPromptCharacters),
    builder.Configuration.GetValue(
        "AIFarm:Execution:MaximumCostPerRequestMinor",
        ChatExecutionLimits.Default.MaximumCostPerRequestMinor));
var contextDefaults = ChatContextLimits.Default;
var contextLimits = new ChatContextLimits(
    builder.Configuration.GetValue(
        "AIFarm:Context:DefaultMessageCount",
        contextDefaults.DefaultMessageCount),
    builder.Configuration.GetValue(
        "AIFarm:Context:MaximumMessageCount",
        contextDefaults.MaximumMessageCount),
    builder.Configuration.GetValue(
        "AIFarm:Context:MaximumInputTokens",
        contextDefaults.MaximumInputTokens),
    builder.Configuration.GetValue(
        "AIFarm:Context:SummaryTokenBudget",
        contextDefaults.SummaryTokenBudget)).Validate();
var providerDefaults = AiProviderProtectionOptions.Default;
var providerProtection = new AiProviderProtectionOptions(
    TimeSpan.FromSeconds(builder.Configuration.GetValue(
        "AIFarm:Execution:ProviderTimeoutSeconds",
        providerDefaults.Timeout.TotalSeconds)),
    builder.Configuration.GetValue(
        "AIFarm:Execution:CircuitBreakerFailureThreshold",
        providerDefaults.CircuitBreakerFailureThreshold),
    TimeSpan.FromSeconds(builder.Configuration.GetValue(
        "AIFarm:Execution:CircuitBreakerOpenSeconds",
        providerDefaults.CircuitBreakerOpenDuration.TotalSeconds)));
var configuredProvider = builder.Configuration["AIFarm:AI:Provider"] ?? "Fake";
OpenAiProviderOptions? openAi = null;
if (configuredProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
{
    var apiKey = builder.Configuration["OPENAI_API_KEY"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new InvalidOperationException(
            "OPENAI_API_KEY is required when AIFarm:AI:Provider is OpenAI.");
    }

    openAi = new OpenAiProviderOptions(
        apiKey,
        builder.Configuration["AIFarm:AI:OpenAI:Model"] ??
            "gpt-5.4-mini-2026-03-17",
        builder.Configuration["AIFarm:AI:OpenAI:PriceCatalogVersion"] ??
            "openai-gpt-5.4-mini-2026-03-17-aic-v1",
        builder.Configuration.GetValue<long>(
            "AIFarm:AI:OpenAI:InputCostPerMillionMinor",
            750_000),
        builder.Configuration.GetValue<long>(
            "AIFarm:AI:OpenAI:OutputCostPerMillionMinor",
            4_500_000),
        builder.Configuration.GetValue<int>(
            "AIFarm:AI:OpenAI:MaximumOutputTokens",
            512),
        OpenAiProviderOptions.DefaultEndpoint);
}
else if (!configuredProvider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        $"AIFarm:AI:Provider '{configuredProvider}' is not supported.");
}
builder.Services.AddAIFarmDevelopment(
    executionLimits,
    providerProtection,
    openAi,
    contextLimits);
builder.Services.RemoveAll<IAIFarmIdentityAccessor>();
builder.Services.AddScoped<IAIFarmIdentityAccessor,
    AuthenticatedAIFarmIdentityAccessor>();
builder.Services.AddHealthChecks()
    .AddCheck<AIFarmReadinessHealthCheck>("aifarm_dependencies", tags: ["ready"]);

var postgresConnection =
    builder.Configuration["DATABASE_URL"] ??
    builder.Configuration.GetConnectionString("AIFarm");
var postgresMigrationConnection =
    builder.Configuration["DATABASE_URL_UNPOOLED"] ??
    builder.Configuration.GetConnectionString("AIFarmMigrations");
if (string.IsNullOrWhiteSpace(postgresConnection))
{
    throw new InvalidOperationException(
        "DATABASE_URL is required because authenticated accounts are persisted in PostgreSQL.");
}
builder.Services.AddAIFarmPostgres(postgresConnection);
builder.Services.AddIdentityCore<AIFarmUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddEntityFrameworkStores<AIFarmDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

var app = builder.Build();

var runMigrations = app.Configuration.GetValue(
    "AIFarm:RunMigrationsOnStartup",
    app.Environment.IsDevelopment());
if (runMigrations && string.IsNullOrWhiteSpace(postgresMigrationConnection))
{
    throw new InvalidOperationException(
        "DATABASE_URL_UNPOOLED is required when startup migrations are enabled.");
}
if (runMigrations)
{
    await app.Services.MigrateAIFarmAsync(postgresMigrationConnection!);
}

// Configure the HTTP request pipeline.
app.UseMiddleware<RequestCorrelationMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = (context, report) => WriteHealthResponse(context, report, "live"),
});
var readinessOptions = new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = (context, report) => WriteHealthResponse(context, report, "ready"),
};
app.MapHealthChecks("/health/ready", readinessOptions);
app.MapHealthChecks("/health", readinessOptions);
app.MapPost("/account/logout", async (
    SignInManager<AIFarmUser> signInManager,
    [FromForm] string? returnUrl) =>
{
    await signInManager.SignOutAsync();
    return Results.LocalRedirect(
        string.IsNullOrWhiteSpace(returnUrl) ? "~/" : $"~/{returnUrl.TrimStart('/')}");
}).RequireAuthorization();

app.Run();

static Task WriteHealthResponse(HttpContext context, HealthReport report, string check)
{
    context.Response.ContentType = "application/json; charset=utf-8";
    var checks = report.Entries.Select(entry => new
    {
        name = entry.Key,
        status = entry.Value.Status.ToString().ToLowerInvariant(),
    });
    return context.Response.WriteAsJsonAsync(new
    {
        status = report.Status == HealthStatus.Healthy ? "ok" : "unavailable",
        check,
        timestampUtc = context.RequestServices
            .GetRequiredService<TimeProvider>()
            .GetUtcNow(),
        correlationId = context.TraceIdentifier,
        checks,
    });
}

static void LoadLocalNeonEnvironment(ConfigurationManager configuration, string contentRootPath)
{
    if (!configuration.GetValue("AIFarm:LoadLocalEnvironment", true))
    {
        return;
    }

    var directory = new DirectoryInfo(contentRootPath);
    while (directory is not null)
    {
        var path = Path.Combine(directory.FullName, ".env.local");
        if (File.Exists(path))
        {
            var values = File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(
                    parts => parts[0].Trim(),
                    parts => parts[1].Trim().Trim('"', '\''),
                    StringComparer.OrdinalIgnoreCase);
            configuration.AddInMemoryCollection(values!);
            return;
        }

        directory = directory.Parent;
    }
}
