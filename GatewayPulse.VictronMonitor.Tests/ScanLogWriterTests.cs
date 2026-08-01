using GatewayPulse.VictronMonitor.Logging;

namespace GatewayPulse.VictronMonitor.Tests;

public sealed class ScanLogWriterTests
{
    [Fact]
    public void ConcurrentWriters_UseDistinctLogFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gateway-pulse-scan-log-{Guid.NewGuid():N}");
        try
        {
            using var first = new ScanLogWriter(directory);
            using var second = new ScanLogWriter(directory);

            Assert.NotEqual(first.Path, second.Path);
            Assert.True(File.Exists(first.Path));
            Assert.True(File.Exists(second.Path));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
