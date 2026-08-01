using GatewayPulse.VictronMonitor;
using GatewayPulse.VictronMonitor.CommandLine;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    var options = MonitorOptions.Parse(args);
    return await MonitorApplication.RunAsync(options, shutdown.Token);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Argument error: {ex.Message}");
    Console.Error.WriteLine("Run GatewayPulse.VictronMonitor.exe --help for usage.");
    return 2;
}
