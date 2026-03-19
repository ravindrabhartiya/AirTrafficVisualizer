namespace ATC.Shared;

public static class Constants
{
    public const string KafkaTopic = "flight-telemetry";
    public const string RedisGeoKey = "active_flights";
    public const double CollisionRadiusKm = 5.0;
    public const double AltitudeThresholdFeet = 1000.0;
}
