namespace GatewayPulse.VictronMonitor.Logging;

public sealed class MonitorLogger
{
    private readonly string _logFile;
    private readonly object _sync = new();
    private bool _writeFailureReported;

    public MonitorLogger(string logsPath)
    {
        Directory.CreateDirectory(logsPath);
        _logFile = Path.Combine(logsPath, $"victron-monitor-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    public string LogFile => _logFile;

    public void Info(string message) => Write("INFO", message);
    public void Warning(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var sanitizedMessage = string.Concat(message.Select(character =>
            char.IsControl(character) ? $"\\u{(int)character:X4}" : character.ToString()));
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {sanitizedMessage}";
        lock (_sync)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                ReportWriteFailure(ex);
            }

            try
            {
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                ReportWriteFailure(ex);
            }
        }
    }

    private void ReportWriteFailure(Exception exception)
    {
        if (_writeFailureReported)
            return;

        _writeFailureReported = true;
        try
        {
            Console.Error.WriteLine($"GatewayPulse.VictronMonitor logging disabled: {exception.Message}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
