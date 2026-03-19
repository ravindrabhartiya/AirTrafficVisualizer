namespace ATC.TelemetryProducer;

using System.Net;

/// <summary>
/// Intercepts 429 Too Many Requests responses from the OpenSky API.
/// Reads the Retry-After header and delays the next request accordingly.
/// </summary>
public sealed class RetryAfterHandler : DelegatingHandler
{
    private readonly ILogger<RetryAfterHandler> _logger;

    /// <summary>
    /// Shared gate that the Worker loop can await. Set when a 429 forces a cooldown.
    /// </summary>
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static DateTimeOffset _retryAfterUntil = DateTimeOffset.MinValue;

    public RetryAfterHandler(ILogger<RetryAfterHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // If a previous 429 set a cooldown, wait it out before sending
        var remainingDelay = _retryAfterUntil - DateTimeOffset.UtcNow;
        if (remainingDelay > TimeSpan.Zero)
        {
            _logger.LogWarning("Waiting {Seconds:F0}s for previous Retry-After cooldown", remainingDelay.TotalSeconds);
            await Task.Delay(remainingDelay, cancellationToken);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = ParseRetryAfter(response);
            _retryAfterUntil = DateTimeOffset.UtcNow + retryAfter;

            _logger.LogWarning(
                "Received 429 Too Many Requests. Retry-After: {Seconds}s. Sleeping before retry.",
                retryAfter.TotalSeconds);

            await Task.Delay(retryAfter, cancellationToken);

            // Retry once after the cooldown
            response = await base.SendAsync(request, cancellationToken);
        }

        return response;
    }

    private static TimeSpan ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is not null)
        {
            // Retry-After as delta-seconds
            if (header.Delta.HasValue)
                return header.Delta.Value;

            // Retry-After as HTTP-date
            if (header.Date.HasValue)
            {
                var delay = header.Date.Value - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(30);
            }
        }

        // Default fallback if no valid Retry-After header
        return TimeSpan.FromSeconds(60);
    }
}
