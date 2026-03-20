namespace ATC.TrackingEngine;

using ATC.Shared;
using StackExchange.Redis;

/// <summary>
/// Encapsulates the core flight-processing logic: Redis storage, collision detection, and SQLite persistence.
/// Extracted from Worker to make it independently testable.
/// </summary>
public sealed class FlightProcessor
{
    private readonly IDatabase _db;
    private readonly IFlightSnapshotStore _snapshotStore;
    private readonly ILogger<FlightProcessor> _logger;

    public FlightProcessor(IDatabase db, IFlightSnapshotStore snapshotStore, ILogger<FlightProcessor> logger)
    {
        _db = db;
        _snapshotStore = snapshotStore;
        _logger = logger;
    }

    /// <summary>
    /// Processes a single flight telemetry message: stores in Redis, checks collisions,
    /// persists to SQLite, and returns the FlightPosition DTO.
    /// </summary>
    public async Task<(FlightPosition Position, List<CollisionWarning> Warnings)> ProcessAsync(FlightTelemetry flight)
    {
        double lat = flight.Latitude!.Value;
        double lon = flight.Longitude!.Value;
        double alt = flight.BaroAltitude ?? 0;

        // Store position in Redis geo key
        await _db.GeoAddAsync(Constants.RedisGeoKey, lon, lat, flight.Icao24);

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
        await _db.HashSetAsync(hashKey, entries);
        await _db.KeyExpireAsync(hashKey, TimeSpan.FromMinutes(5));

        // Collision detection — only for airborne aircraft above minimum altitude
        var warnings = (!flight.OnGround && alt >= Constants.MinAltitudeFeet)
            ? await DetectCollisionsAsync(flight.Icao24, flight.Callsign, lon, lat, alt)
            : new List<CollisionWarning>();

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

        // Persist to SQLite
        try { await _snapshotStore.UpsertAsync(position); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to persist flight {Icao} to SQLite", flight.Icao24); }

        return (position, warnings);
    }

    private async Task<List<CollisionWarning>> DetectCollisionsAsync(string icao, string callsign, double lon, double lat, double alt)
    {
        var warnings = new List<CollisionWarning>();

        var nearby = await _db.GeoSearchAsync(
            Constants.RedisGeoKey,
            lon, lat,
            new GeoSearchCircle(Constants.CollisionRadiusKm, GeoUnit.Kilometers),
            count: 20);

        if (nearby.Length <= 1) return warnings;

        foreach (var neighbor in nearby)
        {
            var neighborId = neighbor.Member.ToString();
            if (neighborId == icao) continue;

            var neighborFields = await _db.HashGetAsync($"flight:{neighborId}", new RedisValue[] { "altitude", "onGround" });
            if (!neighborFields[0].HasValue) continue;
            bool neighborOnGround = neighborFields[1].HasValue && neighborFields[1].ToString() == "1";
            if (neighborOnGround) continue; // skip aircraft on the ground
            if (double.TryParse(neighborFields[0], out double neighborAlt) && neighborAlt >= Constants.MinAltitudeFeet)
            {
                double altDiff = Math.Abs(alt - neighborAlt);
                if (altDiff <= Constants.AltitudeThresholdFeet)
                {
                    double distKm = neighbor.Distance ?? 0;
                    _logger.LogCritical(
                        "⚠️  COLLISION WARNING: {Icao1} ({Call1}) and {Icao2} at {Alt1:F0}ft - " +
                        "distance {Dist:F2}km, altitude diff {AltDiff:F0}ft",
                        icao, callsign, neighborId, alt, distKm, altDiff);

                    warnings.Add(new CollisionWarning(icao, neighborId, distKm, altDiff));
                }
            }
        }

        return warnings;
    }
}

public sealed record CollisionWarning(string Icao1, string Icao2, double DistanceKm, double AltitudeDiffFeet);
