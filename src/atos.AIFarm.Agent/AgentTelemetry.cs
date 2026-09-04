using System.Diagnostics;
using atos.AIFarm.Core.Application;

namespace atos.AIFarm.Agent;

internal sealed record AgentTelemetryLogEntry(
    DateTimeOffset AtUtc,
    string Status,
    string Message);

internal sealed record AgentTelemetryActiveJob(
    Guid JobId,
    string WorkloadVersion,
    DateTimeOffset StartedAtUtc,
    int LogicalProcessors,
    long MemoryBytes);

internal sealed record AgentUtilizationSample(
    long UnixSeconds,
    double WorkerCpuPercent,
    long WorkerMemoryBytes,
    int ActiveJobs);

internal sealed record AgentTelemetrySnapshot(
    string Phase,
    string? PhaseDetail,
    Guid? NodeId,
    string? BaseUrl,
    string MachineName,
    int HardwareLogicalProcessors,
    long HardwareMemoryBytes,
    bool AcceptJobs,
    int MaxLogicalProcessors,
    long MaxMemoryBytes,
    DateTimeOffset? LastHeartbeatAtUtc,
    string? LastHeartbeatStatus,
    DateTimeOffset SessionStartedAtUtc,
    int JobsCompleted,
    int JobsFailed,
    long VerifiedCpMilli,
    long WorkerCpuMilliseconds,
    long PeakWorkerMemoryBytes,
    long LiveWorkerMemoryBytes,
    double LiveWorkerCpuPercent,
    FarmAgentIdentity? Identity,
    IReadOnlyList<AgentTelemetryActiveJob> ActiveJobs,
    IReadOnlyList<AgentTelemetryLogEntry> Log,
    IReadOnlyList<AgentUtilizationSample> Utilization,
    string? PendingConfirmationKey);

/// <summary>
/// Estado observável da sessão do Agent, compartilhado entre o loop de
/// heartbeats e o painel local. Toda mutação é serializada pelo lock interno;
/// o snapshot é imutável e barato de servir a cada poll da janela.
/// </summary>
internal sealed class AgentTelemetry
{
    public static AgentTelemetry Instance { get; } = new();

    private const int LogCapacity = 150;
    // 3 minutos de série a 1 amostra/s — o suficiente para a aba Desempenho.
    private const int UtilizationCapacity = 180;
    private readonly Queue<AgentUtilizationSample> _utilization = new();
    private readonly Lock _gate = new();
    private readonly Queue<AgentTelemetryLogEntry> _log = new();
    private readonly Dictionary<Guid, AgentTelemetryActiveJob> _activeJobs = [];
    private readonly Dictionary<int, WorkerSample> _workers = [];
    private string _phase = "SEM_VINCULO";
    private string? _phaseDetail;
    private Guid? _nodeId;
    private string? _baseUrl;
    private bool _acceptJobs = true;
    private int _maxLogicalProcessors = 1;
    private long _maxMemoryBytes;
    private DateTimeOffset? _lastHeartbeatAtUtc;
    private string? _lastHeartbeatStatus;
    private DateTimeOffset _sessionStartedAtUtc = DateTimeOffset.UtcNow;
    private int _jobsCompleted;
    private int _jobsFailed;
    private long _verifiedCpMilli;
    private long _workerCpuMilliseconds;
    private long _peakWorkerMemoryBytes;
    private FarmAgentIdentity? _identity;
    private string? _pendingConfirmationKey;

    private sealed record WorkerSample(
        long WorkingSetBytes,
        double CpuMilliseconds,
        DateTimeOffset AtUtc,
        double PreviousCpuMilliseconds,
        DateTimeOffset PreviousAtUtc);

    public void Log(string status, string message)
    {
        lock (_gate)
        {
            _log.Enqueue(new AgentTelemetryLogEntry(DateTimeOffset.UtcNow, status, message));
            while (_log.Count > LogCapacity) _log.Dequeue();
        }
    }

    public void SetPhase(string phase, string? detail = null)
    {
        lock (_gate)
        {
            _phase = phase;
            _phaseDetail = detail;
        }
    }

    public void SetBinding(Guid nodeId, string baseUrl)
    {
        lock (_gate)
        {
            _nodeId = nodeId;
            _baseUrl = baseUrl;
        }
    }

    public void SetPolicy(AgentLocalPolicy policy)
    {
        lock (_gate)
        {
            _acceptJobs = policy.AcceptJobs;
            _maxLogicalProcessors = policy.MaxLogicalProcessors;
            _maxMemoryBytes = policy.MaxMemoryBytes;
        }
    }

    public void SetHeartbeat(string status)
    {
        lock (_gate)
        {
            _lastHeartbeatAtUtc = DateTimeOffset.UtcNow;
            _lastHeartbeatStatus = status;
        }
    }

    public void SetIdentity(FarmAgentIdentity? identity)
    {
        lock (_gate) _identity = identity;
    }

    public void SetPendingConfirmationKey(string? key)
    {
        lock (_gate) _pendingConfirmationKey = key;
    }

    public void JobStarted(Guid jobId, string workloadVersion, int logicalProcessors, long memoryBytes)
    {
        lock (_gate)
        {
            _activeJobs[jobId] = new AgentTelemetryActiveJob(
                jobId, workloadVersion, DateTimeOffset.UtcNow, logicalProcessors, memoryBytes);
        }
    }

    public void JobFinished(Guid jobId, bool verified, long computeUnitsMilli, long processorTimeMilliseconds, long peakMemoryBytes)
    {
        lock (_gate)
        {
            _activeJobs.Remove(jobId);
            if (verified)
            {
                _jobsCompleted++;
                _verifiedCpMilli += Math.Max(0, computeUnitsMilli);
            }
            else
            {
                _jobsFailed++;
            }
            _workerCpuMilliseconds += Math.Max(0, processorTimeMilliseconds);
            _peakWorkerMemoryBytes = Math.Max(_peakWorkerMemoryBytes, peakMemoryBytes);
        }
    }

    public void SampleWorker(Process process)
    {
        try
        {
            var workingSet = process.WorkingSet64;
            var cpu = process.TotalProcessorTime.TotalMilliseconds;
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                _workers[process.Id] = _workers.TryGetValue(process.Id, out var previous)
                    ? new WorkerSample(workingSet, cpu, now, previous.CpuMilliseconds, previous.AtUtc)
                    : new WorkerSample(workingSet, cpu, now, cpu, now);
                _peakWorkerMemoryBytes = Math.Max(_peakWorkerMemoryBytes, workingSet);
            }
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            NotSupportedException or
            SystemException)
        {
        }
    }

    public void RemoveWorker(int processId)
    {
        lock (_gate) _workers.Remove(processId);
    }

    private (double CpuPercent, long MemoryBytes) LiveWorkerLoad(DateTimeOffset now)
    {
        long liveMemory = 0;
        double liveCpuPercent = 0;
        foreach (var worker in _workers.Values.ToArray())
        {
            // Amostras paradas há mais de 5s pertencem a workers já encerrados.
            if (now - worker.AtUtc > TimeSpan.FromSeconds(5)) continue;
            liveMemory += worker.WorkingSetBytes;
            var elapsed = (worker.AtUtc - worker.PreviousAtUtc).TotalMilliseconds;
            if (elapsed > 0)
                liveCpuPercent += Math.Clamp(
                    (worker.CpuMilliseconds - worker.PreviousCpuMilliseconds) / elapsed * 100d,
                    0,
                    100d * Environment.ProcessorCount);
        }
        return (liveCpuPercent, liveMemory);
    }

    /// <summary>
    /// Uma batida por segundo do host alimenta a série da aba Desempenho —
    /// inclusive com zeros, para o gráfico mostrar a ociosidade de verdade.
    /// </summary>
    public void RecordUtilizationSample()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var (cpu, memory) = LiveWorkerLoad(now);
            _utilization.Enqueue(new AgentUtilizationSample(
                now.ToUnixTimeSeconds(),
                Math.Round(cpu, 1),
                memory,
                _activeJobs.Count));
            while (_utilization.Count > UtilizationCapacity) _utilization.Dequeue();
        }
    }

    public (double CpuPercent, long MemoryBytes) LiveLoad()
    {
        lock (_gate) return LiveWorkerLoad(DateTimeOffset.UtcNow);
    }

    public AgentTelemetrySnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var (liveCpuPercent, liveMemory) = LiveWorkerLoad(now);

            return new AgentTelemetrySnapshot(
                _phase,
                _phaseDetail,
                _nodeId,
                _baseUrl,
                Environment.MachineName.ToUpperInvariant(),
                AgentInventory.LogicalProcessors,
                AgentInventory.MemoryBytes,
                _acceptJobs,
                _maxLogicalProcessors,
                _maxMemoryBytes,
                _lastHeartbeatAtUtc,
                _lastHeartbeatStatus,
                _sessionStartedAtUtc,
                _jobsCompleted,
                _jobsFailed,
                _verifiedCpMilli,
                _workerCpuMilliseconds,
                _peakWorkerMemoryBytes,
                liveMemory,
                Math.Round(liveCpuPercent, 1),
                _identity,
                [.. _activeJobs.Values.OrderBy(x => x.StartedAtUtc)],
                [.. _log],
                [.. _utilization],
                _pendingConfirmationKey);
        }
    }

    internal void ResetForTests()
    {
        lock (_gate)
        {
            _log.Clear();
            _activeJobs.Clear();
            _workers.Clear();
            _utilization.Clear();
            _phase = "SEM_VINCULO";
            _phaseDetail = null;
            _nodeId = null;
            _baseUrl = null;
            _acceptJobs = true;
            _maxLogicalProcessors = 1;
            _maxMemoryBytes = 0;
            _lastHeartbeatAtUtc = null;
            _lastHeartbeatStatus = null;
            _sessionStartedAtUtc = DateTimeOffset.UtcNow;
            _jobsCompleted = 0;
            _jobsFailed = 0;
            _verifiedCpMilli = 0;
            _workerCpuMilliseconds = 0;
            _peakWorkerMemoryBytes = 0;
            _identity = null;
            _pendingConfirmationKey = null;
        }
    }
}
