using ATC.DashboardApi;
using ATC.Shared;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.AspNetCore.ResponseCompression;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var kafkaBootstrap = builder.Configuration.GetValue("Kafka:BootstrapServers", "localhost:9092")!;
var redisConnection = builder.Configuration.GetValue("Redis:ConnectionString", "localhost:6379")!;
var dbPath = builder.Configuration.GetValue("Sqlite:DbPath", "flights.db")!;

var redisOptions = ConfigurationOptions.Parse(redisConnection);
redisOptions.AllowAdmin = true;
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisOptions));
builder.Services.AddSingleton<IFlightSnapshotStore>(new SqliteFlightSnapshotStore(dbPath));
builder.Services.AddSingleton(new AdminClientConfig { BootstrapServers = kafkaBootstrap });

builder.Services.AddSignalR();

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/json" });
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              .SetIsOriginAllowed(_ => true);
    });
});

var app = builder.Build();

app.UseResponseCompression();
app.UseCors();
app.UseStaticFiles();

app.MapHub<FlightHub>("/flighthub");

app.MapGet("/radar", async (IConnectionMultiplexer redis, IFlightSnapshotStore snapshotStore,
    double? swLat, double? swLng, double? neLat, double? neLng) =>
{
    var db = redis.GetDatabase();

    // ── Determine which ICAO24 members to fetch ──
    RedisValue[] members;
    var bbox = GeoHelpers.ParseBbox(swLat, swLng, neLat, neLng);

    if (bbox is not null)
    {
        // Viewport-filtered: only return flights visible on the map
        var results = await db.GeoSearchAsync(
            Constants.RedisGeoKey,
            bbox.CenterLon, bbox.CenterLat,
            new GeoSearchBox(bbox.WidthKm, bbox.HeightKm, GeoUnit.Kilometers));
        members = results.Select(r => r.Member).ToArray();
    }
    else
    {
        // No bbox — return all (backward compatible)
        members = await db.SortedSetRangeByRankAsync(Constants.RedisGeoKey);
    }

    if (members is null || members.Length == 0)
    {
        // Cold start: fall back to SQLite snapshot
        var snapshot = await snapshotStore.LoadAllAsync(TimeSpan.FromMinutes(30));
        return Results.Ok(snapshot);
    }

    // ── Pipelined Redis lookups (one round-trip for all hashes) ──
    var batch = db.CreateBatch();
    var tasks = new Dictionary<string, Task<HashEntry[]>>(members.Length);
    foreach (var member in members)
    {
        var icao = member.ToString();
        tasks[icao] = batch.HashGetAllAsync($"flight:{icao}");
    }
    batch.Execute();
    await Task.WhenAll(tasks.Values);

    var flights = new List<FlightPosition>(tasks.Count);
    foreach (var (icao, task) in tasks)
    {
        var hash = task.Result;
        if (hash.Length == 0) continue;

        var dict = hash.ToDictionary(
            h => h.Name.ToString(),
            h => h.Value.ToString());
        var fp = GeoHelpers.ParseFlightFromDict(icao, dict);
        if (fp is not null) flights.Add(fp);
    }

    return Results.Ok(flights);
});

app.MapFallbackToFile("index.html");

// ─── Backend Metrics ─────────────────────────────────────────────────────
app.MapGet("/api/metrics", async (IConnectionMultiplexer redis, AdminClientConfig kafkaConfig) =>
{
    var result = new Dictionary<string, object>();
    var db = redis.GetDatabase();

    // ── Redis metrics ──
    try
    {
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var info = await server.InfoAsync();
        var memSection = info.FirstOrDefault(s => s.Key == "Memory");
        var clientSection = info.FirstOrDefault(s => s.Key == "Clients");
        var statsSection = info.FirstOrDefault(s => s.Key == "Stats");

        var geoCount = await db.SortedSetLengthAsync(Constants.RedisGeoKey);
        var dbSize = await server.DatabaseSizeAsync();

        result["redis"] = new
        {
            activeFlights = geoCount,
            totalKeys = dbSize,
            usedMemory = memSection.FirstOrDefault(k => k.Key == "used_memory_human").Value ?? "N/A",
            usedMemoryPeak = memSection.FirstOrDefault(k => k.Key == "used_memory_peak_human").Value ?? "N/A",
            connectedClients = clientSection.FirstOrDefault(k => k.Key == "connected_clients").Value ?? "N/A",
            opsPerSec = statsSection.FirstOrDefault(k => k.Key == "instantaneous_ops_per_sec").Value ?? "N/A",
            uptimeSeconds = info.FirstOrDefault(s => s.Key == "Server")
                .FirstOrDefault(k => k.Key == "uptime_in_seconds").Value ?? "N/A"
        };
    }
    catch (Exception ex)
    {
        result["redis"] = new { error = ex.Message };
    }

    // ── Kafka metrics ──
    try
    {
        using var admin = new AdminClientBuilder(kafkaConfig).Build();
        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(5));

        // Broker info
        var brokers = metadata.Brokers.Select(b => new
        {
            id = b.BrokerId,
            host = b.Host,
            port = b.Port
        });

        // Topic info
        var topicMeta = metadata.Topics.FirstOrDefault(t => t.Topic == Constants.KafkaTopic);
        object? topicInfo = null;
        if (topicMeta is not null)
        {
            topicInfo = new
            {
                name = topicMeta.Topic,
                partitions = topicMeta.Partitions.Count,
                replicationFactor = topicMeta.Partitions.FirstOrDefault()?.Replicas.Length ?? 0
            };
        }

        // Consumer group lag
        var groups = admin.ListGroups(TimeSpan.FromSeconds(5));
        var trackingGroup = groups.FirstOrDefault(g => g.Group == "tracking-engine");

        object? consumerInfo = null;
        if (trackingGroup is not null && topicMeta is not null)
        {
            consumerInfo = new
            {
                groupId = trackingGroup.Group,
                state = trackingGroup.State,
                members = trackingGroup.Members.Count,
                protocol = trackingGroup.Protocol
            };
        }

        // Get offsets for lag calculation
        var partitionLags = new List<object>();
        long totalLag = 0;
        long totalCurrent = 0;
        long totalEnd = 0;

        if (topicMeta is not null)
        {
            var tps = topicMeta.Partitions
                .Select(p => new TopicPartition(Constants.KafkaTopic, p.PartitionId))
                .ToList();

            // Get high watermarks (log end offsets)
            var watermarks = new Dictionary<int, WatermarkOffsets>();
            using var consumer = new ConsumerBuilder<string, string>(
                new ConsumerConfig
                {
                    BootstrapServers = kafkaConfig.BootstrapServers,
                    GroupId = "__metrics_reader"
                }).Build();

            foreach (var tp in tps)
            {
                var wm = consumer.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(5));
                watermarks[tp.Partition.Value] = wm;
            }

            // Get committed offsets for tracking-engine group
            using var offsetConsumer = new ConsumerBuilder<string, string>(
                new ConsumerConfig
                {
                    BootstrapServers = kafkaConfig.BootstrapServers,
                    GroupId = "tracking-engine",
                    EnableAutoCommit = false
                }).Build();

            var committedList = offsetConsumer.Committed(tps, TimeSpan.FromSeconds(5));
            var committedOffsets = committedList
                .ToDictionary(p => p.Partition.Value, p => p.Offset.Value);

            foreach (var tp in tps)
            {
                var pid = tp.Partition.Value;
                var high = watermarks.GetValueOrDefault(pid).High.Value;
                var current = committedOffsets.GetValueOrDefault(pid, -1);
                var lag = current >= 0 ? high - current : high;

                totalLag += lag;
                totalCurrent += current >= 0 ? current : 0;
                totalEnd += high;

                partitionLags.Add(new
                {
                    partition = pid,
                    currentOffset = current,
                    logEndOffset = high,
                    lag
                });
            }
        }

        result["kafka"] = new
        {
            brokers,
            topic = topicInfo,
            consumer = consumerInfo,
            partitions = partitionLags,
            totalLag,
            totalMessages = totalEnd,
            totalConsumed = totalCurrent
        };
    }
    catch (Exception ex)
    {
        result["kafka"] = new { error = ex.Message };
    }

    // ── SQLite metrics ──
    try
    {
        var flights = await db.SortedSetLengthAsync(Constants.RedisGeoKey);
        var fileInfo = new FileInfo(dbPath);
        result["sqlite"] = new
        {
            dbSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            dbPath = fileInfo.FullName
        };
    }
    catch (Exception ex)
    {
        result["sqlite"] = new { error = ex.Message };
    }

    result["timestamp"] = DateTime.UtcNow.ToString("O");

    return Results.Ok(result);
});

// Route lookup: uses adsbdb.com (free, no auth) with Redis caching
app.MapGet("/route/{callsign}", async (string callsign, IConnectionMultiplexer redis) =>
{
    // Sanitise input — callsigns are alphanumeric only
    var clean = new string(callsign.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToUpperInvariant();
    if (string.IsNullOrEmpty(clean))
        return Results.BadRequest("Invalid callsign");

    var db = redis.GetDatabase();
    var cacheKey = $"route:{clean}";

    // Check Redis cache first
    var cached = await db.StringGetAsync(cacheKey);
    if (cached.HasValue)
        return Results.Content(cached.ToString(), "application/json");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    http.DefaultRequestHeaders.Add("User-Agent", "ATC-Dashboard/1.0");

    // adsbdb.com — returns airline, origin, destination with full airport details
    try
    {
        var resp = await http.GetAsync($"https://api.adsbdb.com/v0/callsign/{clean}");
        if (resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadAsStringAsync();
            // Forward the full adsbdb response — frontend will parse it
            await db.StringSetAsync(cacheKey, json, TimeSpan.FromHours(1));
            return Results.Content(json, "application/json");
        }
    }
    catch { /* Fall through */ }

    // Cache miss (5 min) to avoid hammering API
    var notFound = System.Text.Json.JsonSerializer.Serialize(new { response = (object?)null });
    await db.StringSetAsync(cacheKey, notFound, TimeSpan.FromMinutes(5));
    return Results.Content(notFound, "application/json");
});

// Aircraft info lookup: uses hexdb.io (free, no auth) for registration, type, owner
app.MapGet("/aircraft/{icao24}", async (string icao24, IConnectionMultiplexer redis) =>
{
    var clean = new string(icao24.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLowerInvariant();
    if (string.IsNullOrEmpty(clean))
        return Results.BadRequest("Invalid ICAO24");

    var db = redis.GetDatabase();
    var cacheKey = $"aircraft:{clean}";

    var cached = await db.StringGetAsync(cacheKey);
    if (cached.HasValue)
        return Results.Content(cached.ToString(), "application/json");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    http.DefaultRequestHeaders.Add("User-Agent", "ATC-Dashboard/1.0");

    try
    {
        var resp = await http.GetAsync($"https://hexdb.io/api/v1/aircraft/{clean}");
        if (resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadAsStringAsync();
            await db.StringSetAsync(cacheKey, json, TimeSpan.FromHours(24));
            return Results.Content(json, "application/json");
        }
    }
    catch { /* Fall through */ }

    var notFound = "{}";
    await db.StringSetAsync(cacheKey, notFound, TimeSpan.FromHours(1));
    return Results.Content(notFound, "application/json");
});

app.Run();
