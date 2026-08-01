namespace GatewayPulse.Lp100Monitor.Logging;

public sealed class MonitorLogger
{
    private readonly string _directory;
    private readonly object _gate = new();
    private DateTime _lastFlood = DateTime.MinValue;
    private string? _lastMessage;

    public MonitorLogger(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public void Info(string message) => Write("INFO", message, throttle: false);
    public void Warn(string message) => Write("WARN", message, throttle: true);
    public void Error(string message) => Write("ERROR", message, throttle: true);

    private void Write(string level, string message, bool throttle)
    {
        lock (_gate)
        {
            if (throttle &&
                string.Equals(_lastMessage, message, StringComparison.Ordinal) &&
                DateTime.UtcNow - _lastFlood < TimeSpan.FromSeconds(30))
            {
                return;
            }

            _lastMessage = message;
            _lastFlood = DateTime.UtcNow;
            var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}";
            var path = Path.Combine(_directory, $"lp100-{DateTime.UtcNow:yyyyMMdd}.log");
            File.AppendAllText(path, line + Environment.NewLine);
            Console.Error.WriteLine(line);
        }
    }
}
