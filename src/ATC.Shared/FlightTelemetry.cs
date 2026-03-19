namespace ATC.Shared;

using System.Text.Json.Serialization;

public sealed class FlightTelemetry
{
    [JsonPropertyName("icao24")]
    public string Icao24 { get; set; } = string.Empty;

    [JsonPropertyName("callsign")]
    public string Callsign { get; set; } = string.Empty;

    [JsonPropertyName("originCountry")]
    public string OriginCountry { get; set; } = string.Empty;

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("baroAltitude")]
    public double? BaroAltitude { get; set; }

    [JsonPropertyName("velocity")]
    public double? Velocity { get; set; }

    [JsonPropertyName("trueTrack")]
    public double? TrueTrack { get; set; }

    [JsonPropertyName("verticalRate")]
    public double? VerticalRate { get; set; }

    [JsonPropertyName("onGround")]
    public bool OnGround { get; set; }

    [JsonPropertyName("lastUpdate")]
    public long LastUpdate { get; set; }
}
