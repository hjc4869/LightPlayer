using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;

namespace LightStudio.RemoteServices;

/// <summary>
/// Bounds the load one WebDAV library places on its server: a few concurrent background reads and
/// directory listings, and paced request starts that slow down while the server reports overload.
/// </summary>
internal sealed class WebDavRequestThrottle
{
    private const int MaximumBackgroundReads = 3;
    private const int MaximumListings = 2;

    private const int MaximumOverloadRetries = 2;
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim backgroundReads = new(MaximumBackgroundReads, MaximumBackgroundReads);
    private readonly SemaphoreSlim listings = new(MaximumListings, MaximumListings);
    private readonly SemaphoreSlim turn = new(1, 1);
    private readonly object pacing = new();
    private TimeSpan interval = MinimumInterval;
    private long lastStart = Stopwatch.GetTimestamp();

    /// <summary>Waits for one of the slots shared by previews, artwork, and scan probes.</summary>
    public Task<IDisposable> EnterBackgroundReadAsync(CancellationToken cancellationToken) =>
        EnterAsync(backgroundReads, cancellationToken);

    public Task<IDisposable> EnterListingAsync(CancellationToken cancellationToken) =>
        EnterAsync(listings, cancellationToken);

    /// <summary>Creates a handler that paces its requests and retries the ones the server defers.</summary>
    public HttpMessageHandler CreatePacedHandler() => new PacedHandler(this);

    private static async Task<IDisposable> EnterAsync(SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Slot(slots);
    }

    /// <summary>Completes once the current spacing has passed since the previous request started.</summary>
    private async Task WaitForTurnAsync(CancellationToken cancellationToken)
    {
        // One caller waits out the spacing at a time, so a canceled caller gives its turn to the next.
        await turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                TimeSpan delay;
                lock (pacing)
                {
                    delay = interval - Stopwatch.GetElapsedTime(lastStart);
                    if (delay <= TimeSpan.Zero)
                    {
                        lastStart = Stopwatch.GetTimestamp();
                        return;
                    }
                }

                // Timers truncate to whole milliseconds; rounding up keeps the wait from spinning.
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(delay.TotalMilliseconds)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            turn.Release();
        }
    }

    private void ReportAccepted()
    {
        lock (pacing)
        {
            interval = TimeSpan.FromTicks(Math.Max(MinimumInterval.Ticks, interval.Ticks * 3 / 4));
        }
    }

    private void ReportOverload(HttpResponseMessage response)
    {
        TimeSpan requested = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => TimeSpan.Zero,
        };
        bool started;
        TimeSpan spacing;
        lock (pacing)
        {
            started = interval == MinimumInterval;
            interval = TimeSpan.FromTicks(Math.Clamp(
                Math.Max(interval.Ticks * 2, requested.Ticks),
                MinimumInterval.Ticks,
                MaximumInterval.Ticks));
            lastStart = Stopwatch.GetTimestamp();
            spacing = interval;
        }

        if (started)
        {
            AppLog.Warning(
                "WebDAV.Throttle",
                $"'{response.RequestMessage?.RequestUri?.Host}' answered {(int)response.StatusCode}; " +
                $"spacing requests {spacing.TotalMilliseconds:0} ms apart.");
        }
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? slots = slots;

        public void Dispose() => Interlocked.Exchange(ref slots, null)?.Release();
    }

    private sealed class PacedHandler(WebDavRequestThrottle throttle) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Bounded ranges continue a read in chunks, so only deferred ones wait for a turn.
            bool continues = request.Headers.Range is { } range && range.Ranges.All(item => item.To is not null);
            for (var retry = 0; ; retry++)
            {
                if (retry > 0 || !continues)
                {
                    await throttle.WaitForTurnAsync(cancellationToken).ConfigureAwait(false);
                }

                HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
                {
                    throttle.ReportAccepted();
                    return response;
                }

                throttle.ReportOverload(response);
                if (retry >= MaximumOverloadRetries)
                {
                    return response;
                }

                response.Dispose();
            }
        }
    }
}
