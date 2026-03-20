namespace ATC.Tests;

using System.Text.Json;
using ATC.Shared;

public sealed class GeoHelpersTests
{
    // ─── ParseBbox ──────────────────────────────────────────────

    [Fact]
    public void ParseBbox_AllNull_ReturnsNull()
    {
        Assert.Null(GeoHelpers.ParseBbox(null, null, null, null));
    }

    [Fact]
    public void ParseBbox_PartialNull_ReturnsNull()
    {
        Assert.Null(GeoHelpers.ParseBbox(40.0, -74.0, null, -73.0));
    }

    [Fact]
    public void ParseBbox_LatitudeOutOfRange_ReturnsNull()
    {
        Assert.Null(GeoHelpers.ParseBbox(-100, -74, 41, -73)); // swLat < -90
    }

    [Fact]
    public void ParseBbox_LongitudeOutOfRange_ReturnsNull()
    {
        Assert.Null(GeoHelpers.ParseBbox(40, -200, 41, -73)); // swLng < -180
    }

    [Fact]
    public void ParseBbox_NorthSouthInverted_ReturnsNull()
    {
        // neLat must be > swLat
        Assert.Null(GeoHelpers.ParseBbox(41, -74, 40, -73));
    }

    [Fact]
    public void ParseBbox_ValidCoords_ReturnsCorrectCenter()
    {
        var result = GeoHelpers.ParseBbox(40.0, -74.0, 42.0, -72.0);

        Assert.NotNull(result);
        Assert.Equal(41.0, result.CenterLat, 1);
        Assert.Equal(-73.0, result.CenterLon, 1);
    }

    [Fact]
    public void ParseBbox_ValidCoords_HeightIsPositive()
    {
        var result = GeoHelpers.ParseBbox(40.0, -74.0, 42.0, -72.0);

        Assert.NotNull(result);
        // 2 degrees of lat ≈ 222.64 km * 1.1 padding ≈ 244.9 km
        Assert.True(result.HeightKm > 240);
        Assert.True(result.HeightKm < 250);
    }

    [Fact]
    public void ParseBbox_ValidCoords_WidthIsPositive()
    {
        var result = GeoHelpers.ParseBbox(40.0, -74.0, 42.0, -72.0);

        Assert.NotNull(result);
        Assert.True(result.WidthKm > 100);  // cos(41°) shrinks it
    }

    [Fact]
    public void ParseBbox_EquatorRegion_WidthEqualsHeight()
    {
        // At equator, cos(0) = 1 so 1° lat ≈ 1° lon in km
        var result = GeoHelpers.ParseBbox(-1.0, -1.0, 1.0, 1.0);

        Assert.NotNull(result);
        Assert.Equal(result.WidthKm, result.HeightKm, 0);
    }

    // ─── ParseFlightFromDict ────────────────────────────────────

    [Fact]
    public void ParseFlightFromDict_EmptyDict_ReturnsNull()
    {
        var dict = new Dictionary<string, string>();
        Assert.Null(GeoHelpers.ParseFlightFromDict("abc123", dict));
    }

    [Fact]
    public void ParseFlightFromDict_FullDict_MapsAllFields()
    {
        var dict = new Dictionary<string, string>
        {
            ["callsign"] = "UAL123",
            ["latitude"] = "47.600000",
            ["longitude"] = "-122.300000",
            ["altitude"] = "35000.0",
            ["velocity"] = "250.5",
            ["trueTrack"] = "180.0",
            ["verticalRate"] = "-5.0",
            ["onGround"] = "0",
            ["originCountry"] = "United States",
            ["lastUpdate"] = "1700000000"
        };

        var fp = GeoHelpers.ParseFlightFromDict("abc123", dict);

        Assert.NotNull(fp);
        Assert.Equal("abc123", fp.Icao24);
        Assert.Equal("UAL123", fp.Callsign);
        Assert.Equal(47.6, fp.Latitude, 2);
        Assert.Equal(-122.3, fp.Longitude, 2);
        Assert.Equal(35000.0, fp.Altitude, 1);
        Assert.Equal(250.5, fp.Velocity, 1);
        Assert.Equal(180.0, fp.TrueTrack, 1);
        Assert.Equal(-5.0, fp.VerticalRate, 1);
        Assert.False(fp.OnGround);
        Assert.Equal("United States", fp.OriginCountry);
        Assert.Equal(1700000000, fp.LastUpdate);
    }

    [Fact]
    public void ParseFlightFromDict_OnGround_ParsesCorrectly()
    {
        var dict = new Dictionary<string, string>
        {
            ["onGround"] = "1",
            ["latitude"] = "0",
            ["longitude"] = "0"
        };

        var fp = GeoHelpers.ParseFlightFromDict("xyz", dict);
        Assert.NotNull(fp);
        Assert.True(fp.OnGround);
    }

    [Fact]
    public void ParseFlightFromDict_MissingFields_DefaultsToZero()
    {
        var dict = new Dictionary<string, string>
        {
            ["callsign"] = "TEST"
        };

        var fp = GeoHelpers.ParseFlightFromDict("abc", dict);
        Assert.NotNull(fp);
        Assert.Equal(0, fp.Latitude);
        Assert.Equal(0, fp.Longitude);
        Assert.Equal(0, fp.Altitude);
        Assert.Equal(0, fp.Velocity);
    }

    // ─── FlightPosition compact JSON serialization ──────────────

    [Fact]
    public void FlightPosition_SerializesToCompactPropertyNames()
    {
        var fp = new FlightPosition
        {
            Icao24 = "abc123",
            Callsign = "UAL456",
            Latitude = 47.6,
            Longitude = -122.3,
            Altitude = 35000,
            Velocity = 250,
            TrueTrack = 180,
            VerticalRate = -5,
            OnGround = false,
            OriginCountry = "US",
            LastUpdate = 1700000000
        };

        var json = JsonSerializer.Serialize(fp);

        Assert.Contains("\"icao24\":", json);
        Assert.Contains("\"cs\":", json);
        Assert.Contains("\"lat\":", json);
        Assert.Contains("\"lon\":", json);
        Assert.Contains("\"alt\":", json);
        Assert.Contains("\"vel\":", json);
        Assert.Contains("\"trk\":", json);
        Assert.Contains("\"vr\":", json);
        Assert.Contains("\"gnd\":", json);
        Assert.Contains("\"cty\":", json);
        Assert.Contains("\"ts\":", json);

        // Must NOT contain old long names
        Assert.DoesNotContain("\"callsign\":", json);
        Assert.DoesNotContain("\"latitude\":", json);
        Assert.DoesNotContain("\"longitude\":", json);
        Assert.DoesNotContain("\"altitude\":", json);
        Assert.DoesNotContain("\"velocity\":", json);
        Assert.DoesNotContain("\"trueTrack\":", json);
        Assert.DoesNotContain("\"verticalRate\":", json);
        Assert.DoesNotContain("\"onGround\":", json);
        Assert.DoesNotContain("\"originCountry\":", json);
        Assert.DoesNotContain("\"lastUpdate\":", json);
    }

    [Fact]
    public void FlightPosition_DeserializesFromCompactPropertyNames()
    {
        var json = """{"icao24":"abc","cs":"UAL","lat":47.6,"lon":-122.3,"alt":35000,"vel":250,"trk":180,"vr":-5,"gnd":false,"cty":"US","ts":1700000000}""";

        var fp = JsonSerializer.Deserialize<FlightPosition>(json);

        Assert.NotNull(fp);
        Assert.Equal("abc", fp.Icao24);
        Assert.Equal("UAL", fp.Callsign);
        Assert.Equal(47.6, fp.Latitude);
        Assert.Equal(-122.3, fp.Longitude);
        Assert.Equal(35000, fp.Altitude);
    }

    [Fact]
    public void FlightPosition_CompactJson_IsSmallerThanLegacy()
    {
        var fp = new FlightPosition
        {
            Icao24 = "abc123",
            Callsign = "UAL456",
            Latitude = 47.6,
            Longitude = -122.3,
            Altitude = 35000,
            Velocity = 250,
            TrueTrack = 180,
            VerticalRate = -5,
            OnGround = false,
            OriginCountry = "United States",
            LastUpdate = 1700000000
        };

        var compactJson = JsonSerializer.Serialize(fp);
        // Legacy would use long names — the compact names should be shorter
        Assert.True(compactJson.Length < 200, $"Compact JSON should be under 200 chars, was {compactJson.Length}");
    }
}
