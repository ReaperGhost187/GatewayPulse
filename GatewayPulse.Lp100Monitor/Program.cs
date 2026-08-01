using GatewayPulse.Lp100Monitor;
using GatewayPulse.Lp100Monitor.CommandLine;

var options = MonitorOptions.Parse(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return await MonitorApplication.RunAsync(options, cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
