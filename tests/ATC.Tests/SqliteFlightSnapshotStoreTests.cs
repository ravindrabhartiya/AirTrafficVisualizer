namespace ATC.Tests;

using ATC.Shared;

public sealed class SqliteFlightSnapshotStoreTests : IDisposable
{
    private readonly SqliteFlightSnapshotStore _store;

    public SqliteFlightSnapshotStoreTests()
    {
        // Use a unique in-memory database per test instance (shared cache keeps it alive for the connection)
        var name = Guid.NewGuid().ToString("N");
        _store = new SqliteFlightSnapshotStore($":memory:");
    }

    public void Dispose()
    {
        _store.Dispose();
    }

    private static FlightPosition MakeFlight(string icao = "ABC123", double lat = 47.6, double lon = -122.3) => new()
    {
        Icao24 = icao,
        Callsign = "TEST001",
        Latitude = lat,
        Longitude = lon,
        Altitude = 35000,
        Velocity = 220,
        TrueTrack = 90,
        VerticalRate = 0,
        OnGround = false,
        OriginCountry = "UnitTest",
        LastUpdate = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    };

    [Fact]
    public async Task Upsert_ThenLoadAll_ReturnsFlight()
    {
        var flight = MakeFlight();
        await _store.UpsertAsync(flight);

        var loaded = await _store.LoadAllAsync(TimeSpan.FromMinutes(5));

        Assert.Single(loaded);
        Assert.Equal("ABC123", loaded[0].Icao24);
        Assert.Equal("TEST001", loaded[0].Callsign);
        Assert.Equal(47.6, loaded[0].Latitude, 1);
        Assert.Equal(-122.3, loaded[0].Longitude, 1);
        Assert.Equal(35000, loaded[0].Altitude, 0);
    }

    [Fact]
    public async Task Upsert_SameIcao_UpdatesInPlace()
    {
        await _store.UpsertAsync(MakeFlight("DUP01", lat: 40.0));
        await _store.UpsertAsync(MakeFlight("DUP01", lat: 50.0));

        var loaded = await _store.LoadAllAsync(TimeSpan.FromMinutes(5));

        Assert.Single(loaded);
        Assert.Equal(50.0, loaded[0].Latitude, 1);
    }

    [Fact]
    public async Task LoadAll_RespectsMaxAge()
    {
        await _store.UpsertAsync(MakeFlight());

        // Wait so the row is definitely older than the cutoff
        await Task.Delay(2500);

        var loaded = await _store.LoadAllAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task PurgeStale_RemovesOldEntries()
    {
        await _store.UpsertAsync(MakeFlight());

        // Purge with very large max age — nothing should be removed
        await _store.PurgeStaleAsync(TimeSpan.FromHours(1));
        var remaining = await _store.LoadAllAsync(TimeSpan.FromHours(1));
        Assert.Single(remaining);

        // Wait so the row ages past the threshold
        await Task.Delay(2500);

        // Purge with 1-second max age — row is now stale
        await _store.PurgeStaleAsync(TimeSpan.FromSeconds(1));
        remaining = await _store.LoadAllAsync(TimeSpan.FromHours(1));
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task MultipleFlights_AllReturned()
    {
        for (int i = 0; i < 10; i++)
            await _store.UpsertAsync(MakeFlight($"FLT{i:D3}"));

        var loaded = await _store.LoadAllAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(10, loaded.Count);
    }

    [Fact]
    public async Task OnGround_PersistsCorrectly()
    {
        var flight = MakeFlight();
        flight.OnGround = true;
        await _store.UpsertAsync(flight);

        var loaded = await _store.LoadAllAsync(TimeSpan.FromMinutes(5));
        Assert.True(loaded[0].OnGround);
    }
}
