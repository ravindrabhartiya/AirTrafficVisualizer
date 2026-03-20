namespace ATC.Shared;

/// <summary>
/// Pure helper methods for geo-spatial calculations and flight DTO mapping.
/// No external dependencies — designed for easy unit testing.
/// </summary>
public static class GeoHelpers
{
    /// <summary>Parameters for a Redis GEOSEARCH BYBOX query.</summary>
    public record BboxParams(double CenterLon, double CenterLat, double WidthKm, double HeightKm);

    /// <summary>
    /// Parses SW/NE corner coordinates into centre-point + dimensions for a Redis GEOSEARCH BYBOX.
    /// Returns null if any parameter is missing or out of range.
    /// </summary>
    public static BboxParams? ParseBbox(double? swLat, double? swLng, double? neLat, double? neLng)
    {
        if (!swLat.HasValue || !swLng.HasValue || !neLat.HasValue || !neLng.HasValue)
            return null;

        if (swLat.Value < -90 || swLat.Value > 90 || neLat.Value < -90 || neLat.Value > 90)
            return null;
        if (swLng.Value < -180 || swLng.Value > 180 || neLng.Value < -180 || neLng.Value > 180)
            return null;
        if (neLat.Value <= swLat.Value)
            return null;

        var centerLat = (swLat.Value + neLat.Value) / 2;
        var centerLon = (swLng.Value + neLng.Value) / 2;

        var heightKm = Math.Abs(neLat.Value - swLat.Value) * 111.32;
        var widthKm = Math.Abs(neLng.Value - swLng.Value) * 111.32
                      * Math.Cos(centerLat * Math.PI / 180);

        // 10 % padding so markers at the very edge aren't clipped
        widthKm *= 1.1;
        heightKm *= 1.1;

        return new BboxParams(centerLon, centerLat, widthKm, heightKm);
    }

    /// <summary>
    /// Builds a <see cref="FlightPosition"/> from a Redis hash stored as a string dictionary.
    /// Returns null when the dictionary is empty.
    /// </summary>
    public static FlightPosition? ParseFlightFromDict(string icao, IReadOnlyDictionary<string, string> dict)
    {
        if (dict.Count == 0) return null;

        return new FlightPosition
        {
            Icao24 = icao,
            Callsign = dict.GetValueOrDefault("callsign", ""),
            Latitude = double.TryParse(dict.GetValueOrDefault("latitude"), out var lat) ? lat : 0,
            Longitude = double.TryParse(dict.GetValueOrDefault("longitude"), out var lon) ? lon : 0,
            Altitude = double.TryParse(dict.GetValueOrDefault("altitude"), out var alt) ? alt : 0,
            Velocity = double.TryParse(dict.GetValueOrDefault("velocity"), out var vel) ? vel : 0,
            TrueTrack = double.TryParse(dict.GetValueOrDefault("trueTrack"), out var trk) ? trk : 0,
            VerticalRate = double.TryParse(dict.GetValueOrDefault("verticalRate"), out var vr) ? vr : 0,
            OnGround = dict.GetValueOrDefault("onGround") == "1",
            OriginCountry = dict.GetValueOrDefault("originCountry", ""),
            LastUpdate = long.TryParse(dict.GetValueOrDefault("lastUpdate"), out var upd) ? upd : 0
        };
    }
}
