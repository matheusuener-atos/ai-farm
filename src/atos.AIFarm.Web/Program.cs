using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Infrastructure.Development;
using atos.AIFarm.Infrastructure.Attachments;
using atos.AIFarm.Infrastructure.Persistence;
using atos.AIFarm.Infrastructure.Providers.OpenAI;
using atos.AIFarm.Web;
using atos.AIFarm.Web.Components;
using atos.AIFarm.Web.Components.Account;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

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
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
        AIFarmApiTokenDefaults.Scheme, _ => { });
builder.Services.AddAuthorization(options =>
{
    AddApiPolicy(options, AIFarmApiTokenDefaults.ProfileReadPolicy, ApiTokenScope.ProfileRead);
    AddApiPolicy(options, AIFarmApiTokenDefaults.UsageReadPolicy, ApiTokenScope.UsageRead);
    AddApiPolicy(options, AIFarmApiTokenDefaults.IncidentsReadPolicy, ApiTokenScope.IncidentsRead);
    AddApiPolicy(options, AIFarmApiTokenDefaults.AuditReadPolicy, ApiTokenScope.AuditRead);
    AddApiPolicy(options, AIFarmApiTokenDefaults.ConversationsReadPolicy, ApiTokenScope.ConversationsRead);
    AddApiPolicy(options, AIFarmApiTokenDefaults.IrisExecutePolicy, ApiTokenScope.IrisExecute);
    AddApiPolicy(options, AIFarmApiTokenDefaults.FarmReadPolicy, ApiTokenScope.FarmRead);
    AddApiPolicy(options, AIFarmApiTokenDefaults.FarmExecutePolicy, ApiTokenScope.FarmExecute);
});
builder.Services.AddHttpContextAccessor();
if (builder.Configuration.GetValue<bool>("RENDER"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("farm-agent", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
    options.AddPolicy("personal-api", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(AIFarmApiTokenDefaults.TokenIdClaim)?.Value ??
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
    options.AddPolicy("iris-execute", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(AIFarmApiTokenDefaults.TokenIdClaim)?.Value ??
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
    options.AddPolicy("farm-execute", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(AIFarmApiTokenDefaults.TokenIdClaim)?.Value ??
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
});
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
var objectStorageValues = new[]
{
    builder.Configuration["AWS_ACCESS_KEY_ID"],
    builder.Configuration["AWS_SECRET_ACCESS_KEY"],
    builder.Configuration["AWS_ENDPOINT_URL_S3"],
    builder.Configuration["AWS_REGION"]
};
NeonObjectStorageOptions? objectStorage = null;
if (objectStorageValues.All(x => !string.IsNullOrWhiteSpace(x)))
{
    objectStorage = new NeonObjectStorageOptions(
        objectStorageValues[0]!, objectStorageValues[1]!,
        objectStorageValues[2]!, objectStorageValues[3]!);
}
else if (objectStorageValues.Any(x => !string.IsNullOrWhiteSpace(x)))
{
    throw new InvalidOperationException(
        "Neon Object Storage requires AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_ENDPOINT_URL_S3 and AWS_REGION together.");
}
var manifestSigningKey =
    builder.Configuration["AIFarm:Farm:ManifestSigningKey"];
FarmManifestSigningOptions farmManifestSigning;
if (string.IsNullOrWhiteSpace(manifestSigningKey))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException(
            "AIFarm:Farm:ManifestSigningKey is required outside Development.");
    farmManifestSigning = FarmManifestSigningOptions.Development;
}
else
{
    farmManifestSigning = new FarmManifestSigningOptions(manifestSigningKey);
}
builder.Services.AddAIFarmPostgres(
    postgresConnection,
    objectStorage: objectStorage,
    farmManifestSigning: farmManifestSigning);
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
if (app.Configuration.GetValue<bool>("RENDER"))
{
    app.UseForwardedHeaders();
}
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Task.CompletedTask;
    });
    await next(context);
});
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

// Keep a physical-file fallback for framework assets. MapStaticAssets remains
// enabled below for fingerprinting and compression, while this prevents a
// published Blazor runtime file from becoming an application-level 404.
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/api/chat/attachments/{attachmentId:guid}", async (
    Guid attachmentId,
    bool? preview,
    HttpContext httpContext,
    IChatAttachmentService attachments,
    CancellationToken cancellationToken) =>
{
    var download = await attachments.DownloadAsync(attachmentId, cancellationToken);
    if (download is null)
    {
        return Results.NotFound();
    }

    httpContext.Response.Headers.CacheControl = "private, no-store";
    httpContext.Response.Headers.XContentTypeOptions = "nosniff";
    var showInline = preview is true &&
        download.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    return Results.File(
        download.Content,
        download.ContentType,
        showInline ? null : download.FileName);
}).RequireAuthorization();

app.MapGet("/api/chat/conversations/{conversationId:guid}/export/{format}", async (
    Guid conversationId,
    string format,
    IConversationExportService exports,
    CancellationToken cancellationToken) =>
{
    if (!Enum.TryParse<ConversationExportFormat>(format, true, out var exportFormat))
        return Results.BadRequest("Formato de exportação inválido.");
    var export = await exports.ExportAsync(conversationId, exportFormat, cancellationToken);
    return export is null
        ? Results.NotFound()
        : Results.File(export.Content, export.ContentType, export.FileName);
}).RequireAuthorization();

app.MapGet("/api/farm/jobs/{jobId:guid}/evidence", async (
    Guid jobId,
    HttpResponse response,
    IFarmLabService farmLab,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "private, no-store";
    response.Headers.XContentTypeOptions = "nosniff";
    try
    {
        var evidence = await farmLab.DownloadEvidenceAsync(jobId, cancellationToken);
        return evidence is null
            ? Results.NotFound()
            : Results.File(evidence.Content, "application/json; charset=utf-8", evidence.FileName);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).RequireAuthorization();

app.MapGet("/api/farm/jobs/{jobId:guid}/output", async (
    Guid jobId,
    HttpResponse response,
    IFarmUsefulJobService usefulJobs,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "private, no-store";
    response.Headers.XContentTypeOptions = "nosniff";
    try
    {
        var output = await usefulJobs.DownloadOutputAsync(jobId, cancellationToken);
        return output is null
            ? Results.NotFound()
            : Results.File(output.Content, output.ContentType, output.FileName);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).RequireAuthorization();

app.MapGet("/api/farm/jobs/export", async (
    string? search,
    string? workloadId,
    string? status,
    string? origin,
    bool? integrityFailuresOnly,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    HttpResponse response,
    IFarmJobHistoryService history,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "private, no-store";
    response.Headers.XContentTypeOptions = "nosniff";
    try
    {
        var export = await history.ExportAsync(BuildFarmJobHistoryQuery(
            search, workloadId, status, origin, integrityFailuresOnly,
            fromUtc, toUtc, 1, 100), cancellationToken);
        return Results.File(export.Content, export.ContentType, export.FileName);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization();

app.MapGet("/api/farm/economics/{scenarioId:guid}/report", async (
    Guid scenarioId,
    HttpResponse response,
    IFarmEconomicsScenarioStore scenarios,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "private, no-store";
    response.Headers.XContentTypeOptions = "nosniff";
    try
    {
        var report = await scenarios.DownloadReportAsync(scenarioId, cancellationToken);
        return report is null
            ? Results.NotFound()
            : Results.File(report.Content, "application/json; charset=utf-8", report.FileName);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).RequireAuthorization();

app.MapGet("/api/audit/export", async (
    string? search,
    string? category,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    HttpResponse response,
    IAuditTrail audit,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "private, no-store";
    response.Headers.XContentTypeOptions = "nosniff";
    if (!string.IsNullOrWhiteSpace(category) &&
        !Enum.TryParse<AuditCategory>(category, true, out _))
        return Results.BadRequest(new { error = "Categoria de auditoria inválida." });
    try
    {
        var parsedCategory = Enum.TryParse<AuditCategory>(category, true, out var value) ? value : (AuditCategory?)null;
        var export = await audit.ExportAsync(
            new AuditQuery(search, parsedCategory, fromUtc, toUtc), cancellationToken);
        return Results.File(export.Content, export.ContentType, export.FileName);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization();

var personalApi = app.MapGroup("/api/v1")
    .RequireRateLimiting("personal-api");
personalApi.MapGet("/me", async (
    IAIFarmIdentityAccessor identityAccessor,
    CancellationToken cancellationToken) =>
{
    var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
    return Results.Ok(new
    {
        identity.AccountId,
        identity.DisplayName,
        authenticatedWith = "iris_personal_token"
    });
}).RequireAuthorization(AIFarmApiTokenDefaults.ProfileReadPolicy);

personalApi.MapGet("/usage", async (
    IAiUsageQuery usage,
    IAiUsageBudgetService budget,
    CancellationToken cancellationToken) => Results.Ok(new
    {
        usage = await usage.GetDashboardAsync(20, cancellationToken),
        budget = await budget.GetSnapshotAsync(cancellationToken)
    })).RequireAuthorization(AIFarmApiTokenDefaults.UsageReadPolicy);

personalApi.MapGet("/usage/analytics", async (
    int? days,
    IAiUsageQuery usage,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await usage.GetAnalyticsAsync(days ?? 30, cancellationToken));
    }
    catch (ArgumentOutOfRangeException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AIFarmApiTokenDefaults.UsageReadPolicy);

personalApi.MapGet("/farm/analytics", async (
    int? days,
    IFarmAnalyticsQuery analytics,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await analytics.GetAsync(days ?? 30, cancellationToken));
    }
    catch (ArgumentOutOfRangeException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AIFarmApiTokenDefaults.FarmReadPolicy);

personalApi.MapGet("/farm/workloads", () => Results.Ok(new
{
    catalogVersion = FarmWorkloadCatalog.Version,
    workloads = FarmWorkloadCatalog.Definitions
})).RequireAuthorization(AIFarmApiTokenDefaults.FarmReadPolicy);

personalApi.MapGet("/farm/nodes", async (
    IFarmLabService farm,
    CancellationToken cancellationToken) =>
    Results.Ok((await farm.GetDashboardAsync(cancellationToken)).Nodes))
    .RequireAuthorization(AIFarmApiTokenDefaults.FarmReadPolicy);

personalApi.MapPost("/farm/nodes/{nodeId:guid}/pause", async (
    Guid nodeId,
    IFarmLabService farm,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await farm.SetNodeStatusAsync(nodeId, FarmNodeStatus.Paused, cancellationToken)); }
    catch (InvalidOperationException exception) { return Results.NotFound(new { error = exception.Message }); }
}).DisableAntiforgery()
  .RequireAuthorization(AIFarmApiTokenDefaults.FarmExecutePolicy)
  .RequireRateLimiting("farm-execute");

personalApi.MapPost("/farm/nodes/{nodeId:guid}/resume", async (
    Guid nodeId,
    IFarmLabService farm,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await farm.SetNodeStatusAsync(nodeId, FarmNodeStatus.Online, cancellationToken)); }
    catch (InvalidOperationException exception) { return Results.NotFound(new { error = exception.Message }); }
}).DisableAntiforgery()
  .RequireAuthorization(AIFarmApiTokenDefaults.FarmExecutePolicy)
  .RequireRateLimiting("farm-execute");

personalApi.MapGet("/farm/schedules", async (
    IFarmScheduleService schedules,
    CancellationToken cancellationToken) =>
    Results.Ok(await schedules.GetDashboardAsync(cancellationToken)))
    .RequireAuthorization(AIFarmApiTokenDefaults.FarmReadPolicy);

personalApi.MapPost("/farm/schedules", async (
    FarmScheduleRequest request,
    IFarmScheduleService schedules,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await schedules.CreateAsync(request, cancellationToken)); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
}).DisableAntiforgery()
  .RequireAuthorization(AIFarmApiTokenDefaults.FarmExecutePolicy)
  .RequireRateLimiting("farm-execute")
  .WithMetadata(new RequestSizeLimitAttribute(4 * 1024));

personalApi.MapPost("/farm/schedules/{scheduleId:guid}/pause", async (
    Guid scheduleId,
    IFarmScheduleService schedules,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await schedules.SetStatusAsync(
        scheduleId, FarmScheduleStatus.Paused, cancellationToken)); }
    catch (InvalidOperationException exception) { return Results.NotFound(new { error = exception.Message }); }
}).DisableAntiforgery()
  .RequireAuthorization(AIFarmApiTokenDefaults.FarmExecutePolicy)
  .RequireRateLimiting("farm-execute");

personalApi.MapPost("/farm/schedules/{scheduleId:guid}/resume", async (
    Guid scheduleId,
    IFarmScheduleService schedules,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await schedules.SetStatusAsync(
        scheduleId, FarmScheduleStatus.Active, cancellationToken)); }
    catch (InvalidOperationException exception) { return Results.NotFound(new { error = exception.Message }); }
}).DisableAntiforgery()
  .RequireAuthorization(AIFarmApiTokenDefaults.FarmExecutePolicy)
  .RequireRateLimiting("farm-execute");

personalApi.MapPost("/farm/schedules/{scheduleId:guid}/trigger", async (
    Guid scheduleId,
    IFarmScheduleService schedules,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await schedules.TriggerNowAsync(scheduleId, cancellationToken)); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
}).DisableAntiforgery()
  .RequireAuthorization(AIFarmApiTokenDefaults.FarmExecutePolicy)
  .RequireRateLimiting("farm-execute");

personalApi.MapGet("/farm/jobs", async (
    string? search,
    string? workloadId,
    string? status,
    string? origin,
    bool? integrityFailuresOnly,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int? page,
    int? pageSize,
    IFarmJobHistoryService history,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await history.SearchAsync(BuildFarmJobHistoryQuery(
            search, workloadId, status, origin, integrityFailuresOnly,
            fromUtc, toUtc, page ?? 1, pageSize ?? 20), cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AIFarmApiTokenDefaults.FarmReadPolicy);

personalApi.MapGet("/farm/jobs/export", async (
    string? search,
    string? workloadId,
    string? status,
    string? origin,
    bool? integrityFailuresOnly,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    HttpResponse response,
    IFarmJobHistoryService history,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "private, no-store";
    response.Headers.XContentTypeOptions = "nosniff";
    try
    {
        var export = await history.ExportAsync(BuildFarmJobHistoryQuery(
            search, workloadId, status, origin, integrityFailuresOnly,
            fromUtc, toUtc, 1, 100), cancellationToken);
        return Results.File(export.Content, export.ContentType, export.FileName);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AIFarmApiTokenDefaults.FarmReadPolicy);

personalApi.MapPost("/farm/jobs", async (
    FarmApiJobRequest submission,
    HttpRequest request,
    HttpResponse response,
    IFarmApiJobService jobs,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "private, no-store";
    try
    {
        if (!Guid.TryParse(
                request.HttpContext.User.FindFirst(AIFarmApiTokenDefaults.TokenIdClaim)?.Value,
                out var tokenId))
            return Results.Unauthorized();
        return Results.Ok(await jobs.SubmitAsync(
            submission,
            tokenId,
            request.Headers["X-Idempotency-Key"].ToString(),
            cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).DisableAntiforgery()
  .RequireAuthorization(AIFarmApiTokenDefaults.FarmExecutePolicy)
  .RequireRateLimiting("farm-execute")
  .WithMetadata(new RequestSizeLimitAttribute(4 * 1024));

personalApi.MapPost("/farm/useful-jobs", async (
    FarmUsefulJobRequest submission,
    HttpRequest request,
    HttpResponse response,
    IFarmUsefulJobService jobs,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "private, no-store";
    try
    {
        if (!Guid.TryParse(
                request.HttpContext.User.FindFirst(AIFarmApiTokenDefaults.TokenIdClaim)?.Value,
                out var tokenId))
            return Results.Unauthorized();
        return Results.Ok(await jobs.SubmitAsync(
            submission,
            tokenId,
            request.Headers["X-Idempotency-Key"].ToString(),
            cancellationToken));
    }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
}).DisableAntiforgery()
  .RequireAuthorization(AIFarmApiTokenDefaults.FarmExecutePolicy)
  .RequireRateLimiting("farm-execute")
  .WithMetadata(new RequestSizeLimitAttribute(48 * 1024));

personalApi.MapGet("/farm/useful-jobs/{jobId:guid}/output", async (
    Guid jobId,
    HttpResponse response,
    IFarmUsefulJobService jobs,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "private, no-store";
    response.Headers.XContentTypeOptions = "nosniff";
    try
    {
        var output = await jobs.DownloadOutputAsync(jobId, cancellationToken);
        return output is null ? Results.NotFound() :
            Results.File(output.Content, output.ContentType, output.FileName);
    }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
}).RequireAuthorization(AIFarmApiTokenDefaults.FarmReadPolicy);

personalApi.MapGet("/farm/jobs/{jobId:guid}", async (
    Guid jobId,
    IFarmApiJobService jobs,
    CancellationToken cancellationToken) =>
{
    var job = await jobs.GetAsync(jobId, cancellationToken);
    return job is null ? Results.NotFound() : Results.Ok(job);
}).RequireAuthorization(AIFarmApiTokenDefaults.FarmReadPolicy);

personalApi.MapPost("/farm/jobs/{jobId:guid}/cancel", async (
    Guid jobId,
    IFarmApiJobService jobs,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await jobs.CancelAsync(jobId, cancellationToken));
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).DisableAntiforgery()
  .RequireAuthorization(AIFarmApiTokenDefaults.FarmExecutePolicy)
  .RequireRateLimiting("farm-execute");

personalApi.MapGet("/farm/jobs/{jobId:guid}/evidence", async (
    Guid jobId,
    HttpResponse response,
    IFarmApiJobService jobs,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "private, no-store";
    response.Headers.XContentTypeOptions = "nosniff";
    try
    {
        var evidence = await jobs.DownloadEvidenceAsync(jobId, cancellationToken);
        return evidence is null
            ? Results.NotFound()
            : Results.File(evidence.Content, "application/json; charset=utf-8", evidence.FileName);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).RequireAuthorization(AIFarmApiTokenDefaults.FarmReadPolicy);

personalApi.MapGet("/incidents", async (
    string? search,
    string? severity,
    bool? openOnly,
    IOperationalIncidentService incidents,
    CancellationToken cancellationToken) =>
{
    if (!string.IsNullOrWhiteSpace(severity) &&
        !Enum.TryParse<IncidentSeverity>(severity, true, out _))
        return Results.BadRequest(new { error = "Severidade inválida." });
    var parsed = Enum.TryParse<IncidentSeverity>(severity, true, out var value)
        ? value : (IncidentSeverity?)null;
    return Results.Ok(await incidents.SearchAsync(
        new IncidentQuery(search, parsed, openOnly ?? true), cancellationToken));
}).RequireAuthorization(AIFarmApiTokenDefaults.IncidentsReadPolicy);

personalApi.MapGet("/audit", async (
    string? search,
    string? category,
    int? page,
    int? pageSize,
    IAuditTrail audit,
    CancellationToken cancellationToken) =>
{
    if (!string.IsNullOrWhiteSpace(category) &&
        !Enum.TryParse<AuditCategory>(category, true, out _))
        return Results.BadRequest(new { error = "Categoria inválida." });
    var parsed = Enum.TryParse<AuditCategory>(category, true, out var value)
        ? value : (AuditCategory?)null;
    try
    {
        return Results.Ok(await audit.SearchAsync(
            new AuditQuery(search, parsed, Page: page ?? 1, PageSize: pageSize ?? 25),
            cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AIFarmApiTokenDefaults.AuditReadPolicy);

personalApi.MapGet("/conversations", async (
    string? search,
    int? page,
    int? pageSize,
    bool? archived,
    IChatStore chat,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await chat.SearchConversationsAsync(
            new ConversationQuery(search, page ?? 1, pageSize ?? 25, archived ?? false),
            cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AIFarmApiTokenDefaults.ConversationsReadPolicy);

personalApi.MapGet("/conversations/{conversationId:guid}", async (
    Guid conversationId,
    IChatStore chat,
    CancellationToken cancellationToken) =>
{
    var conversation = await chat.GetConversationAsync(conversationId, cancellationToken);
    return conversation is null ? Results.NotFound() : Results.Ok(conversation);
}).RequireAuthorization(AIFarmApiTokenDefaults.ConversationsReadPolicy);

personalApi.MapPost("/iris/responses", async (
    IrisApiExecutionRequest execution,
    HttpRequest request,
    HttpResponse response,
    IrisApiService iris,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "private, no-store";
    try
    {
        if (!Guid.TryParse(
                request.HttpContext.User.FindFirst(AIFarmApiTokenDefaults.TokenIdClaim)?.Value,
                out var tokenId))
            return Results.Unauthorized();
        var result = await iris.ExecuteAsync(
            execution,
            tokenId,
            request.Headers["X-Idempotency-Key"].ToString(),
            cancellationToken);
        return Results.Ok(result);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (AiUsageBudgetExceededException exception)
    {
        return Results.Conflict(new { error = exception.Message, code = "usage_budget_exceeded" });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).DisableAntiforgery()
  .RequireAuthorization(AIFarmApiTokenDefaults.IrisExecutePolicy)
  .RequireRateLimiting("iris-execute")
  .WithMetadata(new RequestSizeLimitAttribute(32 * 1024));

app.MapPost("/api/farm/agents/enroll", async (
    HttpRequest request,
    HttpResponse response,
    FarmAgentEnrollmentRequest agent,
    IFarmAgentService farmAgents,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "no-store";
    var token = request.Headers["X-Enrollment-Token"].ToString();
    try
    {
        var enrollment = await farmAgents.EnrollAsync(token, agent, cancellationToken);
        return enrollment is null ? Results.Unauthorized() : Results.Ok(enrollment);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).DisableAntiforgery()
  .RequireRateLimiting("farm-agent")
  .WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

app.MapPost("/api/farm/agents/heartbeat", async (
    HttpRequest request,
    HttpResponse response,
    FarmAgentHeartbeatRequest heartbeat,
    IFarmAgentService farmAgents,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "no-store";
    var authorization = request.Headers.Authorization.ToString();
    var credential = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? authorization["Bearer ".Length..].Trim()
        : string.Empty;
    try
    {
        var result = await farmAgents.HeartbeatAsync(credential, heartbeat, cancellationToken);
        return result is null ? Results.Unauthorized() : Results.Ok(result);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).DisableAntiforgery()
  .RequireRateLimiting("farm-agent")
  .WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

app.MapGet("/api/farm/agents/trust", async (
    HttpRequest request,
    HttpResponse response,
    IFarmAgentService farmAgents,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "no-store";
    var trust = await farmAgents.GetTrustAsync(ReadBearer(request), cancellationToken);
    return trust is null ? Results.Unauthorized() : Results.Ok(trust);
}).RequireRateLimiting("farm-agent");

app.MapPost("/api/farm/agents/jobs/claim", async (
    HttpRequest request,
    HttpResponse response,
    IFarmAgentService farmAgents,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "no-store";
    var offer = await farmAgents.ClaimJobAsync(ReadBearer(request), cancellationToken);
    return offer is null ? Results.NoContent() : Results.Ok(offer);
}).DisableAntiforgery()
  .RequireRateLimiting("farm-agent")
  .WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

app.MapPost("/api/farm/agents/jobs/{jobId:guid}/started", async (
    Guid jobId,
    HttpRequest request,
    IFarmAgentService farmAgents,
    CancellationToken cancellationToken) =>
    await farmAgents.MarkJobStartedAsync(ReadBearer(request), jobId, cancellationToken)
        ? Results.NoContent()
        : Results.NotFound()).DisableAntiforgery()
  .RequireRateLimiting("farm-agent")
  .WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

app.MapPost("/api/farm/agents/jobs/{jobId:guid}/reject", async (
    Guid jobId,
    HttpRequest request,
    FarmRemoteJobRejection rejection,
    IFarmAgentService farmAgents,
    CancellationToken cancellationToken) =>
{
    try
    {
        return await farmAgents.RejectJobAsync(
            ReadBearer(request), jobId, rejection, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).DisableAntiforgery()
  .RequireRateLimiting("farm-agent")
  .WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

app.MapPost("/api/farm/agents/jobs/{jobId:guid}/result", async (
    Guid jobId,
    HttpRequest request,
    HttpResponse response,
    FarmRemoteJobResult result,
    IFarmAgentService farmAgents,
    CancellationToken cancellationToken) =>
{
    response.Headers.CacheControl = "no-store";
    try
    {
        var acknowledgement = await farmAgents.SubmitResultAsync(
            ReadBearer(request),
            jobId,
            result,
            cancellationToken);
        return acknowledgement is null ? Results.NotFound() : Results.Ok(acknowledgement);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
}).DisableAntiforgery()
  .RequireRateLimiting("farm-agent")
  .WithMetadata(new RequestSizeLimitAttribute(160 * 1024));

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

static string ReadBearer(HttpRequest request)
{
    var authorization = request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? authorization["Bearer ".Length..].Trim()
        : string.Empty;
}

static void AddApiPolicy(
    Microsoft.AspNetCore.Authorization.AuthorizationOptions options,
    string name,
    ApiTokenScope scope)
{
    options.AddPolicy(name, policy =>
    {
        policy.AddAuthenticationSchemes(AIFarmApiTokenDefaults.Scheme);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(AIFarmApiTokenDefaults.ScopeClaim, scope.ToString());
    });
}

static FarmJobHistoryQuery BuildFarmJobHistoryQuery(
    string? search,
    string? workloadId,
    string? status,
    string? origin,
    bool? integrityFailuresOnly,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int page,
    int pageSize)
{
    FarmJobStatus? parsedStatus = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse<FarmJobStatus>(status, true, out var value) ||
            !Enum.IsDefined(value))
            throw new ArgumentException("Status de Job inválido.", nameof(status));
        parsedStatus = value;
    }

    FarmJobOrigin? parsedOrigin = null;
    if (!string.IsNullOrWhiteSpace(origin))
    {
        if (!Enum.TryParse<FarmJobOrigin>(origin, true, out var value) ||
            !Enum.IsDefined(value))
            throw new ArgumentException("Origem de Job inválida.", nameof(origin));
        parsedOrigin = value;
    }

    return new FarmJobHistoryQuery(
        search,
        workloadId,
        parsedStatus,
        parsedOrigin,
        integrityFailuresOnly ?? false,
        fromUtc,
        toUtc,
        page,
        pageSize).Normalize();
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
