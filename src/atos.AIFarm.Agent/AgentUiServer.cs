using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace atos.AIFarm.Agent;

internal sealed record AgentUiFarmRequest(bool AcceptJobs);
internal sealed record AgentUiLimitsRequest(int LogicalProcessors, long MemoryMegabytes);
internal sealed record AgentUiEnrollRequest(string Url, string Code);

/// <summary>
/// Painel local do Agent: Kestrel preso a 127.0.0.1 com porta aleatória e um
/// token de sessão no caminho — outra pessoa na máquina não adivinha a URL, e
/// nada é exposto para fora do loopback.
/// </summary>
internal sealed class AgentUiServer(AgentUiHost host) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string _token =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private WebApplication? _app;

    public string Start()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        var root = $"/{_token}";
        app.MapGet(root + "/", () =>
            Results.Content(AgentUiPage.Html, "text/html; charset=utf-8"));
        app.MapGet(root + "/api/state", () =>
            Results.Json(AgentTelemetry.Instance.Snapshot(), JsonOptions));
        app.MapPost(root + "/api/farm", (AgentUiFarmRequest request) =>
            ExecuteAsync(() => host.SetAcceptJobsAsync(request.AcceptJobs)));
        app.MapPost(root + "/api/limits", (AgentUiLimitsRequest request) =>
            ExecuteAsync(() => host.SetLimitsAsync(
                request.LogicalProcessors, request.MemoryMegabytes)));
        app.MapPost(root + "/api/enroll", (AgentUiEnrollRequest request) =>
            ExecuteAsync(async () =>
            {
                await host.EnrollAsync(request.Url, request.Code);
                await host.RefreshIdentityAsync();
            }));
        app.MapPost(root + "/api/rotate", () =>
            ExecuteAsync(async () =>
            {
                await host.RotateCredentialAsync();
                await host.RefreshIdentityAsync();
            }));
        app.MapPost(root + "/api/identity/refresh", () =>
            ExecuteAsync(host.RefreshIdentityAsync));
        app.MapPost(root + "/api/exit", () =>
        {
            host.RequestExit();
            return Results.Json(new { ok = true }, JsonOptions);
        });

        app.Start();
        _app = app;
        var address = app.Urls.First();
        return $"{address.TrimEnd('/')}{root}/";
    }

    private static async Task<IResult> ExecuteAsync(Func<Task> action)
    {
        try
        {
            await action();
            return Results.Json(new { ok = true }, JsonOptions);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            ArgumentOutOfRangeException or
            InvalidOperationException or
            AgentAuthenticationException or
            HttpRequestException or
            OverflowException or
            TaskCanceledException)
        {
            return Results.Json(
                new { ok = false, error = exception.Message },
                JsonOptions,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    public void Dispose()
    {
        if (_app is null) return;
        try { _app.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch (Exception exception) when (exception is
            OperationCanceledException or
            ObjectDisposedException or
            InvalidOperationException)
        {
        }
    }
}
