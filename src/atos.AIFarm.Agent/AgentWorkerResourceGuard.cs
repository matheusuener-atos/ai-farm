using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace atos.AIFarm.Agent;

internal sealed record AgentWorkerTelemetry(
    long PeakMemoryBytes,
    long ProcessorTimeMilliseconds,
    string Enforcement);

internal sealed class AgentWorkerResourceLimitException(string message)
    : InvalidOperationException(message);

internal sealed class AgentWorkerResourceGuard : IDisposable
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly Process _process;
    private readonly long _maximumMemoryBytes;
    private IntPtr _jobHandle;
    private long _peakMemoryBytes;
    private bool _memoryExceeded;
    private bool _hardMemoryLimit;
    private bool _cpuAffinity;

    private AgentWorkerResourceGuard(
        Process process,
        int maximumLogicalProcessors,
        long maximumMemoryBytes)
    {
        _process = process;
        _maximumMemoryBytes = maximumMemoryBytes;
        _cpuAffinity = TryApplyCpuAffinity(process, maximumLogicalProcessors);
        _hardMemoryLimit = TryAttachWindowsJob(process, maximumMemoryBytes, out _jobHandle);
    }

    public static AgentWorkerResourceGuard Attach(
        Process process,
        int maximumLogicalProcessors,
        long maximumMemoryBytes)
    {
        if (maximumLogicalProcessors <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumLogicalProcessors));
        if (maximumMemoryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMemoryBytes));
        return new AgentWorkerResourceGuard(
            process,
            maximumLogicalProcessors,
            maximumMemoryBytes);
    }

    public void Sample()
    {
        if (_process.HasExited) return;
        try
        {
            _process.Refresh();
            _peakMemoryBytes = Math.Max(
                _peakMemoryBytes,
                Math.Max(_process.WorkingSet64, _process.PeakWorkingSet64));
            if (!_hardMemoryLimit && _process.WorkingSet64 > _maximumMemoryBytes)
            {
                _memoryExceeded = true;
                Kill(_process);
            }
        }
        catch (InvalidOperationException) when (_process.HasExited)
        {
        }
    }

    public AgentWorkerTelemetry Complete()
    {
        try
        {
            _process.Refresh();
            _peakMemoryBytes = Math.Max(_peakMemoryBytes, _process.PeakWorkingSet64);
        }
        catch (InvalidOperationException) when (_process.HasExited)
        {
        }
        if (_memoryExceeded ||
            (!_hardMemoryLimit && _peakMemoryBytes > _maximumMemoryBytes))
            throw new AgentWorkerResourceLimitException(
                $"O worker excedeu o limite de memória de {_maximumMemoryBytes} bytes.");

        long processorTimeMilliseconds;
        try
        {
            processorTimeMilliseconds = Math.Max(
                1,
                checked((long)_process.TotalProcessorTime.TotalMilliseconds));
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            OverflowException or
            SystemException)
        {
            processorTimeMilliseconds = 1;
        }
        var enforcement = _hardMemoryLimit
            ? _cpuAffinity ? "WINDOWS_JOB_OBJECT+CPU_AFFINITY" : "WINDOWS_JOB_OBJECT"
            : _cpuAffinity ? "PROCESS_MONITOR+CPU_AFFINITY" : "PROCESS_MONITOR";
        return new AgentWorkerTelemetry(
            Math.Max(1, _peakMemoryBytes),
            processorTimeMilliseconds,
            enforcement);
    }

    public void Dispose()
    {
        if (_jobHandle == IntPtr.Zero) return;
        CloseHandle(_jobHandle);
        _jobHandle = IntPtr.Zero;
    }

    private static bool TryApplyCpuAffinity(Process process, int maximumLogicalProcessors)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return false;
        try
        {
            using var current = Process.GetCurrentProcess();
            var allowed = unchecked((ulong)current.ProcessorAffinity.ToInt64());
            ulong selected = 0;
            var remaining = maximumLogicalProcessors;
            for (var bit = 0; bit < IntPtr.Size * 8 && remaining > 0; bit++)
            {
                var candidate = 1UL << bit;
                if ((allowed & candidate) == 0) continue;
                selected |= candidate;
                remaining--;
            }
            if (selected == 0) return false;
            process.ProcessorAffinity = unchecked((IntPtr)(long)selected);
            return true;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            NotSupportedException or
            PlatformNotSupportedException or
            Win32Exception)
        {
            return false;
        }
    }

    private static bool TryAttachWindowsJob(
        Process process,
        long maximumMemoryBytes,
        out IntPtr jobHandle)
    {
        jobHandle = IntPtr.Zero;
        if (!OperatingSystem.IsWindows()) return false;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return false;
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitProcessMemory | JobObjectLimitKillOnJobClose
            },
            ProcessMemoryLimit = checked((UIntPtr)(ulong)maximumMemoryBytes)
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformationClass,
                    pointer,
                    (uint)length) ||
                !AssignProcessToJobObject(handle, process.Handle))
            {
                CloseHandle(handle);
                return false;
            }
            jobHandle = handle;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void Kill(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        uint informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
