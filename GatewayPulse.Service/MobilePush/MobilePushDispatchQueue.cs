using GatewayPulse.Core;
using Microsoft.Extensions.Options;

namespace GatewayPulse.ServiceHosting;

/// <summary>
/// Bounded fail-soft queue between alert monitors and APNs delivery.
/// Overflow policy: DropOldest — when full, the oldest pending alert is discarded
/// and marked Failed in the delivery tracker so producers never block on Apple I/O
/// and the queue never grows without bound. Retained items stay FIFO-ordered.
/// </summary>
public sealed class MobilePushDispatchQueue
{
    public const int DefaultCapacity = 64;

    private readonly IOptionsMonitor<ApplePushOptions> _options;
    private readonly MobilePushDeliveryTracker _tracker;
    private readonly ILogger<MobilePushDispatchQueue> _logger;
    private readonly object _gate = new();
    private readonly Queue<MobileAlertEvent> _items = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _completed;
    private long _overflowDrops;

    public MobilePushDispatchQueue(
        IOptionsMonitor<ApplePushOptions> options,
        MobilePushDeliveryTracker tracker,
        ILogger<MobilePushDispatchQueue> logger)
    {
        _options = options;
        _tracker = tracker;
        _logger = logger;
    }

    public long OverflowDrops => Interlocked.Read(ref _overflowDrops);

    public int Capacity => ResolveCapacity(_options.CurrentValue);

    public int Count
    {
        get
        {
            lock (_gate) return _items.Count;
        }
    }

    /// <summary>Enqueues quickly. Never waits on APNs. DropOldest on overflow.</summary>
    public MobilePublishAck Enqueue(MobileAlertEvent alertEvent)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);

        var capacity = ResolveCapacity(_options.CurrentValue);
        lock (_gate)
        {
            if (_completed)
            {
                if (!string.IsNullOrWhiteSpace(alertEvent.DedupeKey))
                    _tracker.Report(alertEvent.DedupeKey, anySuccess: false);
                return new MobilePublishAck { Accepted = false, Outcome = MobilePublishOutcomes.Dropped };
            }

            if (_items.Count >= capacity)
            {
                var dropped = _items.Dequeue();
                Interlocked.Increment(ref _overflowDrops);
                if (!string.IsNullOrWhiteSpace(dropped.DedupeKey))
                    _tracker.Report(dropped.DedupeKey, anySuccess: false);
                _logger.LogWarning(
                    "Mobile push queue overflow (drop-oldest) droppedType={AlertType} category=queue_overflow",
                    dropped.Type);
                // Semaphore count unchanged: one pending slot replaced another.
            }
            else
            {
                _signal.Release();
            }

            if (!string.IsNullOrWhiteSpace(alertEvent.DedupeKey))
                _tracker.MarkPending(alertEvent.DedupeKey);

            _items.Enqueue(alertEvent);
            return new MobilePublishAck { Accepted = true, Outcome = MobilePublishOutcomes.Queued };
        }
    }

    /// <summary>Waits for the next event or cancellation. Returns null when completed and drained.</summary>
    public async Task<MobileAlertEvent?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (_items.Count > 0)
                    return _items.Dequeue();

                if (_completed)
                    return null;

                // Spurious wake / race — continue waiting.
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            // Wake any waiter so it can observe completion once drained.
            _signal.Release();
        }
    }

    private static int ResolveCapacity(ApplePushOptions options)
    {
        var capacity = options.DispatchQueueCapacity > 0
            ? options.DispatchQueueCapacity
            : DefaultCapacity;
        return Math.Clamp(capacity, 8, 512);
    }
}
