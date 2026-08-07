namespace GatewayPulse.ServiceHosting;

/// <summary>Latest radio frequency observation from CI-V or rigctld.</summary>
public sealed class RadioCatFrequencyCache
{
    private readonly object _lock = new();
    private decimal? _frequencyKhz;
    private string _source = "Unknown";
    private DateTimeOffset? _updatedAt;
    private string _status = "Disabled";

    public void Set(decimal? frequencyKhz, string source, string status)
    {
        lock (_lock)
        {
            _frequencyKhz = frequencyKhz is > 0 ? frequencyKhz : null;
            _source = string.IsNullOrWhiteSpace(source) ? "Unknown" : source;
            _updatedAt = _frequencyKhz is > 0 ? DateTimeOffset.UtcNow : _updatedAt;
            _status = status;
        }
    }

    public void SetStatus(string status)
    {
        lock (_lock)
            _status = status;
    }

    public (decimal? FrequencyKhz, string Source, DateTimeOffset? UpdatedAt, string Status) Snapshot()
    {
        lock (_lock)
            return (_frequencyKhz, _source, _updatedAt, _status);
    }
}
