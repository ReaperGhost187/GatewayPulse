using System.Globalization;
using System.Text;

namespace GatewayPulse.Lp100Monitor.Logging;

/// <summary>
/// Bounded timestamped capture of raw LP-100A 'P' response bodies for PACTOR session analysis.
/// Writes under the monitor logs directory (typically C:\PWM\logs).
/// </summary>
public sealed class RawCaptureWriter : IDisposable
{
    public const long DefaultMaxBytes = 8 * 1024 * 1024; // 8 MiB

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private long _bytesWritten;
    private bool _disposed;

    public RawCaptureWriter(string logsPath, long maxBytes = DefaultMaxBytes)
    {
        Directory.CreateDirectory(logsPath);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        _path = Path.Combine(logsPath, $"lp100-raw-capture_{stamp}.log");
        _maxBytes = Math.Max(64 * 1024, maxBytes);
    }

    public string FilePath => _path;
    public bool IsFull { get; private set; }

    public void Write(string? rawBody, decimal? forwardWatts = null, decimal? swr = null)
    {
        if (_disposed || IsFull || string.IsNullOrWhiteSpace(rawBody))
            return;

        lock (_gate)
        {
            if (_disposed || IsFull)
                return;

            _writer ??= new StreamWriter(new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read))
            {
                AutoFlush = true
            };

            if (_bytesWritten == 0)
            {
                var header = "# Gateway Pulse LP-100A raw P capture (display snapshots; Peak Hold recommended for PACTOR)" +
                             Environment.NewLine +
                             "# utc_iso\traw_body\tforward_w\tswr" + Environment.NewLine;
                _writer.Write(header);
                _bytesWritten += Encoding.UTF8.GetByteCount(header);
            }

            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O}\t{rawBody}\t{forwardWatts?.ToString(CultureInfo.InvariantCulture) ?? ""}\t{swr?.ToString(CultureInfo.InvariantCulture) ?? ""}{Environment.NewLine}");
            var bytes = Encoding.UTF8.GetByteCount(line);
            if (_bytesWritten + bytes > _maxBytes)
            {
                _writer.WriteLine("# CAPTURE FULL — further frames discarded");
                IsFull = true;
                return;
            }

            _writer.Write(line);
            _bytesWritten += bytes;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
