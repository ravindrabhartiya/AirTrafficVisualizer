namespace ATC.Tests;

using ATC.TelemetryProducer;
using ATC.Shared;

public sealed class MockFlightGeneratorTests
{
    [Fact]
    public void GenerateFrame_Returns200Flights()
    {
        var gen = new MockFlightGenerator();
        var frame = gen.GenerateFrame();

        Assert.Equal(200, frame.Count);
    }

    [Fact]
    public void GenerateFrame_CustomCount()
    {
        var gen = new MockFlightGenerator(flightCount: 5);
        var frame = gen.GenerateFrame();

        Assert.Equal(5, frame.Count);
    }

    [Fact]
    public void GenerateFrame_AllHaveValidCoordinates()
    {
        var gen = new MockFlightGenerator(flightCount: 50);
        var frame = gen.GenerateFrame();

        foreach (var f in frame)
        {
            Assert.InRange(f.Latitude!.Value, -85, 85);
            Assert.InRange(f.Longitude!.Value, -180, 180);
            Assert.True(f.BaroAltitude > 0, $"Altitude should be positive, got {f.BaroAltitude}");
            Assert.False(string.IsNullOrEmpty(f.Icao24));
            Assert.False(string.IsNullOrEmpty(f.Callsign));
        }
    }

    [Fact]
    public void GenerateFrame_UniqueIcaoCodes()
    {
        var gen = new MockFlightGenerator(flightCount: 100);
        var frame = gen.GenerateFrame();

        var icaos = frame.Select(f => f.Icao24).ToList();
        Assert.Equal(icaos.Count, icaos.Distinct().Count());
    }

    [Fact]
    public void MultipleFrames_PositionsChange()
    {
        var gen = new MockFlightGenerator(flightCount: 5);
        var frame1 = gen.GenerateFrame();
        var pos1 = frame1.Select(f => (f.Latitude, f.Longitude)).ToList();

        var frame2 = gen.GenerateFrame();
        var pos2 = frame2.Select(f => (f.Latitude, f.Longitude)).ToList();

        // At least some positions should have changed
        bool anyChanged = pos1.Zip(pos2).Any(p => p.First != p.Second);
        Assert.True(anyChanged, "Positions should change between frames");
    }

    [Fact]
    public void GenerateFrame_VelocityInRange()
    {
        var gen = new MockFlightGenerator(flightCount: 50);
        var frame = gen.GenerateFrame();

        foreach (var f in frame)
        {
            Assert.InRange(f.Velocity!.Value, 100, 400); // Generous bounds
        }
    }

    [Fact]
    public void GenerateFrame_HasLastUpdate()
    {
        var gen = new MockFlightGenerator(flightCount: 5);
        var frame = gen.GenerateFrame();

        foreach (var f in frame)
        {
            Assert.True(f.LastUpdate > 0);
        }
    }
}
