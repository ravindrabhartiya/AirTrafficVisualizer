namespace ATC.Tests;

using ATC.DashboardApi;
using ATC.Shared;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class FlightHubTests
{
    [Fact]
    public void FlightHub_CanBeInstantiated()
    {
        var hub = new FlightHub(NullLogger<FlightHub>.Instance);

        Assert.NotNull(hub);
    }
}
