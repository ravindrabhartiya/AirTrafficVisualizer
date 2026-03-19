namespace ATC.TelemetryProducer;

using System.Text.Json;
using System.Threading.RateLimiting;
using ATC.Shared;
using Confluent.Kafka;
using StackExchange.Redis;

public sealed class Worker : BackgroundService
{
    private const string RedisLastCallKey = "opensky:last_api_call";

    private readonly IProducer<string, string> _producer;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly ILogger<Worker> _logger;
    private readonly bool _useMock;
    private readonly MockFlightGenerator _mockGenerator = new();

    public Worker(
        IProducer<string, string> producer,
        IHttpClientFactory httpClientFactory,
        IConnectionMultiplexer redis,
        TokenBucketRateLimiter rateLimiter,
        IConfiguration configuration,
        ILogger<Worker> logger)
    {
        _producer = producer;
        _httpClientFactory = httpClientFactory;
        _redis = redis;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _useMock = configuration.GetValue("UseMockData", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telemetry Producer started. Mode: {Mode}", _useMock ? "MOCK" : "OpenSky API");

        // On startup (API mode): check Redis for the last call timestamp to avoid double-dipping
        if (!_useMock)
        {
            await EnforceStartupCooldownAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<FlightTelemetry> flights;

                if (_useMock)
                {
                    flights = _mockGenerator.GenerateFrame();
                }
                else
                {
                    // Acquire a rate-limiter token (blocks until one is available)
                    using var lease = await _rateLimiter.AcquireAsync(1, stoppingToken);
                    if (!lease.IsAcquired)
                    {
                        _logger.LogWarning("Rate limiter denied request, retrying next cycle");
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }

                    flights = await FetchFromOpenSkyAsync(stoppingToken);

                    // Record the call timestamp in Redis
                    var db = _redis.GetDatabase();
                    await db.StringSetAsync(
                        RedisLastCallKey,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                        TimeSpan.FromHours(1));
                }

                int produced = 0;
                foreach (var flight in flights)
                {
                    if (flight.Latitude is null || flight.Longitude is null) continue;

                    var json = JsonSerializer.Serialize(flight);
                    await _producer.ProduceAsync(
                        Constants.KafkaTopic,
                        new Message<string, string> { Key = flight.Icao24, Value = json },
                        stoppingToken);

                    produced++;
                }

                _logger.LogInformation("Produced {Count} flight telemetry messages", produced);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error producing telemetry");
            }

            // Mock mode runs at 5 s cadence; API mode cadence is governed by the rate limiter
            var delay = _useMock ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1);
            await Task.Delay(delay, stoppingToken);
        }

        _producer.Flush(TimeSpan.FromSeconds(5));
        _logger.LogInformation("Telemetry Producer stopped");
    }

    /// <summary>
    /// Reads the last API call timestamp from Redis. If it was less than 22 s ago,
    /// sleeps for the remaining time so a freshly restarted producer doesn't waste credits.
    /// </summary>
    private async Task EnforceStartupCooldownAsync(CancellationToken ct)
    {
        try
        {
            var db = _redis.GetDatabase();
            var raw = await db.StringGetAsync(RedisLastCallKey);

            if (raw.HasValue && long.TryParse(raw, out long lastEpoch))
            {
                var lastCall = DateTimeOffset.FromUnixTimeSeconds(lastEpoch);
                var elapsed = DateTimeOffset.UtcNow - lastCall;
                var minInterval = TimeSpan.FromSeconds(22);

                if (elapsed < minInterval)
                {
                    var wait = minInterval - elapsed;
                    _logger.LogInformation(
                        "Last OpenSky call was {Elapsed:F0}s ago (at {Time}). " +
                        "Waiting {Wait:F0}s to honour rate limit.",
                        elapsed.TotalSeconds, lastCall, wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                }
                else
                {
                    _logger.LogInformation(
                        "Last OpenSky call was {Elapsed:F0}s ago — safe to proceed.",
                        elapsed.TotalSeconds);
                }
            }
            else
            {
                _logger.LogInformation("No previous API call timestamp found in Redis.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read last API call from Redis; proceeding without cooldown");
        }
    }

    private async Task<IReadOnlyList<FlightTelemetry>> FetchFromOpenSkyAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("OpenSky");
        var response = await client.GetAsync("/api/states/all", ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<FlightTelemetry>();
        if (!doc.RootElement.TryGetProperty("states", out var states)) return results;

        foreach (var state in states.EnumerateArray())
        {
            if (state.GetArrayLength() < 17) continue;

            var flight = new FlightTelemetry
            {
                Icao24 = state[0].GetString() ?? "",
                Callsign = state[1].GetString()?.Trim() ?? "",
                OriginCountry = state[2].GetString() ?? "",
                Longitude = state[5].ValueKind == JsonValueKind.Number ? state[5].GetDouble() : null,
                Latitude = state[6].ValueKind == JsonValueKind.Number ? state[6].GetDouble() : null,
                BaroAltitude = state[7].ValueKind == JsonValueKind.Number ? state[7].GetDouble() : null,
                OnGround = state[8].GetBoolean(),
                Velocity = state[9].ValueKind == JsonValueKind.Number ? state[9].GetDouble() : null,
                TrueTrack = state[10].ValueKind == JsonValueKind.Number ? state[10].GetDouble() : null,
                VerticalRate = state[11].ValueKind == JsonValueKind.Number ? state[11].GetDouble() : null,
                LastUpdate = state[4].ValueKind == JsonValueKind.Number ? state[4].GetInt64() : 0
            };

            results.Add(flight);
        }

        _logger.LogInformation("Fetched {Count} flights from OpenSky Network", results.Count);
        return results;
    }
}
