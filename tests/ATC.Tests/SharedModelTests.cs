namespace ATC.Tests;

using ATC.Shared;
using System.Text.Json;

public sealed class FlightTelemetryTests
{
    [Fact]
    public void Serialize_Deserialize_RoundTrip()
    {
        var telemetry = new FlightTelemetry
        {
            Icao24 = "abc123",
            Callsign = "UAL123",
            OriginCountry = "United States",
            Longitude = -122.3,
            Latitude = 47.6,
            BaroAltitude = 35000,
            Velocity = 220.5,
            TrueTrack = 180.0,
            VerticalRate = -5.0,
            OnGround = false,
            LastUpdate = 1700000000
        };

        var json = JsonSerializer.Serialize(telemetry);
        var deserialized = JsonSerializer.Deserialize<FlightTelemetry>(json)!;

        Assert.Equal(telemetry.Icao24, deserialized.Icao24);
        Assert.Equal(telemetry.Callsign, deserialized.Callsign);
        Assert.Equal(telemetry.Latitude, deserialized.Latitude);
        Assert.Equal(telemetry.Longitude, deserialized.Longitude);
        Assert.Equal(telemetry.BaroAltitude, deserialized.BaroAltitude);
        Assert.Equal(telemetry.OnGround, deserialized.OnGround);
        Assert.Equal(telemetry.LastUpdate, deserialized.LastUpdate);
    }

    [Fact]
    public void Deserialize_WithNulls_HandlesGracefully()
    {
        var json = """{"icao24":"abc","callsign":"TST","latitude":null,"longitude":null,"baroAltitude":null}""";
        var deserialized = JsonSerializer.Deserialize<FlightTelemetry>(json)!;

        Assert.Null(deserialized.Latitude);
        Assert.Null(deserialized.Longitude);
        Assert.Null(deserialized.BaroAltitude);
    }

    [Fact]
    public void JsonPropertyNames_AreCorrect()
    {
        var telemetry = new FlightTelemetry { Icao24 = "test", BaroAltitude = 30000 };
        var json = JsonSerializer.Serialize(telemetry);

        Assert.Contains("\"icao24\":", json);
        Assert.Contains("\"baroAltitude\":", json);
        Assert.Contains("\"callsign\":", json);
        Assert.Contains("\"trueTrack\":", json);
    }
}

public sealed class FlightPositionTests
{
    [Fact]
    public void Serialize_Deserialize_RoundTrip()
    {
        var position = new FlightPosition
        {
            Icao24 = "abc123",
            Callsign = "UAL123",
            Latitude = 47.6,
            Longitude = -122.3,
            Altitude = 35000,
            Velocity = 220,
            TrueTrack = 90,
            VerticalRate = 10,
            OnGround = false,
            OriginCountry = "US",
            LastUpdate = 1700000000
        };

        var json = JsonSerializer.Serialize(position);
        var deserialized = JsonSerializer.Deserialize<FlightPosition>(json)!;

        Assert.Equal(position.Icao24, deserialized.Icao24);
        Assert.Equal(position.Latitude, deserialized.Latitude);
        Assert.Equal(position.Altitude, deserialized.Altitude);
        Assert.Equal(position.VerticalRate, deserialized.VerticalRate);
        Assert.Equal(position.OriginCountry, deserialized.OriginCountry);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var position = new FlightPosition();

        Assert.Equal(string.Empty, position.Icao24);
        Assert.Equal(string.Empty, position.Callsign);
        Assert.Equal(0, position.Latitude);
        Assert.Equal(0, position.Longitude);
        Assert.False(position.OnGround);
    }
}

public sealed class ConstantsTests
{
    [Fact]
    public void KafkaTopic_HasValue()
    {
        Assert.Equal("flight-telemetry", Constants.KafkaTopic);
    }

    [Fact]
    public void CollisionRadius_IsReasonable()
    {
        Assert.True(Constants.CollisionRadiusKm > 0);
        Assert.True(Constants.CollisionRadiusKm <= 50);
    }

    [Fact]
    public void AltitudeThreshold_IsReasonable()
    {
        Assert.True(Constants.AltitudeThresholdFeet > 0);
        Assert.True(Constants.AltitudeThresholdFeet <= 5000);
    }
}
