namespace ATC.Shared;

using System.Text.Json.Serialization;

/// <summary>
/// DTO returned by the Dashboard API and pushed via SignalR.
/// </summary>
public sealed class FlightPosition
{
    [JsonPropertyName("icao24")]
    public string Icao24 { get; set; } = string.Empty;

    [JsonPropertyName("cs")]
    public string Callsign { get; set; } = string.Empty;

    [JsonPropertyName("lat")]
    public double Latitude { get; set; }

    [JsonPropertyName("lon")]
    public double Longitude { get; set; }

    [JsonPropertyName("alt")]
    public double Altitude { get; set; }

    [JsonPropertyName("vel")]
    public double Velocity { get; set; }

    [JsonPropertyName("trk")]
    public double TrueTrack { get; set; }

    [JsonPropertyName("gnd")]
    public bool OnGround { get; set; }

    [JsonPropertyName("ts")]
    public long LastUpdate { get; set; }

    [JsonPropertyName("cty")]
    public string OriginCountry { get; set; } = string.Empty;

    [JsonPropertyName("vr")]
    public double VerticalRate { get; set; }
}
