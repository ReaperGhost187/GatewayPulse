using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace GatewayPulse.ServiceHosting;

public static class Lp100MonitorServiceCollectionExtensions
{
    public static IServiceCollection AddLp100MonitorSupervision(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<Lp100MonitorOptions>(configuration.GetSection("Lp100Monitor"));
        services.Configure<RfMonitoringOptions>(configuration.GetSection("RfMonitoring"));
        services.Configure<DashboardOptions>(configuration.GetSection("Dashboard"));
        services.TryAddSingleton<ILp100MonitorProcessLauncher, Lp100MonitorProcessLauncher>();
        services.AddSingleton<RadioCatFrequencyCache>();
        services.AddSingleton<IcomCivSerialFrequencyClient>();
        services.AddSingleton<RadioCatFrequencyClient>();
        services.AddSingleton<FrequencySnapshotProvider>();
        services.AddHostedService<RadioCatFrequencyPoller>();
        services.AddSingleton<RfTransmissionMonitor>();
        services.AddHostedService(provider => provider.GetRequiredService<RfTransmissionMonitor>());
        services.AddHostedService<Lp100MonitorSupervisor>();
        services.AddHostedService<RfAlertMonitor>();
        return services;
    }
}

public interface ILp100MonitorProcess : IAsyncDisposable
{
    int ExitCode { get; }
    bool HasExited { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

public interface ILp100MonitorProcessLauncher
{
    ILp100MonitorProcess Start(ProcessStartInfo startInfo);
}

public sealed class Lp100MonitorProcessLauncher : ILp100MonitorProcessLauncher
{
    public ILp100MonitorProcess Start(ProcessStartInfo startInfo)
    {
        var job = KillOnCloseJob.Create();
        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the LP-100A monitor process.");
            job.Assign(process);
            return new Lp100MonitorProcess(process, job);
        }
        catch
        {
            try
            {
                if (process is not null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            process?.Dispose();
            job.Dispose();
            throw;
        }
    }

    private sealed class Lp100MonitorProcess(Process process, KillOnCloseJob job) : ILp100MonitorProcess
    {
        public int ExitCode => process.ExitCode;
        public bool HasExited => process.HasExited;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill()
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        public ValueTask DisposeAsync()
        {
            job.Dispose();
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class KillOnCloseJob(SafeFileHandle handle) : IDisposable
    {
        private const uint KillOnJobClose = 0x00002000;
        public static KillOnCloseJob Create()
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose }
            };
            if (!SetInformationJobObject(handle, 9, ref information, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            return new KillOnCloseJob(handle);
        }

        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose() => handle.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobObjectExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

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
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
    }
}

public sealed class Lp100MonitorSupervisor(
    IOptionsMonitor<Lp100MonitorOptions> options,
    IOptions<DashboardOptions> dashboardOptions,
    IHostEnvironment environment,
    ILp100MonitorProcessLauncher processLauncher,
    ILogger<Lp100MonitorSupervisor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var demoMode = dashboardOptions.Value.DemoMode;
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = options.CurrentValue;
            if (!current.Enabled && !demoMode)
            {
                logger.LogInformation("LP-100A monitor supervision is disabled.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            ProcessStartInfo startInfo;
            try
            {
                startInfo = Lp100MonitorLaunchSpec.Create(current, environment.ContentRootPath, demoMode);
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "LP-100A monitor configuration is invalid.");
                return;
            }

            ILp100MonitorProcess? process = null;
            try
            {
                if (!File.Exists(startInfo.FileName))
                    throw new FileNotFoundException("The configured LP-100A monitor executable is missing.", startInfo.FileName);

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current.OutputPath))!);
                Directory.CreateDirectory(Path.GetFullPath(current.LogsPath));

                process = processLauncher.Start(startInfo);
                logger.LogInformation(demoMode
                    ? "LP-100A monitor started in Demo Mode (mock telemetry)."
                    : "LP-100A monitor started (port={Port}, autoDetect={AutoDetect}).",
                    current.Port, current.AutoDetect);
                await process.WaitForExitAsync(stoppingToken);
                if (!stoppingToken.IsCancellationRequested)
                    logger.LogWarning("LP-100A monitor exited with code {ExitCode}; restarting.", process.ExitCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "LP-100A monitor failed; restarting.");
            }
            finally
            {
                if (process is not null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    await process.DisposeAsync();
                }
            }

            if (stoppingToken.IsCancellationRequested)
                break;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, current.RestartDelaySeconds)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
