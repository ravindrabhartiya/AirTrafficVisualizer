namespace ATC.TrackingEngine;

using ATC.Shared;
using StackExchange.Redis;

/// <summary>
/// Encapsulates the core flight-processing logic: Redis storage only.
/// SQLite persistence is handled asynchronously by a background drain loop
/// so consumers do the bare minimum and always keep up with producers.
/// </summary>
public sealed class FlightProcessor
{
    private readonly IDatabase _db;
    private readonly ILogger<FlightProcessor> _logger;

    public FlightProcessor(IDatabase db, ILogger<FlightProcessor> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Processes a single flight telemetry message: stores in Redis,
    /// persists to SQLite, and returns the FlightPosition DTO.
    /// </summary>
    public async Task<FlightPosition> ProcessAsync(FlightTelemetry flight)
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

        return new FlightPosition
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
    }
}
