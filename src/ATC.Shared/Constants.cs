namespace ATC.Shared;

public static class Constants
{
    public const string KafkaTopic = "flight-telemetry";
    public const string RedisGeoKey = "active_flights";
    public const double CollisionRadiusKm = 2.0;
    public const double AltitudeThresholdFeet = 500.0;
    public const double MinAltitudeFeet = 500.0;
}
