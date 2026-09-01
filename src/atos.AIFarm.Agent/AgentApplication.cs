using System.Net.Http.Headers;

namespace atos.AIFarm.Agent;

internal static class AgentApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "run";
        var configurationStore = new AgentConfigurationStore(
            Environment.GetEnvironmentVariable("AIFARM_AGENT_DATA_DIR"));
        var credentialStore = new WindowsCredentialStore();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("atos-agent", "0.1.0"));
        var client = new AgentClient(httpClient);

        try
        {
            return command switch
            {
                "enroll" => await EnrollAsync(args, client, configurationStore, credentialStore),
                "import" => await ImportAsync(args, configurationStore, credentialStore),
                "run" => await RunHeartbeatsAsync(client, configurationStore, credentialStore),
                "status" => await ShowStatusAsync(configurationStore, credentialStore),
                "help" or "--help" or "-h" => ShowHelp(),
                _ => UnknownCommand(command)
            };
        }
        catch (AgentAuthenticationException exception)
        {
            Write("AUTH", exception.Message);
            return 3;
        }
        catch (OperationCanceledException)
        {
            Write("STOP", "Agent encerrado.");
            return 0;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            HttpRequestException or
            IOException or
            InvalidOperationException or
            PlatformNotSupportedException)
        {
            Write("ERRO", exception.Message);
            return 2;
        }
    }

    private static async Task<int> EnrollAsync(
        string[] args,
        AgentClient client,
        AgentConfigurationStore configurationStore,
        WindowsCredentialStore credentialStore)
    {
        var baseUrl = args.ElementAtOrDefault(1) ?? Prompt("URL da AI Farm", "http://localhost:5159");
        baseUrl = AgentEndpoint.Normalize(baseUrl);
        var token = Environment.GetEnvironmentVariable("AIFARM_AGENT_ENROLLMENT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            token = ReadSecret("Código de vínculo");
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Código de vínculo obrigatório.");

        using var cancellation = CreateCancellation();
        var enrollment = await client.EnrollAsync(baseUrl, token, cancellation.Source.Token);
        var target = CredentialTarget(enrollment.NodeId);
        credentialStore.Save(target, enrollment.NodeId.ToString(), enrollment.Credential);
        await configurationStore.SaveAsync(new AgentConfiguration(
            baseUrl,
            enrollment.NodeId,
            target,
            AgentInventory.Version,
            enrollment.HeartbeatIntervalSeconds), cancellation.Source.Token);
        Write("OK", $"Node {enrollment.NodeId} vinculado. A credencial está no Windows Credential Manager.");
        return 0;
    }

    private static async Task<int> ImportAsync(
        string[] args,
        AgentConfigurationStore configurationStore,
        WindowsCredentialStore credentialStore)
    {
        if (args.Length < 3 || !Guid.TryParse(args[2], out var nodeId))
            throw new ArgumentException("Uso: atos-agent import <url> <node-id>");
        var credential = Environment.GetEnvironmentVariable("AIFARM_AGENT_CREDENTIAL");
        if (string.IsNullOrWhiteSpace(credential))
            throw new ArgumentException("AIFARM_AGENT_CREDENTIAL é obrigatória para a importação.");

        var baseUrl = AgentEndpoint.Normalize(args[1]);
        var target = CredentialTarget(nodeId);
        credentialStore.Save(target, nodeId.ToString(), credential);
        await configurationStore.SaveAsync(new AgentConfiguration(
            baseUrl,
            nodeId,
            target,
            AgentInventory.Version,
            30));
        Write("OK", $"Node {nodeId} importado para o Windows Credential Manager.");
        return 0;
    }

    private static async Task<int> RunHeartbeatsAsync(
        AgentClient client,
        AgentConfigurationStore configurationStore,
        WindowsCredentialStore credentialStore)
    {
        var configuration = await RequiredConfigurationAsync(configurationStore);
        var credential = credentialStore.Read(configuration.CredentialTarget);
        if (string.IsNullOrWhiteSpace(credential))
            throw new AgentAuthenticationException(
                "Credencial local ausente. Execute 'atos-agent enroll <url>'.");

        using var cancellation = CreateCancellation();
        var failures = 0;
        var interval = Math.Clamp(configuration.HeartbeatIntervalSeconds, 10, 300);
        Write("START", $"Node {configuration.NodeId} // {configuration.BaseUrl} // Ctrl+C para parar.");
        while (!cancellation.Source.IsCancellationRequested)
        {
            try
            {
                var heartbeat = await client.HeartbeatAsync(
                    configuration,
                    credential,
                    cancellation.Source.Token);
                failures = 0;
                interval = Math.Clamp(heartbeat.HeartbeatIntervalSeconds, 10, 300);
                Write(
                    "HEARTBEAT",
                    $"{heartbeat.Status.ToString().ToUpperInvariant()} // ACCEPT_JOBS={heartbeat.AcceptJobs} // PRÓXIMO={interval}s");
            }
            catch (AgentAuthenticationException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException &&
                !cancellation.Source.IsCancellationRequested)
            {
                failures++;
                interval = Math.Min(60, Math.Max(5, 5 * (1 << Math.Min(failures - 1, 3))));
                Write("RETRY", $"{exception.Message} // NOVA TENTATIVA={interval}s");
            }

            await Task.Delay(TimeSpan.FromSeconds(interval), cancellation.Source.Token);
        }
        return 0;
    }

    private static async Task<int> ShowStatusAsync(
        AgentConfigurationStore configurationStore,
        WindowsCredentialStore credentialStore)
    {
        var configuration = await configurationStore.LoadAsync();
        if (configuration is null)
        {
            Write("STATUS", "NÃO VINCULADO");
            return 1;
        }
        var credential = credentialStore.Read(configuration.CredentialTarget);
        Write(
            "STATUS",
            $"NODE={configuration.NodeId} // SERVER={configuration.BaseUrl} // CREDENTIAL={(credential is null ? "AUSENTE" : "SEGURA")}");
        return credential is null ? 1 : 0;
    }

    private static async Task<AgentConfiguration> RequiredConfigurationAsync(
        AgentConfigurationStore configurationStore) =>
        await configurationStore.LoadAsync() ?? throw new InvalidOperationException(
            "Agent não vinculado. Execute 'atos-agent enroll <url>'.");

    private static string CredentialTarget(Guid nodeId) =>
        $"atos.ai-farm.agent/{nodeId:N}";

    private static string Prompt(string label, string defaultValue)
    {
        Console.Write($"{label} [{defaultValue}]: ");
        var value = Console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static string ReadSecret(string label)
    {
        Console.Write($"{label}: ");
        if (Console.IsInputRedirected) return Console.ReadLine() ?? string.Empty;
        var value = new List<char>();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && value.Count > 0)
            {
                value.RemoveAt(value.Count - 1);
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Add(key.KeyChar);
                Console.Write('*');
            }
        }
        Console.WriteLine();
        return new string([.. value]);
    }

    private static int ShowHelp()
    {
        Console.WriteLine("atos Compute Agent");
        Console.WriteLine("  atos-agent enroll [url]  Vincula esta máquina usando código de uso único.");
        Console.WriteLine("  atos-agent run           Envia heartbeats até Ctrl+C.");
        Console.WriteLine("  atos-agent status        Mostra configuração e presença da credencial.");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Write("ERRO", $"Comando desconhecido: {command}");
        ShowHelp();
        return 2;
    }

    private static void Write(string status, string message) =>
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} // {status} // {message}");

    private static CancellationRegistration CreateCancellation() => new();

    private sealed class CancellationRegistration : IDisposable
    {
        public CancellationTokenSource Source { get; } = new();

        public CancellationRegistration() => Console.CancelKeyPress += Cancel;

        private void Cancel(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            Source.Cancel();
        }

        public void Dispose()
        {
            Console.CancelKeyPress -= Cancel;
            Source.Dispose();
        }
    }
}
