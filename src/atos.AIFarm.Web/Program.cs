using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure;
using atos.AIFarm.Web;
using atos.AIFarm.Web.Components;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.DataProtection;
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
if (builder.Environment.IsDevelopment())
{
    var developmentKeys = Path.Combine(builder.Environment.ContentRootPath, ".aifarm-keys");
    builder.Services.AddDataProtection()
        .SetApplicationName("atos.AIFarm.Development")
        .PersistKeysToFileSystem(new DirectoryInfo(developmentKeys));
}
builder.Services.AddAIFarmDevelopment();
builder.Services.AddHealthChecks()
    .AddCheck<AIFarmReadinessHealthCheck>("aifarm_dependencies", tags: ["ready"]);

var postgresConnection =
    builder.Configuration["DATABASE_URL"] ??
    builder.Configuration.GetConnectionString("AIFarm");
var postgresMigrationConnection =
    builder.Configuration["DATABASE_URL_UNPOOLED"] ??
    builder.Configuration.GetConnectionString("AIFarmMigrations");
if (builder.Environment.IsDevelopment() is false && string.IsNullOrWhiteSpace(postgresConnection))
{
    throw new InvalidOperationException("DATABASE_URL is required outside Development.");
}
if (!string.IsNullOrWhiteSpace(postgresConnection))
{
    builder.Services.AddAIFarmPostgres(postgresConnection);
}

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

await app.Services.InitializeAIFarmAsync();

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
