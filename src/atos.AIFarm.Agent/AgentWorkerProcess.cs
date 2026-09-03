using System.Diagnostics;
using System.Text.Json;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Agent;

internal sealed record AgentWorkerRequest(
    string Protocol,
    string Workload,
    string WorkloadVersion,
    string Nonce,
    int Iterations,
    string? InputPayload = null);

internal sealed record AgentWorkerResult(
    string Protocol,
    string Digest,
    long DurationMilliseconds,
    long PeakMemoryBytes = 0,
    long ProcessorTimeMilliseconds = 0,
    string Enforcement = "UNREPORTED",
    string? OutputPayload = null);

internal sealed record AgentWorkerLaunch(string FileName, IReadOnlyList<string> Arguments);

internal static class AgentWorkerProtocol
{
    public const string Version = "atos-worker-ipc-v3";
    public const string Workload = "SHA256_CHAIN";
    public const int Iterations = 100_000;
    public const int MaximumMessageCharacters = 96 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

internal static class AgentWorkerHost
{
    public static async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await ReadBoundedAsync(input, cancellationToken);
            var request = JsonSerializer.Deserialize<AgentWorkerRequest>(
                payload,
                AgentWorkerProtocol.JsonOptions) ?? throw new InvalidOperationException(
                "A solicitação do worker está vazia.");
            var definition = Validate(request);
            var stopwatch = Stopwatch.StartNew();
            var execution = FarmDeterministicWorkloads.Execute(
                definition.Workload,
                request.Nonce,
                request.Iterations,
                request.InputPayload,
                cancellationToken);
            stopwatch.Stop();
            var result = new AgentWorkerResult(
                AgentWorkerProtocol.Version,
                execution.Digest,
                Math.Max(1, stopwatch.ElapsedMilliseconds),
                OutputPayload: execution.OutputPayload);
            await output.WriteLineAsync(JsonSerializer.Serialize(
                result,
                AgentWorkerProtocol.JsonOptions));
            await output.FlushAsync(cancellationToken);
            return 0;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException or
            JsonException or
            IOException)
        {
            await error.WriteLineAsync($"WORKER_ERROR: {exception.Message}");
            return 2;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        TextReader input,
        CancellationToken cancellationToken)
    {
        var buffer = new char[AgentWorkerProtocol.MaximumMessageCharacters + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await input.ReadAsync(
                buffer.AsMemory(length, buffer.Length - length),
                cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length == 0 || length > AgentWorkerProtocol.MaximumMessageCharacters)
            throw new InvalidOperationException("A mensagem IPC do worker excede o limite permitido.");
        return new string(buffer, 0, length);
    }

    private static FarmWorkloadDefinition Validate(AgentWorkerRequest request)
    {
        var definition = FarmWorkloadCatalog.Find(request.Workload, request.WorkloadVersion);
        if (request.Protocol != AgentWorkerProtocol.Version ||
            definition is null ||
            request.Iterations != definition.Iterations)
            throw new InvalidOperationException("A solicitação viola a allowlist do worker.");
        byte[] nonce;
        try { nonce = Convert.FromHexString(request.Nonce); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("O nonce do worker não é hexadecimal.", exception);
        }
        if (nonce.Length != 16)
            throw new InvalidOperationException("O nonce do worker deve possuir 128 bits.");
        if (definition.InputSchema is null && request.InputPayload is not null)
            throw new InvalidOperationException("O workload não aceita entrada tipada.");
        if (definition.InputSchema is not null)
            _ = FarmDeterministicWorkloads.ComputeInputDigest(request.InputPayload!);
        return definition;
    }
}

internal sealed class AgentWorkerProcessRunner(AgentWorkerLaunch launch)
{
    public static AgentWorkerProcessRunner CreateCurrent() => new(ResolveCurrentLaunch());

    public Task<AgentWorkerResult> RunAsync(
        RemoteJobManifest manifest,
        CancellationToken cancellationToken) => RunAsync(manifest, null, cancellationToken);

    public async Task<AgentWorkerResult> RunAsync(
        RemoteJobManifest manifest,
        FarmRemoteJobInput? input,
        CancellationToken cancellationToken)
    {
        var definition = FarmWorkloadCatalog.Find(manifest.Workload, manifest.WorkloadVersion);
        if (definition is null || manifest.Iterations != definition.Iterations)
            throw new InvalidOperationException("O manifesto não pertence à allowlist do worker.");

        using var process = new Process
        {
            StartInfo = CreateStartInfo(launch, manifest.MaxMemoryBytes),
            EnableRaisingEvents = true
        };
        if (!process.Start())
            throw new InvalidOperationException("Não foi possível iniciar o processo worker.");
        using var resources = AgentWorkerResourceGuard.Attach(
            process,
            manifest.MaxLogicalProcessors,
            manifest.MaxMemoryBytes);

        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            var request = new AgentWorkerRequest(
                AgentWorkerProtocol.Version,
                manifest.Workload,
                manifest.WorkloadVersion,
                manifest.Nonce,
                manifest.Iterations,
                input?.Payload);
            var payload = JsonSerializer.Serialize(
                request,
                AgentWorkerProtocol.JsonOptions);
            await process.StandardInput.WriteAsync(payload.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                resources.Sample();
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Worker terminou com código {process.ExitCode}: {Sanitize(error)}");
        if (output.Length > AgentWorkerProtocol.MaximumMessageCharacters)
            throw new InvalidOperationException("A resposta IPC do worker excede o limite permitido.");
        var result = JsonSerializer.Deserialize<AgentWorkerResult>(
            output,
            AgentWorkerProtocol.JsonOptions) ?? throw new InvalidOperationException(
            "O worker retornou uma resposta vazia.");
        Validate(result);
        var telemetry = resources.Complete();
        return result with
        {
            PeakMemoryBytes = telemetry.PeakMemoryBytes,
            ProcessorTimeMilliseconds = telemetry.ProcessorTimeMilliseconds,
            Enforcement = telemetry.Enforcement
        };
    }

    private static ProcessStartInfo CreateStartInfo(
        AgentWorkerLaunch launch,
        long maximumMemoryBytes)
    {
        var start = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        var inheritedEnvironment = start.Environment.ToArray();
        start.Environment.Clear();
        foreach (var variable in inheritedEnvironment)
        {
            if (IsRequiredRuntimeVariable(variable.Key))
                start.Environment[variable.Key] = variable.Value;
        }
        start.Environment["DOTNET_GCHeapHardLimit"] = maximumMemoryBytes.ToString("X");
        start.Environment["DOTNET_GCConserveMemory"] = "9";
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("__worker");
        return start;
    }

    private static bool IsRequiredRuntimeVariable(string name) => name.Equals(
            "SystemRoot",
            StringComparison.OrdinalIgnoreCase) ||
        name.Equals("WINDIR", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TEMP", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TMP", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TMPDIR", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DOTNET_ROOT_X64", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DOTNET_ROOT_ARM64", StringComparison.OrdinalIgnoreCase);

    private static AgentWorkerLaunch ResolveCurrentLaunch()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException(
            "O caminho do executável atual não está disponível.");
        if (!string.Equals(
                Path.GetFileNameWithoutExtension(executable),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
            return new AgentWorkerLaunch(executable, []);

        var assembly = Path.Combine(AppContext.BaseDirectory, "atos-agent.dll");
        if (!File.Exists(assembly))
            throw new InvalidOperationException("O assembly de entrada do Agent não foi localizado.");
        return new AgentWorkerLaunch(executable, [assembly]);
    }

    private static void Validate(AgentWorkerResult result)
    {
        if (result.Protocol != AgentWorkerProtocol.Version ||
            result.DurationMilliseconds <= 0)
            throw new InvalidOperationException("O worker retornou metadados inválidos.");
        byte[] digest;
        try { digest = Convert.FromHexString(result.Digest); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("O worker retornou um digest inválido.", exception);
        }
        if (digest.Length != 32)
            throw new InvalidOperationException("O digest do worker deve possuir 256 bits.");
    }

    private static void Kill(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static string Sanitize(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }
}
