namespace ATC.Shared;

using Microsoft.Data.Sqlite;

/// <summary>
/// SQLite-backed flight snapshot store. Persists latest flight positions so data
/// survives service restarts (eliminates the cold-start empty-radar problem).
/// </summary>
public sealed class SqliteFlightSnapshotStore : IFlightSnapshotStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteFlightSnapshotStore(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS flight_snapshots (
                icao24        TEXT PRIMARY KEY,
                callsign      TEXT NOT NULL DEFAULT '',
                latitude      REAL NOT NULL,
                longitude     REAL NOT NULL,
                altitude      REAL NOT NULL DEFAULT 0,
                velocity      REAL NOT NULL DEFAULT 0,
                true_track    REAL NOT NULL DEFAULT 0,
                vertical_rate REAL NOT NULL DEFAULT 0,
                on_ground     INTEGER NOT NULL DEFAULT 0,
                origin_country TEXT NOT NULL DEFAULT '',
                last_update   INTEGER NOT NULL,
                updated_at    TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_snapshots_updated ON flight_snapshots(updated_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task UpsertAsync(FlightPosition position)
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO flight_snapshots
                    (icao24, callsign, latitude, longitude, altitude, velocity,
                     true_track, vertical_rate, on_ground, origin_country, last_update, updated_at)
                VALUES
                    ($icao, $cs, $lat, $lon, $alt, $vel, $trk, $vr, $gnd, $country, $upd, datetime('now'))
                ON CONFLICT(icao24) DO UPDATE SET
                    callsign = excluded.callsign,
                    latitude = excluded.latitude,
                    longitude = excluded.longitude,
                    altitude = excluded.altitude,
                    velocity = excluded.velocity,
                    true_track = excluded.true_track,
                    vertical_rate = excluded.vertical_rate,
                    on_ground = excluded.on_ground,
                    origin_country = excluded.origin_country,
                    last_update = excluded.last_update,
                    updated_at = datetime('now');
                """;
            cmd.Parameters.AddWithValue("$icao", position.Icao24);
            cmd.Parameters.AddWithValue("$cs", position.Callsign);
            cmd.Parameters.AddWithValue("$lat", position.Latitude);
            cmd.Parameters.AddWithValue("$lon", position.Longitude);
            cmd.Parameters.AddWithValue("$alt", position.Altitude);
            cmd.Parameters.AddWithValue("$vel", position.Velocity);
            cmd.Parameters.AddWithValue("$trk", position.TrueTrack);
            cmd.Parameters.AddWithValue("$vr", position.VerticalRate);
            cmd.Parameters.AddWithValue("$gnd", position.OnGround ? 1 : 0);
            cmd.Parameters.AddWithValue("$country", position.OriginCountry);
            cmd.Parameters.AddWithValue("$upd", position.LastUpdate);
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<FlightPosition>> LoadAllAsync(TimeSpan maxAge)
    {
        await _lock.WaitAsync();
        try
        {
        var cutoff = DateTime.UtcNow - maxAge;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT icao24, callsign, latitude, longitude, altitude, velocity,
                   true_track, vertical_rate, on_ground, origin_country, last_update
            FROM flight_snapshots
            WHERE updated_at >= $cutoff;
            """;
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("yyyy-MM-dd HH:mm:ss"));

        var flights = new List<FlightPosition>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            flights.Add(new FlightPosition
            {
                Icao24 = reader.GetString(0),
                Callsign = reader.GetString(1),
                Latitude = reader.GetDouble(2),
                Longitude = reader.GetDouble(3),
                Altitude = reader.GetDouble(4),
                Velocity = reader.GetDouble(5),
                TrueTrack = reader.GetDouble(6),
                VerticalRate = reader.GetDouble(7),
                OnGround = reader.GetInt32(8) == 1,
                OriginCountry = reader.GetString(9),
                LastUpdate = reader.GetInt64(10)
            });
        }

        return (IReadOnlyList<FlightPosition>)flights;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PurgeStaleAsync(TimeSpan maxAge)
    {
        await _lock.WaitAsync();
        try
        {
            var cutoff = DateTime.UtcNow - maxAge;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM flight_snapshots WHERE updated_at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
