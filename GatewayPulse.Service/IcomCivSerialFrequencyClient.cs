using System.IO.Ports;
using GatewayPulse.Core;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Read-only Icom CI-V frequency poll over a dedicated COM port (CT-17 / USB CI-V).
/// Never sends VFO/mode/PTT commands.
/// </summary>
public sealed class IcomCivSerialFrequencyClient(IOptionsMonitor<GatewayPulseOptions> options)
{
    public Task<(decimal? FrequencyKhz, string Status)> TryGetFrequencyAsync(CancellationToken cancellationToken)
    {
        var cat = options.CurrentValue.RadioCat ?? new RadioCatOptions();
        if (!cat.Enabled)
            return Task.FromResult<(decimal?, string)>((null, "Disabled"));

        if (string.IsNullOrWhiteSpace(cat.PortName))
            return Task.FromResult<(decimal?, string)>((null, "CI-V COM port not set"));

        if (!IcomCivFrequencyCodec.TryParseAddress(cat.CivAddress, out var radioAddress))
            return Task.FromResult<(decimal?, string)>((null, "Invalid CI-V address"));

        var baud = cat.BaudRate > 0 ? cat.BaudRate : 19200;
        var timeout = Math.Clamp(cat.TimeoutMs, 100, 2000);
        var portName = cat.PortName.Trim().ToUpperInvariant();

        // SerialPort.Open/Read are synchronous and can stall on bad drivers.
        // Always hop to the thread pool so BackgroundService.StartAsync / Kestrel
        // startup cannot be blocked before the first await.
        return Task.Run(
            () => ReadFrequency(portName, baud, timeout, radioAddress, cancellationToken),
            cancellationToken);
    }

    private static (decimal? FrequencyKhz, string Status) ReadFrequency(
        string portName,
        int baud,
        int timeout,
        byte radioAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = timeout,
                WriteTimeout = timeout,
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true
            };
            port.Open();
            // Drain stale bytes.
            try { port.DiscardInBuffer(); } catch { /* ignore */ }

            var request = IcomCivFrequencyCodec.BuildReadFrequencyRequest(radioAddress);
            port.Write(request, 0, request.Length);

            var buffer = new byte[64];
            var total = 0;
            var deadline = Environment.TickCount64 + timeout;
            while (Environment.TickCount64 < deadline && total < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (port.BytesToRead <= 0)
                    {
                        Thread.Sleep(15);
                        continue;
                    }

                    var n = port.Read(buffer, total, buffer.Length - total);
                    if (n <= 0)
                        continue;
                    total += n;
                    if (IcomCivFrequencyCodec.TryDecodeFrequencyHz(buffer.AsSpan(0, total), radioAddress, out var hz))
                    {
                        var khz = hz / 1000m;
                        return (khz, $"OK {portName} @ {baud}");
                    }
                }
                catch (TimeoutException)
                {
                    break;
                }
            }

            return (null, $"No CI-V frequency reply on {portName}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "CI-V error: " + ex.GetType().Name);
        }
    }
}
