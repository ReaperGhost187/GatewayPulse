using System.Collections.Concurrent;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Tracks queued → delivered/failed outcomes for mobile alerts without coupling monitors
/// to Apple network I/O. Keys are caller-supplied (e.g. RF condition dedupe keys).
/// </summary>
public sealed class MobilePushDeliveryTracker
{
    public enum DeliveryStatus
    {
        None = 0,
        Pending = 1,
        Delivered = 2,
        Failed = 3
    }

    private readonly ConcurrentDictionary<string, DeliveryStatus> _states =
        new(StringComparer.Ordinal);

    public void MarkPending(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        _states[key] = DeliveryStatus.Pending;
    }

    public void Report(string key, bool anySuccess)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        _states[key] = anySuccess ? DeliveryStatus.Delivered : DeliveryStatus.Failed;
    }

    public DeliveryStatus GetStatus(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return DeliveryStatus.None;
        return _states.TryGetValue(key, out var status) ? status : DeliveryStatus.None;
    }

    public bool WasDelivered(string key) => GetStatus(key) == DeliveryStatus.Delivered;

    public bool IsPending(string key) => GetStatus(key) == DeliveryStatus.Pending;

    public bool HasFailed(string key) => GetStatus(key) == DeliveryStatus.Failed;

    public void Clear(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        _states.TryRemove(key, out _);
    }
}
