using System.Diagnostics;

namespace ArtistFeed.Core;

/// <summary>
/// One shared pace for every request the app makes to Bandcamp: together they never ask faster
/// than one request per Gap seconds (1 by default; ARTIST_FEED_BANDCAMP_GAP overrides it). When Bandcamp answers 429 ("slow down"), everything pauses
/// for the time it asks, and the gap widens for a while afterwards.
/// Work you are waiting on is "foreground"; the regular feed refresh is "background" and stands
/// aside completely while foreground work runs or a pause is in force.
/// </summary>
public sealed class Pace
{
    public double Gap { get; }
    public double MaxGap { get; }
    public double Backoff { get; }

    readonly SemaphoreSlim _lock = new(1, 1);
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _next;
    double _gap;
    DateTime _pausedUntil = DateTime.MinValue;
    int _foreground;

    public Pace(double gap = 1, double maxGap = 8, double backoff = 30)
    {
        Gap = gap;
        MaxGap = maxGap;
        Backoff = backoff;
        _gap = gap;
    }

    /// <summary>Background work should stand aside for now and try again later.</summary>
    public sealed class BusyException() : Exception("Bandcamp is busy or has asked for a pause.");

    double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>using (pace.Foreground()) around work the user is waiting on.</summary>
    public IDisposable Foreground()
    {
        Interlocked.Increment(ref _foreground);
        return new Release(() => Interlocked.Decrement(ref _foreground));
    }

    sealed class Release(Action done) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) done();
        }
    }

    /// <summary>True when background work should not ask Bandcamp for anything.</summary>
    public bool OnHold => Volatile.Read(ref _foreground) > 0 || PausedSeconds > 0;

    public int PausedSeconds => Math.Max(0, (int)(_pausedUntil - DateTime.UtcNow).TotalSeconds);

    /// <summary>Take the next slot in the queue. Background callers get BusyException instead of waiting
    /// when someone is waiting on foreground work or Bandcamp has asked for a pause.</summary>
    public async Task WaitAsync(bool background, CancellationToken ct = default)
    {
        if (background && OnHold) throw new BusyException();
        await _lock.WaitAsync(ct);
        try
        {
            while (true)                                    // a pause may be extended while we sleep
            {
                var delay = _next - Now;
                if (delay <= 0) break;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(delay, 5)), ct);
            }
            _next = Now + _gap;
            _gap = Math.Max(Gap, _gap * 0.97);              // drift back to normal while things go well
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Bandcamp said 429: nobody asks again for `seconds`, and the gap widens.</summary>
    public void SlowDown(double seconds)
    {
        _next = Math.Max(_next, Now + seconds);
        var until = DateTime.UtcNow.AddSeconds(seconds);
        if (until > _pausedUntil) _pausedUntil = until;
        _gap = Gap > 0 ? Math.Min(MaxGap, Math.Max(_gap, Gap) * 1.6) : 0;
    }

    /// <summary>How long to stay quiet after a 429: what Bandcamp asks for, else a doubling wait.</summary>
    public double PauseFor(HttpResponseMessage response, int attempt)
    {
        double asked = 0;
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } d) asked = d.TotalSeconds;
            else if (ra.Date is { } when) asked = Math.Max(0, (when - DateTimeOffset.UtcNow).TotalSeconds);
        }
        return Math.Min(600, Math.Max(asked, Backoff * Math.Pow(2, attempt)));
    }
}
