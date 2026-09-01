using System.Diagnostics;

namespace LastWarExtractor.Services;

/// <summary>
/// Thread-safe request-start limiter. Default 28 RPM keeps headroom under a 30 RPM cap.
/// Each retry also consumes a slot. Ported from RequestRateLimiter in extractor.py.
/// </summary>
public sealed class RequestRateLimiter
{
    private readonly double _minIntervalSeconds;
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastStart = double.NegativeInfinity;

    public RequestRateLimiter(int requestsPerMinute = 28)
    {
        if (requestsPerMinute is < 1 or > 30)
            throw new ArgumentException("requestsPerMinute must be between 1 and 30");
        RequestsPerMinute = requestsPerMinute;
        _minIntervalSeconds = 60.0 / requestsPerMinute;
    }

    public int RequestsPerMinute { get; }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        double delay;
        lock (_lock)
        {
            double now = _clock.Elapsed.TotalSeconds;
            delay = Math.Max(0.0, _minIntervalSeconds - (now - _lastStart));
            // Reserve this slot now so concurrent callers serialize correctly.
            _lastStart = now + delay;
        }
        if (delay > 0)
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
    }
}
