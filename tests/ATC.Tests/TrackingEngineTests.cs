namespace ATC.Tests;

using ATC.TrackingEngine;

public sealed class CollisionWarningTests
{
    [Fact]
    public void CollisionWarning_RecordEquality()
    {
        var w1 = new CollisionWarning("ABC", "DEF", 3.5, 500);
        var w2 = new CollisionWarning("ABC", "DEF", 3.5, 500);

        Assert.Equal(w1, w2);
    }

    [Fact]
    public void CollisionWarning_Properties()
    {
        var w = new CollisionWarning("ICAO1", "ICAO2", 2.5, 750);

        Assert.Equal("ICAO1", w.Icao1);
        Assert.Equal("ICAO2", w.Icao2);
        Assert.Equal(2.5, w.DistanceKm);
        Assert.Equal(750, w.AltitudeDiffFeet);
    }
}

public sealed class SignalRConfigTests
{
    [Fact]
    public void SignalRConfig_StoresHubUrl()
    {
        var config = new SignalRConfig("http://localhost:5000/flighthub");
        Assert.Equal("http://localhost:5000/flighthub", config.HubUrl);
    }
}
