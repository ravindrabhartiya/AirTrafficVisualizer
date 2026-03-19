namespace ATC.TrackingEngine;

using System.Text.Json;
using ATC.Shared;
using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR.Client;
using StackExchange.Redis;

public sealed class Worker : BackgroundService
{
    private readonly ConsumerConfig _consumerConfig;
    private readonly IConnectionMultiplexer _redis;
    private readonly SignalRConfig _signalRConfig;
    private readonly ILogger<Worker> _logger;

    public Worker(
        ConsumerConfig consumerConfig,
        IConnectionMultiplexer redis,
        SignalRConfig signalRConfig,
        ILogger<Worker> logger)
    {
        _consumerConfig = consumerConfig;
        _redis = redis;
        _signalRConfig = signalRConfig;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Build SignalR connection with retry
        var hubConnection = new HubConnectionBuilder()
            .WithUrl(_signalRConfig.HubUrl)
            .WithAutomaticReconnect()
            .Build();

        await ConnectToSignalRAsync(hubConnection, stoppingToken);

        // Run Kafka consumer on a background thread (Consume is blocking)
        await Task.Run(() => ConsumeLoop(hubConnection, stoppingToken), stoppingToken);
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

    private void ConsumeLoop(HubConnection hubConnection, CancellationToken ct)
    {
        using var consumer = new ConsumerBuilder<string, string>(_consumerConfig).Build();
        consumer.Subscribe(Constants.KafkaTopic);

        _logger.LogInformation("Tracking Engine consumer started, subscribed to {Topic}", Constants.KafkaTopic);

        var db = _redis.GetDatabase();

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

                    ProcessFlight(db, flight, hubConnection).GetAwaiter().GetResult();
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize flight message");
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
            _logger.LogInformation("Tracking Engine consumer stopped");
        }
    }

    private async Task ProcessFlight(IDatabase db, FlightTelemetry flight, HubConnection hub)
    {
        double lat = flight.Latitude!.Value;
        double lon = flight.Longitude!.Value;
        double alt = flight.BaroAltitude ?? 0;

        // Store position in Redis geo key
        await db.GeoAddAsync(Constants.RedisGeoKey, lon, lat, flight.Icao24);

        // Store metadata as a hash
        var hashKey = $"flight:{flight.Icao24}";
        var entries = new HashEntry[]
        {
            new("callsign",      flight.Callsign),
            new("latitude",      lat.ToString("F6")),
            new("longitude",     lon.ToString("F6")),
            new("altitude",      alt.ToString("F1")),
            new("velocity",      (flight.Velocity ?? 0).ToString("F1")),
            new("trueTrack",     (flight.TrueTrack ?? 0).ToString("F1")),
            new("verticalRate",  (flight.VerticalRate ?? 0).ToString("F1")),
            new("onGround",      flight.OnGround ? "1" : "0"),
            new("originCountry", flight.OriginCountry),
            new("lastUpdate",    flight.LastUpdate.ToString())
        };
        await db.HashSetAsync(hashKey, entries);
        await db.KeyExpireAsync(hashKey, TimeSpan.FromMinutes(5));

        // Collision detection: GEOSEARCH for nearby aircraft
        var nearby = await db.GeoSearchAsync(
            Constants.RedisGeoKey,
            lon, lat,
            new GeoSearchCircle(Constants.CollisionRadiusKm, GeoUnit.Kilometers),
            count: 20);

        if (nearby.Length > 1)
        {
            foreach (var neighbor in nearby)
            {
                var neighborId = neighbor.Member.ToString();
                if (neighborId == flight.Icao24) continue;

                // Check altitude proximity
                var neighborHash = await db.HashGetAsync($"flight:{neighborId}", "altitude");
                if (neighborHash.HasValue && double.TryParse(neighborHash, out double neighborAlt))
                {
                    double altDiff = Math.Abs(alt - neighborAlt);
                    if (altDiff <= Constants.AltitudeThresholdFeet)
                    {
                        double distKm = neighbor.Distance ?? 0;
                        _logger.LogCritical(
                            "⚠️  COLLISION WARNING: {Icao1} ({Call1}) and {Icao2} at {Alt1:F0}ft - " +
                            "distance {Dist:F2}km, altitude diff {AltDiff:F0}ft",
                            flight.Icao24, flight.Callsign, neighborId, alt, distKm, altDiff);
                    }
                }
            }
        }

        // Push update to SignalR hub
        if (hub.State == HubConnectionState.Connected)
        {
            var position = new FlightPosition
            {
                Icao24 = flight.Icao24,
                Callsign = flight.Callsign,
                Latitude = lat,
                Longitude = lon,
                Altitude = alt,
                Velocity = flight.Velocity ?? 0,
                TrueTrack = flight.TrueTrack ?? 0,
                VerticalRate = flight.VerticalRate ?? 0,
                OnGround = flight.OnGround,
                OriginCountry = flight.OriginCountry,
                LastUpdate = flight.LastUpdate
            };

            try
            {
                await hub.InvokeAsync("BroadcastFlightUpdate", position);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send SignalR update for {Icao}", flight.Icao24);
            }
        }
    }
}
