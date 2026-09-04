using System.Diagnostics;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Agent;

/// <summary>
/// Porta-credencial mutável: o loop de heartbeats e os jobs em execução leem
/// sempre o valor atual, então uma rotação no meio da sessão não derruba nada.
/// </summary>
internal sealed class AgentCredentialHandle
{
    private volatile string _current = string.Empty;
    public string Current { get => _current; set => _current = value; }
}

/// <summary>
/// Orquestra o modo janela: mantém o loop de heartbeats vivo em segundo
/// plano, executa os comandos vindos do painel local (farmar, limites,
/// vínculo, rotação) e expõe o desligamento coordenado.
/// </summary>
internal sealed class AgentUiHost(
    AgentClient client,
    AgentConfigurationStore configurationStore,
    WindowsCredentialStore credentialStore)
{
    private readonly AgentCredentialHandle _credentials = new();
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _exitRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private CancellationTokenSource? _loopCancellation;

    public static async Task<int> RunAsync(
        AgentClient client,
        AgentConfigurationStore configurationStore,
        WindowsCredentialStore credentialStore)
    {
        var host = new AgentUiHost(client, configurationStore, credentialStore);
        await host.StartLoopIfBoundAsync();
        // Batida de 1s da aba Desempenho: registra utilização mesmo ociosa,
        // para o gráfico mostrar os vales de verdade.
        _ = Task.Run(async () =>
        {
            var ticks = 0;
            while (!host._shutdown.IsCancellationRequested)
            {
                AgentTelemetry.Instance.RecordUtilizationSample();
                // CP total e política vêm do servidor: renova a cada 5 min.
                if (++ticks % 300 == 0) _ = host.RefreshIdentityAsync();
                try { await Task.Delay(TimeSpan.FromSeconds(1), host._shutdown.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        using var server = new AgentUiServer(host);
        var url = server.Start();
        AgentApplication.Write("PAINEL", $"Painel local em {url}");
        OpenWindow(url);
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            host.RequestExit();
        };
        await host._exitRequested.Task;
        await host.ShutdownAsync();
        AgentApplication.Write("STOP", "Agent encerrado pelo painel.");
        return 0;
    }

    public void RequestExit() => _exitRequested.TrySetResult();

    public async Task ShutdownAsync()
    {
        _shutdown.Cancel();
        if (_loop is { } loop)
        {
            try { await loop; }
            catch (OperationCanceledException) { }
            catch (AgentAuthenticationException) { }
        }
    }

    public async Task StartLoopIfBoundAsync()
    {
        await _controlGate.WaitAsync();
        try
        {
            if (_loop is { IsCompleted: false }) return;
            var configuration = await configurationStore.LoadAsync();
            if (configuration is null)
            {
                AgentTelemetry.Instance.SetPhase("SEM_VINCULO");
                return;
            }
            var credential = credentialStore.Read(configuration.CredentialTarget);
            if (string.IsNullOrWhiteSpace(credential))
            {
                AgentTelemetry.Instance.SetPhase(
                    "SEM_VINCULO",
                    "A credencial local não foi encontrada no Cofre do Windows.");
                return;
            }
            _credentials.Current = credential;
            _loopCancellation?.Dispose();
            _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var loopToken = _loopCancellation.Token;
            _loop = Task.Run(() => RunLoopAsync(loopToken));
            _ = RefreshIdentityAsync();
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken loopToken)
    {
        try
        {
            await AgentApplication.RunHeartbeatsAsync(
                client,
                configurationStore,
                credentialStore,
                _credentials,
                loopToken);
            AgentTelemetry.Instance.SetPhase("PARADO");
        }
        catch (OperationCanceledException)
        {
            AgentTelemetry.Instance.SetPhase("PARADO");
        }
        catch (AgentAuthenticationException exception)
        {
            AgentTelemetry.Instance.SetPhase("CREDENCIAL_RECUSADA", exception.Message);
            AgentApplication.Write("AUTH", exception.Message);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            HttpRequestException or
            IOException or
            InvalidOperationException)
        {
            AgentTelemetry.Instance.SetPhase("ERRO", exception.Message);
            AgentApplication.Write("ERRO", exception.Message);
        }
    }

    public async Task SetAcceptJobsAsync(bool acceptJobs)
    {
        await _controlGate.WaitAsync();
        try
        {
            var configuration = await RequiredConfigurationAsync();
            var policy = configuration.EffectivePolicy with { AcceptJobs = acceptJobs };
            await configurationStore.SaveAsync(configuration with { Policy = policy });
            AgentTelemetry.Instance.SetPolicy(policy);
            AgentApplication.Write(
                acceptJobs ? "RESUME" : "PAUSE",
                acceptJobs
                    ? "Farm ligado pelo painel: novos trabalhos serão aceitos."
                    : "Farm pausado pelo painel: nenhum novo trabalho será aceito.");
        }
        finally
        {
            _controlGate.Release();
        }
        // Ligar o farm também ressuscita uma sessão que morreu (ex.: erro na
        // largada antes do retry existir, ou credencial recusada já resolvida).
        if (acceptJobs) await StartLoopIfBoundAsync();
    }

    public async Task SetLimitsAsync(int logicalProcessors, long memoryMegabytes)
    {
        await _controlGate.WaitAsync();
        try
        {
            var memoryBytes = checked(memoryMegabytes * 1024L * 1024L);
            var configuration = await RequiredConfigurationAsync();
            var policy = AgentLocalPolicy.Normalize(
                configuration.EffectivePolicy with
                {
                    MaxLogicalProcessors = logicalProcessors,
                    MaxMemoryBytes = memoryBytes
                });
            await configurationStore.SaveAsync(configuration with { Policy = policy });
            AgentTelemetry.Instance.SetPolicy(policy);
            AgentApplication.Write(
                "LIMITS",
                $"ACCEPT_JOBS={policy.AcceptJobs} // CPU_MAX={policy.MaxLogicalProcessors} // MEMORY_MAX={policy.MaxMemoryBytes / 1024 / 1024}MB");
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task EnrollAsync(string baseUrl, string enrollmentToken)
    {
        await _controlGate.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(enrollmentToken))
                throw new ArgumentException("Código de vínculo obrigatório.");
            var normalized = AgentEndpoint.Normalize(baseUrl);
            var enrollment = await client.EnrollAsync(
                normalized, enrollmentToken.Trim(), _shutdown.Token);
            var target = $"atos.ai-farm.agent/{enrollment.NodeId:N}";
            credentialStore.Save(target, enrollment.NodeId.ToString(), enrollment.Credential);
            await configurationStore.SaveAsync(new AgentConfiguration(
                normalized,
                enrollment.NodeId,
                target,
                AgentInventory.Version,
                enrollment.HeartbeatIntervalSeconds,
                Policy: AgentLocalPolicy.Default()), _shutdown.Token);
            AgentApplication.Write(
                "OK",
                $"Node {enrollment.NodeId} vinculado. A credencial está no Windows Credential Manager.");
            // O loop antigo — mesmo preso em retry num servidor morto — precisa
            // morrer aqui, senão o vínculo novo nunca assume a sessão.
            _loopCancellation?.Cancel();
            if (_loop is { } previous)
            {
                try { await previous; }
                catch (OperationCanceledException) { }
                catch (AgentAuthenticationException) { }
            }
            _loop = null;
        }
        finally
        {
            _controlGate.Release();
        }
        await StartLoopIfBoundAsync();
    }

    public async Task<FarmAgentCredentialRotation> RotateCredentialAsync()
    {
        await _controlGate.WaitAsync();
        try
        {
            var configuration = await RequiredConfigurationAsync();
            var current = credentialStore.Read(configuration.CredentialTarget)
                ?? throw new AgentAuthenticationException(
                    "Credencial local ausente; refaça o vínculo.");
            var rotation = await client.RotateCredentialAsync(
                configuration, current, _shutdown.Token);
            credentialStore.Save(
                configuration.CredentialTarget,
                rotation.NodeId.ToString(),
                rotation.Credential);
            _credentials.Current = rotation.Credential;
            AgentApplication.Write(
                "ROTATE",
                $"Credencial rotacionada em {rotation.IssuedAtUtc:HH:mm:ss} UTC. A anterior foi invalidada no servidor.");
            return rotation;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task RefreshIdentityAsync()
    {
        try
        {
            var configuration = await configurationStore.LoadAsync();
            if (configuration is null) return;
            var credential = _credentials.Current;
            if (string.IsNullOrWhiteSpace(credential))
                credential = credentialStore.Read(configuration.CredentialTarget) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(credential)) return;
            var identity = await client.GetIdentityAsync(
                configuration, credential, _shutdown.Token);
            AgentTelemetry.Instance.SetIdentity(identity);
        }
        catch (Exception exception) when (exception is
            AgentAuthenticationException or
            HttpRequestException or
            InvalidOperationException or
            TaskCanceledException)
        {
            // Identidade é informativa: falhas aqui não derrubam o painel.
        }
    }

    private async Task<AgentConfiguration> RequiredConfigurationAsync() =>
        await configurationStore.LoadAsync() ?? throw new InvalidOperationException(
            "Agent não vinculado. Use o formulário de vínculo do painel.");

    private static void OpenWindow(string url)
    {
        // Edge em modo app dá uma janela dedicada sem barra de endereço; o
        // fallback abre o navegador padrão. Ambos falham silenciosamente em
        // sessões sem shell gráfico.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "msedge.exe",
                Arguments = $"--app={url}",
                UseShellExecute = true
            });
            return;
        }
        catch (Exception exception) when (exception is
            System.ComponentModel.Win32Exception or
            InvalidOperationException or
            PlatformNotSupportedException)
        {
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception exception) when (exception is
            System.ComponentModel.Win32Exception or
            InvalidOperationException or
            PlatformNotSupportedException)
        {
            AgentApplication.Write(
                "PAINEL",
                $"Abra manualmente no navegador: {url}");
        }
    }
}
