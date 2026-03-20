namespace ATC.TrackingEngine;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using ATC.Shared;
using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR.Client;
using StackExchange.Redis;

public sealed class Worker : BackgroundService
{
    private readonly ConsumerConfig _consumerConfig;
    private readonly IConnectionMultiplexer _redis;
    private readonly IFlightSnapshotStore _snapshotStore;
    private readonly SignalRConfig _signalRConfig;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<Worker> _logger;
    private DateTime _lastConsumeUtc = DateTime.MinValue;

    /// <summary>Queue of flight updates waiting to be flushed to SignalR.</summary>
    private readonly ConcurrentQueue<FlightPosition> _signalRQueue = new();

    /// <summary>
    /// Bounded channel that decouples SQLite writes from the consumer hot path.
    /// Consumers fire-and-forget into this channel; a background loop drains it.
    /// DropOldest ensures consumers never block even if SQLite can't keep up.
    /// </summary>
    private readonly Channel<FlightPosition> _sqliteChannel =
        Channel.CreateBounded<FlightPosition>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    /// <summary>Max age of a telemetry message before it is discarded.</summary>
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(2);

    /// <summary>Number of parallel consumer threads (one per Kafka partition max).</summary>
    private const int ConsumerCount = 3;

    /// <summary>Max items sent in a single SignalR batch message.</summary>
    internal const int MaxBatchSize = 200;

    /// <summary>Max items written to SQLite per batch.</summary>
    internal const int SqliteBatchSize = 500;

    public Worker(
        ConsumerConfig consumerConfig,
        IConnectionMultiplexer redis,
        IFlightSnapshotStore snapshotStore,
        SignalRConfig signalRConfig,
        ILoggerFactory loggerFactory,
        ILogger<Worker> logger)
    {
        _consumerConfig = consumerConfig;
        _redis = redis;
        _snapshotStore = snapshotStore;
        _signalRConfig = signalRConfig;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warm Redis from SQLite snapshot so dashboard has data immediately
        await WarmRedisCacheAsync();

        // Build SignalR connection with retry
        var hubConnection = new HubConnectionBuilder()
            .WithUrl(_signalRConfig.HubUrl)
            .WithAutomaticReconnect()
            .Build();

        await ConnectToSignalRAsync(hubConnection, stoppingToken);

        // Run stale geo-entry cleanup, SignalR batch flush, SQLite drain, and parallel Kafka consumers
        var cleanupTask = CleanupStaleGeoEntriesAsync(stoppingToken);
        var flushTask = FlushSignalRBatchesAsync(hubConnection, stoppingToken);
        var sqliteDrainTask = DrainSqliteAsync(stoppingToken);

        var consumeTasks = new Task[ConsumerCount];
        for (int i = 0; i < ConsumerCount; i++)
        {
            var id = i;
            consumeTasks[i] = ConsumeLoopAsync(id, stoppingToken);
        }
        _logger.LogInformation("Launched {Count} parallel Kafka consumers", ConsumerCount);

        await Task.WhenAll(consumeTasks.Append(cleanupTask).Append(flushTask).Append(sqliteDrainTask));
    }

    private async Task WarmRedisCacheAsync()
    {
        try
        {
            var flights = await _snapshotStore.LoadAllAsync(TimeSpan.FromMinutes(30));
            if (flights.Count == 0)
            {
                _logger.LogInformation("No SQLite snapshot data to warm Redis with");
                return;
            }

            var db = _redis.GetDatabase();
            foreach (var f in flights)
            {
                await db.GeoAddAsync(Constants.RedisGeoKey, f.Longitude, f.Latitude, f.Icao24);
                var hashKey = $"flight:{f.Icao24}";
                var entries = new HashEntry[]
                {
                    new("callsign", f.Callsign),
                    new("latitude", f.Latitude.ToString("F6")),
                    new("longitude", f.Longitude.ToString("F6")),
                    new("altitude", f.Altitude.ToString("F1")),
                    new("velocity", f.Velocity.ToString("F1")),
                    new("trueTrack", f.TrueTrack.ToString("F1")),
                    new("verticalRate", f.VerticalRate.ToString("F1")),
                    new("onGround", f.OnGround ? "1" : "0"),
                    new("originCountry", f.OriginCountry),
                    new("lastUpdate", f.LastUpdate.ToString())
                };
                await db.HashSetAsync(hashKey, entries);
                await db.KeyExpireAsync(hashKey, TimeSpan.FromMinutes(5));
            }

            _logger.LogInformation("Warmed Redis with {Count} flights from SQLite snapshot", flights.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to warm Redis from SQLite — starting cold");
        }
    }

    /// <summary>
    /// Periodically removes entries from the Redis geo set whose flight hash has expired,
    /// preventing phantom aircraft on the dashboard.
    /// </summary>
    private async Task CleanupStaleGeoEntriesAsync(CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var members = await db.SortedSetRangeByRankAsync(Constants.RedisGeoKey);
                int removed = 0;
                foreach (var member in members)
                {
                    var icao = member.ToString();
                    if (!await db.KeyExistsAsync($"flight:{icao}"))
                    {
                        await db.SortedSetRemoveAsync(Constants.RedisGeoKey, member);
                        removed++;
                    }
                }
                if (removed > 0)
                    _logger.LogInformation("Cleaned up {Count} stale geo entries", removed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during geo cleanup");
            }

            // Only purge SQLite if the consumer is actively receiving data,
            // otherwise we'd destroy the recovery snapshot during stalls.
            if (_lastConsumeUtc > DateTime.UtcNow.AddMinutes(-2))
            {
                try
                {
                    await _snapshotStore.PurgeStaleAsync(TimeSpan.FromHours(1));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during SQLite purge");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(60), ct);
        }
    }

    private async Task ConnectToSignalRAsync(HubConnection hub, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await hub.StartAsync(ct);
                _logger.LogInformation("Connected to SignalR hub at {Url}", _signalRConfig.HubUrl);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cannot connect to SignalR hub, retrying in 3s...");
                await Task.Delay(3000, ct);
            }
        }
    }

    /// <summary>
    /// Drains the SignalR queue every 500 ms and sends accumulated flight updates
    /// as a single batch message, reducing per-message SignalR overhead.
    /// </summary>
    private async Task FlushSignalRBatchesAsync(HubConnection hub, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (hub.State == HubConnectionState.Connected && !_signalRQueue.IsEmpty)
                {
                    var batch = new List<FlightPosition>(MaxBatchSize);
                    while (batch.Count < MaxBatchSize && _signalRQueue.TryDequeue(out var pos))
                        batch.Add(pos);

                    if (batch.Count > 0)
                    {
                        await hub.SendAsync("BroadcastFlightBatch", batch, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SignalR batch flush failed ({Count} items in queue)", _signalRQueue.Count);
            }

            await Task.Delay(500, ct);
        }
    }

    /// <summary>
    /// Background loop that drains the SQLite channel and batches upserts.
    /// Runs independently from consumers so SQLite I/O never slows down
    /// Kafka consumption or Redis writes.
    /// </summary>
    private async Task DrainSqliteAsync(CancellationToken ct)
    {
        var reader = _sqliteChannel.Reader;
        _logger.LogInformation("SQLite background drain started (batch size {BatchSize})", SqliteBatchSize);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for at least one item (avoids busy-spinning)
                await reader.WaitToReadAsync(ct);

                var batch = new List<FlightPosition>(SqliteBatchSize);
                while (batch.Count < SqliteBatchSize && reader.TryRead(out var pos))
                    batch.Add(pos);

                // De-duplicate: keep only the latest update per icao24
                var deduped = batch
                    .GroupBy(f => f.Icao24)
                    .Select(g => g.Last())
                    .ToList();

                foreach (var pos in deduped)
                {
                    try { await _snapshotStore.UpsertAsync(pos); }
                    catch (Exception ex) { _logger.LogWarning(ex, "SQLite upsert failed for {Icao}", pos.Icao24); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SQLite drain loop error");
                await Task.Delay(1000, ct);
            }
        }

        _logger.LogInformation("SQLite background drain stopped");
    }

    /// <summary>
    /// Runs a dedicated Kafka consumer. The blocking <c>Consume()</c> call is
    /// offloaded to a thread-pool thread so that the subsequent async Redis
    /// processing (<see cref="FlightProcessor.ProcessAsync"/>) is properly
    /// awaited instead of blocked with <c>.GetAwaiter().GetResult()</c>,
    /// preventing thread-pool starvation under sustained load.
    /// </summary>
    private async Task ConsumeLoopAsync(int consumerId, CancellationToken ct)
    {
        // Offload the blocking Kafka consumer to a dedicated thread so the
        // async Redis work that follows can be awaited without blocking.
        await Task.Run(async () =>
        {
            using var consumer = new ConsumerBuilder<string, string>(_consumerConfig).Build();
            consumer.Subscribe(Constants.KafkaTopic);

            _logger.LogInformation("Consumer-{Id} started, subscribed to {Topic}", consumerId, Constants.KafkaTopic);

            var db = _redis.GetDatabase();
            var processor = new FlightProcessor(db, _loggerFactory.CreateLogger<FlightProcessor>());
            long skipped = 0;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var result = consumer.Consume(ct);
                    if (result?.Message?.Value is null) continue;

                    try
                    {
                        var flight = JsonSerializer.Deserialize<FlightTelemetry>(result.Message.Value);
                        if (flight is null || flight.Latitude is null || flight.Longitude is null) continue;

                        // Discard stale messages aggressively
                        var ageSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - flight.LastUpdate;
                        if (ageSeconds > StaleThreshold.TotalSeconds)
                        {
                            skipped++;
                            if (skipped % 10_000 == 0)
                                _logger.LogInformation("Consumer-{Id}: skipped {Count} stale messages (age > {Sec}s)",
                                    consumerId, skipped, (int)StaleThreshold.TotalSeconds);
                            continue;
                        }

                        _lastConsumeUtc = DateTime.UtcNow;
                        var position = await processor.ProcessAsync(flight);

                        // Enqueue for batched SignalR delivery (non-blocking)
                        _signalRQueue.Enqueue(position);

                        // Enqueue for async SQLite persistence (non-blocking, drop-oldest if full)
                        _sqliteChannel.Writer.TryWrite(position);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Consumer-{Id}: deserialization failed", consumerId);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Graceful shutdown
            }
            finally
            {
                consumer.Close();
                _logger.LogInformation("Consumer-{Id} stopped (skipped {Skipped} stale messages total)", consumerId, skipped);
            }
        }, ct);
    }
}
