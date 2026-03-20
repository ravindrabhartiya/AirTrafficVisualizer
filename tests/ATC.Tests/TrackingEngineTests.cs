namespace ATC.Tests;

using ATC.TrackingEngine;

public sealed class SignalRConfigTests
{
    [Fact]
    public void SignalRConfig_StoresHubUrl()
    {
        var config = new SignalRConfig("http://localhost:5000/flighthub");
        Assert.Equal("http://localhost:5000/flighthub", config.HubUrl);
    }
}
