using System.IO.Ports;
using GatewayPulse.Core;
using GatewayPulse.RfMonitoring;
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
        var portName = SerialPortName.Normalize(cat.PortName);
        if (string.IsNullOrWhiteSpace(portName))
            return Task.FromResult<(decimal?, string)>((null, "CI-V COM port not set"));

        // SerialPort.Open/Read are synchronous and can stall on bad drivers / missing ports
        // under a service account. Always hop off the caller so hosted-service StartAsync
        // and request threads cannot block. Open itself is also hard-timeout capped.
        return Task.Run(
            () => ReadFrequency(portName, baud, timeout, radioAddress, cancellationToken),
            CancellationToken.None);
    }

    private static (decimal? FrequencyKhz, string Status) ReadFrequency(
        string portName,
        int baud,
        int timeout,
        byte radioAddress,
        CancellationToken cancellationToken)
    {
        SerialPort? port = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = timeout,
                WriteTimeout = timeout,
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true
            };

            var openError = OpenWithTimeout(port, timeout, cancellationToken);
            if (openError is not null)
                return (null, openError);

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
        finally
        {
            TryDisposePort(port);
        }
    }

    /// <summary>
    /// <see cref="SerialPort.Open"/> can hang indefinitely on some USB-serial stacks.
    /// Cap wait time so the poller can report failure and keep the host healthy.
    /// </summary>
    private static string? OpenWithTimeout(SerialPort port, int timeoutMs, CancellationToken cancellationToken)
    {
        Exception? openEx = null;
        var opened = false;
        var thread = new Thread(() =>
        {
            try
            {
                port.Open();
                opened = true;
            }
            catch (Exception ex)
            {
                openEx = ex;
            }
        })
        {
            IsBackground = true,
            Name = "GatewayPulse-CI-V-Open"
        };

        thread.Start();
        var remaining = Math.Max(100, timeoutMs);
        while (remaining > 0)
        {
            if (cancellationToken.IsCancellationRequested)
                return "CI-V cancelled";

            if (thread.Join(50))
                break;

            remaining -= 50;
        }

        if (thread.IsAlive)
            return $"CI-V open timeout on {port.PortName}";

        if (openEx is not null)
            return "CI-V error: " + openEx.GetType().Name;

        if (!opened || !port.IsOpen)
            return $"CI-V open failed on {port.PortName}";

        return null;
    }

    private static void TryDisposePort(SerialPort? port)
    {
        if (port is null)
            return;

        try
        {
            if (port.IsOpen)
                port.Close();
        }
        catch
        {
            // ignore
        }

        try
        {
            port.Dispose();
        }
        catch
        {
            // ignore — Open may still be wedged on a background thread
        }
    }
}
