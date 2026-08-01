using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

public static class VictronMonitorServiceCollectionExtensions
{
    public static IServiceCollection AddVictronMonitorSupervision(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<VictronMonitorOptions>(configuration.GetSection("VictronMonitor"));
        services.Configure<DashboardOptions>(configuration.GetSection("Dashboard"));
        services.TryAddSingleton<IVictronMonitorProcessLauncher, VictronMonitorProcessLauncher>();
        services.AddHostedService<VictronMonitorSupervisor>();
        return services;
    }
}

public interface IVictronMonitorProcess : IAsyncDisposable
{
    int Id { get; }
    int ExitCode { get; }
    bool HasExited { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

public interface IVictronMonitorProcessLauncher
{
    IVictronMonitorProcess Start(ProcessStartInfo startInfo);
}

public sealed class VictronMonitorProcessLauncher : IVictronMonitorProcessLauncher
{
    public IVictronMonitorProcess Start(ProcessStartInfo startInfo)
    {
        var job = WindowsKillOnCloseJob.Create();
        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the Victron monitor process.");
            job.Assign(process);
            return new VictronMonitorProcess(process, job);
        }
        catch
        {
            try
            {
                if (process is not null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            process?.Dispose();
            job.Dispose();
            throw;
        }
    }

    private sealed class VictronMonitorProcess(
        Process process,
        WindowsKillOnCloseJob job) : IVictronMonitorProcess
    {
        public int Id => process.Id;
        public int ExitCode => process.ExitCode;
        public bool HasExited => process.HasExited;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            process.WaitForExitAsync(cancellationToken);

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

    private sealed class WindowsKillOnCloseJob(SafeFileHandle handle) : IDisposable
    {
        private const uint KillOnJobClose = 0x00002000;
        private const int ExtendedLimitInformation = 9;

        public static WindowsKillOnCloseJob Create()
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the Victron monitor process job.");

            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = KillOnJobClose
                }
            };
            if (!SetInformationJobObject(
                    handle,
                    ExtendedLimitInformation,
                    ref information,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Unable to configure the Victron monitor process job.");
            }
            return new WindowsKillOnCloseJob(handle);
        }

        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to supervise the Victron monitor process.");
        }

        public void Dispose() => handle.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
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
    }
}

public sealed class VictronMonitorSupervisor(
    IOptions<VictronMonitorOptions> options,
    IOptions<DashboardOptions> dashboardOptions,
    IHostEnvironment environment,
    IVictronMonitorProcessLauncher processLauncher,
    ILogger<VictronMonitorSupervisor> logger) : BackgroundService
{
    private readonly VictronMonitorOptions _options = options.Value;
    private readonly bool _demoMode = dashboardOptions.Value.DemoMode;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled && !_demoMode)
        {
            logger.LogInformation("Victron monitor supervision is disabled.");
            return;
        }

        if (_demoMode)
            logger.LogWarning("Dashboard Demo Mode is ON. The collector will emit mock telemetry (not for production).");

        ProcessStartInfo startInfo;
        try
        {
            startInfo = VictronMonitorLaunchSpec.Create(_options, environment.ContentRootPath, demoMode: _demoMode);
            if (_options.RestartDelaySeconds < 0)
                throw new InvalidOperationException("VictronMonitor:RestartDelaySeconds cannot be negative.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Victron monitor configuration is invalid; the collector will not start.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Yield();
            IVictronMonitorProcess? process = null;
            try
            {
                if (!File.Exists(startInfo.FileName))
                    throw new FileNotFoundException("The configured Victron monitor executable is missing.");
                if (!_demoMode && _options.Devices.Count == 0)
                {
                    if (!File.Exists(Path.GetFullPath(_options.KeyFile)))
                        throw new FileNotFoundException("The configured Victron advertisement key file is missing.");
                }
                else if (!_demoMode)
                {
                    var configurationPath = Path.IsPathRooted(_options.ConfigurationPath)
                        ? _options.ConfigurationPath
                        : Path.Combine(environment.ContentRootPath, _options.ConfigurationPath);
                    if (!File.Exists(Path.GetFullPath(configurationPath)))
                        throw new FileNotFoundException("The configured multi-device Victron configuration file is missing.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.OutputPath))!);
                Directory.CreateDirectory(Path.GetFullPath(_options.LogsPath));

                process = processLauncher.Start(startInfo);
                if (_demoMode)
                    logger.LogInformation("Victron monitor started in Demo Mode (mock telemetry).");
                else if (_options.Devices.Count > 0)
                    logger.LogInformation("Victron monitor started for {DeviceCount} configured power device(s).", _options.Devices.Count);
                else
                    logger.LogInformation("Victron monitor started for device {Address}.", _options.Address);
                await process.WaitForExitAsync(stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                    logger.LogWarning("Victron monitor exited with code {ExitCode}; it will be restarted.", process.ExitCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Victron monitor failed; it will be restarted.");
            }
            finally
            {
                if (process is not null)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Unable to terminate the Victron monitor process during shutdown.");
                    }
                    await process.DisposeAsync();
                }
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.RestartDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
