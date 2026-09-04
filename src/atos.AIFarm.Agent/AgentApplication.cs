using System.Net.Http.Headers;
using System.Security.Cryptography;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Agent;

internal static class AgentApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        // Sem argumentos (duplo clique no executável) o Agent abre o painel
        // local; "run" continua sendo o modo headless para terminal e serviços.
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "ui";
        if (command == "__worker")
            return await AgentWorkerHost.RunAsync(Console.In, Console.Out, Console.Error);
        var configurationStore = new AgentConfigurationStore(
            Environment.GetEnvironmentVariable("AIFARM_AGENT_DATA_DIR"));
        var credentialStore = new WindowsCredentialStore();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "atos-agent",
                FarmAgentProtocol.CurrentAgentVersion["atos-agent/".Length..]));
        var client = new AgentClient(httpClient);

        try
        {
            return command switch
            {
                "enroll" => await EnrollAsync(args, client, configurationStore, credentialStore),
                "import" => await ImportAsync(args, configurationStore, credentialStore),
                "run" => await RunHeartbeatsAsync(client, configurationStore, credentialStore),
                "ui" => await AgentUiHost.RunAsync(client, configurationStore, credentialStore),
                "status" => await ShowStatusAsync(configurationStore, credentialStore),
                "pause" => await SetAvailabilityAsync(false, configurationStore),
                "resume" => await SetAvailabilityAsync(true, configurationStore),
                "limits" => await SetLimitsAsync(args, configurationStore),
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
            enrollment.HeartbeatIntervalSeconds,
            Policy: AgentLocalPolicy.Default()), cancellation.Source.Token);
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
            30,
            Policy: AgentLocalPolicy.Default()));
        Write("OK", $"Node {nodeId} importado para o Windows Credential Manager.");
        return 0;
    }

    internal static async Task<int> RunHeartbeatsAsync(
        AgentClient client,
        AgentConfigurationStore configurationStore,
        WindowsCredentialStore credentialStore,
        AgentCredentialHandle? credentialHandle = null,
        CancellationToken externalToken = default)
    {
        var telemetry = AgentTelemetry.Instance;
        var configuration = await RequiredConfigurationAsync(configurationStore);
        var initialCredential = credentialStore.Read(configuration.CredentialTarget);
        if (string.IsNullOrWhiteSpace(initialCredential))
            throw new AgentAuthenticationException(
                "Credencial local ausente. Execute 'atos-agent enroll <url>'.");
        var credentials = credentialHandle ?? new AgentCredentialHandle();
        credentials.Current = initialCredential;

        using var cancellation = CreateCancellation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellation.Source.Token, externalToken);
        telemetry.SetBinding(configuration.NodeId, configuration.BaseUrl);
        telemetry.SetPolicy(configuration.EffectivePolicy);
        telemetry.SetPhase("CONECTANDO");
        // A largada também precisa de retry: um servidor indisponível na
        // abertura não pode matar a sessão (só Ctrl+C, painel ou auth).
        (AgentConfiguration Configuration, string VerificationKey) trusted;
        var trustFailures = 0;
        while (true)
        {
            try
            {
                trusted = await EnsureManifestTrustAsync(
                    client,
                    configuration,
                    credentials.Current,
                    configurationStore,
                    credentialStore,
                    linked.Token);
                break;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException &&
                !linked.IsCancellationRequested)
            {
                trustFailures++;
                var delay = Math.Min(60, Math.Max(5, 5 * (1 << Math.Min(trustFailures - 1, 3))));
                telemetry.SetPhase("RECONECTANDO", exception.Message);
                Write("RETRY", $"{exception.Message} // NOVA TENTATIVA={delay}s");
                await Task.Delay(TimeSpan.FromSeconds(delay), linked.Token);
            }
        }
        configuration = trusted.Configuration;
        var manifestVerificationKey = trusted.VerificationKey;
        var worker = AgentWorkerProcessRunner.CreateCurrent();
        var failures = 0;
        var interval = Math.Clamp(configuration.HeartbeatIntervalSeconds, 10, 300);
        var activeJobs = new Dictionary<Guid, ActiveJobExecution>();
        Write(
            "START",
            $"Node {configuration.NodeId} // {configuration.BaseUrl} // {ProfileLabel()} // {PolicyLabel(configuration.EffectivePolicy)} // Ctrl+C para parar.");
        try
        {
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    foreach (var completedJob in activeJobs.Values
                                 .Where(x => x.Completion.IsCompleted).ToArray())
                    {
                        activeJobs.Remove(completedJob.JobId);
                        try { await completedJob.Completion; }
                        finally { completedJob.Dispose(); }
                    }

                    var latest = await RequiredConfigurationAsync(configurationStore);
                    if (latest.NodeId != configuration.NodeId ||
                        latest.CredentialTarget != configuration.CredentialTarget)
                        throw new AgentAuthenticationException(
                            "O vínculo local mudou. Reinicie o processo do Agent.");
                    configuration = latest with { Policy = latest.EffectivePolicy };
                    telemetry.SetPolicy(configuration.EffectivePolicy);
                    var credential = credentials.Current;
                    var activeJobIds = activeJobs.Keys.ToArray();
                    var heartbeat = await client.HeartbeatAsync(
                        configuration,
                        credential,
                        linked.Token,
                        activeJobIds);
                    failures = 0;
                    telemetry.SetHeartbeat(heartbeat.Status.ToString().ToUpperInvariant());
                    telemetry.SetPhase(
                        !configuration.EffectivePolicy.AcceptJobs
                            ? "PAUSADO"
                            : heartbeat.AcceptJobs ? "FARMANDO" : "EM_ESPERA");
                    interval = activeJobs.Count == 0
                        ? Math.Clamp(heartbeat.HeartbeatIntervalSeconds, 10, 300)
                        : Math.Clamp(heartbeat.ActiveHeartbeatIntervalSeconds, 1, 10);
                    Write(
                        "HEARTBEAT",
                        $"{heartbeat.Status.ToString().ToUpperInvariant()} // ACCEPT_JOBS={heartbeat.AcceptJobs} // JOBS={activeJobs.Count} // PRÓXIMO={interval}s");
                    var cancellationIds = heartbeat.CancelActiveJobIds ??
                        (heartbeat.CancelActiveJob && heartbeat.ActiveJobId is Guid legacyId ? [legacyId] : []);
                    foreach (var jobId in cancellationIds)
                    {
                        if (!activeJobs.TryGetValue(jobId, out var activeJob)) continue;
                        Write("CANCEL", $"JOB={activeJob.JobId:N} // solicitação recebida do servidor.");
                        activeJob.Cancel();
                    }
                    if (heartbeat.AcceptJobs)
                    {
                        while (true)
                        {
                            var availableProcessors = configuration.EffectivePolicy.MaxLogicalProcessors -
                                activeJobs.Values.Sum(x => x.LogicalProcessors);
                            var availableMemory = configuration.EffectivePolicy.MaxMemoryBytes -
                                activeJobs.Values.Sum(x => x.MemoryBytes);
                            if (availableProcessors <= 0 || availableMemory <= 0) break;
                            var activeJob = await TryStartNextJobAsync(
                                client, configuration, credentials, manifestVerificationKey, worker,
                                activeJobs.Keys.ToArray(), availableProcessors, availableMemory,
                                linked.Token);
                            if (activeJob is null) break;
                            activeJobs.Add(activeJob.JobId, activeJob);
                            interval = Math.Clamp(heartbeat.ActiveHeartbeatIntervalSeconds, 1, 10);
                        }
                    }
                }
                catch (AgentAuthenticationException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or TaskCanceledException &&
                    !linked.IsCancellationRequested)
                {
                    failures++;
                    interval = activeJobs.Count == 0
                        ? Math.Min(60, Math.Max(5, 5 * (1 << Math.Min(failures - 1, 3))))
                        : 2;
                    telemetry.SetPhase("RECONECTANDO", exception.Message);
                    Write("RETRY", $"{exception.Message} // NOVA TENTATIVA={interval}s");
                }

                await Task.Delay(TimeSpan.FromSeconds(interval), linked.Token);
            }
        }
        finally
        {
            foreach (var activeJob in activeJobs.Values)
                activeJob.Cancel();
            foreach (var activeJob in activeJobs.Values)
            {
                await activeJob.Completion;
                activeJob.Dispose();
            }
        }
        return 0;
    }

    private static async Task<(AgentConfiguration Configuration, string VerificationKey)>
        EnsureManifestTrustAsync(
            AgentClient client,
            AgentConfiguration configuration,
            string credential,
            AgentConfigurationStore configurationStore,
            WindowsCredentialStore credentialStore,
            CancellationToken cancellationToken)
    {
        var trust = await client.GetTrustAsync(configuration, credential, cancellationToken);
        if (trust.Algorithm != "HMAC-SHA256-NODE-V1")
            throw new InvalidOperationException("O servidor anunciou um algoritmo de trust não autorizado.");
        if (configuration.ManifestKeyId is not null &&
            !string.Equals(configuration.ManifestKeyId, trust.KeyId, StringComparison.Ordinal))
            throw new AgentAuthenticationException(
                "A chave de assinatura do servidor mudou. Um novo vínculo explícito é necessário.");

        var target = configuration.ManifestKeyTarget ??
            $"atos.ai-farm.agent-manifest/{configuration.NodeId:N}";
        var verificationKey = credentialStore.Read(target);
        if (string.IsNullOrWhiteSpace(verificationKey))
        {
            credentialStore.Save(target, configuration.NodeId.ToString(), trust.VerificationKey);
            verificationKey = trust.VerificationKey;
        }
        var localKeyId = ManifestKeyId(verificationKey);
        if (!string.Equals(localKeyId, trust.KeyId, StringComparison.Ordinal))
            throw new AgentAuthenticationException(
                "A chave de manifesto armazenada localmente não corresponde ao vínculo fixado.");
        configuration = configuration with
        {
            ManifestKeyTarget = target,
            ManifestKeyId = trust.KeyId,
            Policy = configuration.EffectivePolicy
        };
        await configurationStore.SaveAsync(configuration, cancellationToken);
        Write("TRUST", $"HMAC-SHA256-NODE-V1 // KEY_ID={trust.KeyId}");
        return (configuration, verificationKey);
    }

    private static string ManifestKeyId(string verificationKey)
    {
        byte[] key;
        try { key = Convert.FromBase64String(verificationKey); }
        catch (FormatException)
        {
            throw new AgentAuthenticationException(
                "A chave de manifesto armazenada localmente é inválida.");
        }
        try
        {
            return Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()[..16];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<ActiveJobExecution?> TryStartNextJobAsync(
        AgentClient client,
        AgentConfiguration configuration,
        AgentCredentialHandle credentials,
        string manifestVerificationKey,
        AgentWorkerProcessRunner worker,
        IReadOnlyList<Guid> activeJobIds,
        int availableLogicalProcessors,
        long availableMemoryBytes,
        CancellationToken cancellationToken)
    {
        var offer = await client.ClaimJobAsync(
            configuration, credentials.Current, cancellationToken, activeJobIds);
        if (offer is null) return null;
        RemoteJobManifest manifest;
        try
        {
            manifest = RemoteJobVerifier.Verify(
                offer,
                configuration,
                manifestVerificationKey,
                DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException exception)
        {
            await client.RejectJobAsync(
                configuration,
                credentials.Current,
                offer.JobId,
                exception.Message[..Math.Min(exception.Message.Length, 200)],
                cancellationToken);
            Write("REJECT", $"JOB={offer.JobId:N} // {exception.Message}");
            return null;
        }

        if (manifest.MaxLogicalProcessors > availableLogicalProcessors ||
            manifest.MaxMemoryBytes > availableMemoryBytes)
        {
            await client.RejectJobAsync(
                configuration, credentials.Current, offer.JobId,
                "Capacidade residual local insuficiente para o manifesto.",
                cancellationToken);
            Write("REJECT", $"JOB={offer.JobId:N} // capacidade residual insuficiente.");
            return null;
        }

        if (!await client.MarkJobStartedAsync(
            configuration,
            credentials.Current,
            offer.JobId,
            cancellationToken))
        {
            await client.RejectJobAsync(
                configuration,
                credentials.Current,
                offer.JobId,
                "O servidor revogou a oferta antes do início.",
                CancellationToken.None,
                FarmRemoteJobRejectionKind.Cancelled);
            return null;
        }
        Write("JOB", $"RUNNING // {offer.JobId:N} // {manifest.WorkloadVersion}");
        AgentTelemetry.Instance.JobStarted(
            offer.JobId,
            manifest.WorkloadVersion,
            manifest.MaxLogicalProcessors,
            manifest.MaxMemoryBytes);
        var remoteCancellation = new CancellationTokenSource();
        var completion = ExecuteClaimedJobAsync(
            client,
            configuration,
            credentials,
            offer,
            manifest,
            worker,
            remoteCancellation.Token,
            cancellationToken);
        return new ActiveJobExecution(
            offer.JobId, manifest.MaxLogicalProcessors, manifest.MaxMemoryBytes,
            remoteCancellation, completion);
    }

    private static async Task ExecuteClaimedJobAsync(
        AgentClient client,
        AgentConfiguration configuration,
        AgentCredentialHandle credentials,
        FarmRemoteJobOffer offer,
        RemoteJobManifest manifest,
        AgentWorkerProcessRunner worker,
        CancellationToken remoteCancellationToken,
        CancellationToken processCancellationToken)
    {
        var telemetry = AgentTelemetry.Instance;
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            processCancellationToken,
            remoteCancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(manifest.TimeoutSeconds));
        try
        {
            var workerResult = await worker.RunAsync(manifest, offer.Input, timeout.Token);
            var acknowledgement = await client.SubmitResultAsync(
                configuration,
                credentials.Current,
                offer.JobId,
                new FarmRemoteJobResult(
                    workerResult.Digest,
                    workerResult.DurationMilliseconds,
                    workerResult.PeakMemoryBytes,
                    workerResult.ProcessorTimeMilliseconds,
                    workerResult.Enforcement,
                    workerResult.OutputPayload),
                execution.Token);
            telemetry.JobFinished(
                offer.JobId,
                acknowledgement.Verified,
                acknowledgement.ComputeUnitsMilli,
                workerResult.ProcessorTimeMilliseconds,
                workerResult.PeakMemoryBytes);
            Write(
                "JOB",
                $"{acknowledgement.Status.ToString().ToUpperInvariant()} // VERIFIED={acknowledgement.Verified} // CP={acknowledgement.ComputeUnitsMilli / 1000d:0.000} // PEAK={workerResult.PeakMemoryBytes / 1024d / 1024d:0.0}MB // CPU={workerResult.ProcessorTimeMilliseconds}ms // LIMIT={workerResult.Enforcement}");
        }
        catch (OperationCanceledException) when (!execution.IsCancellationRequested)
        {
            telemetry.JobFinished(offer.JobId, false, 0, 0, 0);
            await client.RejectJobAsync(
                configuration,
                credentials.Current,
                offer.JobId,
                "Timeout local do manifesto.",
                CancellationToken.None,
                FarmRemoteJobRejectionKind.Timeout);
            Write("JOB", $"TIMEOUT // {offer.JobId:N}");
        }
        catch (OperationCanceledException)
        {
            telemetry.JobFinished(offer.JobId, false, 0, 0, 0);
            await client.RejectJobAsync(
                configuration,
                credentials.Current,
                offer.JobId,
                remoteCancellationToken.IsCancellationRequested
                    ? "Cancelamento cooperativo recebido do servidor."
                    : "Agent encerrado durante a execução.",
                CancellationToken.None,
                FarmRemoteJobRejectionKind.Cancelled);
            Write("JOB", $"CANCELLED // {offer.JobId:N}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            telemetry.JobFinished(offer.JobId, false, 0, 0, 0);
            var reason = $"Worker local falhou: {exception.Message}";
            await client.RejectJobAsync(
                configuration,
                credentials.Current,
                offer.JobId,
                reason[..Math.Min(reason.Length, 200)],
                CancellationToken.None,
                FarmRemoteJobRejectionKind.Policy);
            Write("JOB", $"WORKER_FAILED // {offer.JobId:N} // {exception.Message}");
        }
    }

    private sealed class ActiveJobExecution(
        Guid jobId,
        int logicalProcessors,
        long memoryBytes,
        CancellationTokenSource cancellation,
        Task completion) : IDisposable
    {
        public Guid JobId { get; } = jobId;
        public int LogicalProcessors { get; } = logicalProcessors;
        public long MemoryBytes { get; } = memoryBytes;
        public Task Completion { get; } = completion;
        public void Cancel() => cancellation.Cancel();
        public void Dispose() => cancellation.Dispose();
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
        var policy = configuration.EffectivePolicy;
        Write(
            "STATUS",
            $"NODE={configuration.NodeId} // SERVER={configuration.BaseUrl} // CREDENTIAL={(credential is null ? "AUSENTE" : "SEGURA")} // {ProfileLabel()} // {PolicyLabel(policy)}");
        return credential is null ? 1 : 0;
    }

    private static async Task<int> SetAvailabilityAsync(
        bool acceptJobs,
        AgentConfigurationStore configurationStore)
    {
        var configuration = await RequiredConfigurationAsync(configurationStore);
        var policy = configuration.EffectivePolicy with { AcceptJobs = acceptJobs };
        await configurationStore.SaveAsync(configuration with { Policy = policy });
        Write(
            acceptJobs ? "RESUME" : "PAUSE",
            acceptJobs
                ? "Novos Jobs serão aceitos após o próximo heartbeat."
                : "Nenhum novo Job será aceito; uma execução atual poderá terminar.");
        return 0;
    }

    private static async Task<int> SetLimitsAsync(
        string[] args,
        AgentConfigurationStore configurationStore)
    {
        if (args.Length < 3 ||
            !int.TryParse(args[1], out var logicalProcessors) ||
            !long.TryParse(args[2], out var memoryMegabytes))
            throw new ArgumentException("Uso: atos-agent limits <cpu> <memoria-mb>");
        long memoryBytes;
        try { memoryBytes = checked(memoryMegabytes * 1024L * 1024L); }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryMegabytes));
        }
        var configuration = await RequiredConfigurationAsync(configurationStore);
        var policy = AgentLocalPolicy.Normalize(
            configuration.EffectivePolicy with
            {
                MaxLogicalProcessors = logicalProcessors,
                MaxMemoryBytes = memoryBytes
            });
        await configurationStore.SaveAsync(configuration with { Policy = policy });
        Write("LIMITS", PolicyLabel(policy));
        return 0;
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
        Console.WriteLine("  atos-agent               Abre o painel local (o mesmo que 'ui').");
        Console.WriteLine("  atos-agent ui            Painel Macintosh no navegador, com farm ligado.");
        Console.WriteLine("  atos-agent enroll [url]  Vincula esta máquina usando código de uso único.");
        Console.WriteLine("  atos-agent run           Envia heartbeats até Ctrl+C (modo headless).");
        Console.WriteLine("  atos-agent status        Mostra configuração e presença da credencial.");
        Console.WriteLine("  atos-agent pause         Para de aceitar novos Jobs após o Job atual.");
        Console.WriteLine("  atos-agent resume        Volta a aceitar novos Jobs.");
        Console.WriteLine("  atos-agent limits <cpu> <memoria-mb>  Define a capacidade máxima autorizada.");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Write("ERRO", $"Comando desconhecido: {command}");
        ShowHelp();
        return 2;
    }

    internal static void Write(string status, string message)
    {
        AgentTelemetry.Instance.Log(status, message);
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} // {status} // {message}");
    }

    private static string PolicyLabel(AgentLocalPolicy policy) =>
        $"ACCEPT_JOBS={policy.AcceptJobs} // CPU_MAX={policy.MaxLogicalProcessors} // MEMORY_MAX={policy.MaxMemoryBytes / 1024 / 1024}MB";

    private static string ProfileLabel()
    {
        var inventory = AgentInventory.Heartbeat();
        var fingerprint = FarmNodeProfileFingerprint.Compute(
            inventory.Runtime,
            inventory.LogicalProcessors,
            inventory.MemoryBytes,
            inventory.AgentVersion);
        return $"PROFILE={fingerprint[..12]}";
    }

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
