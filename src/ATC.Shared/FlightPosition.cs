namespace ATC.Shared;

using System.Text.Json.Serialization;

/// <summary>
/// DTO returned by the Dashboard API and pushed via SignalR.
/// </summary>
public sealed class FlightPosition
{
    [JsonPropertyName("icao24")]
    public string Icao24 { get; set; } = string.Empty;

    [JsonPropertyName("callsign")]
    public string Callsign { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("altitude")]
    public double Altitude { get; set; }

    [JsonPropertyName("velocity")]
    public double Velocity { get; set; }

    [JsonPropertyName("trueTrack")]
    public double TrueTrack { get; set; }

    [JsonPropertyName("onGround")]
    public bool OnGround { get; set; }

    [JsonPropertyName("lastUpdate")]
    public long LastUpdate { get; set; }

    [JsonPropertyName("originCountry")]
    public string OriginCountry { get; set; } = string.Empty;

    [JsonPropertyName("verticalRate")]
    public double VerticalRate { get; set; }
}
