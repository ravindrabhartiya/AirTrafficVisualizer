namespace ATC.TelemetryProducer;

using ATC.Shared;

/// <summary>
/// Generates realistic mock flight telemetry when the OpenSky API is unavailable.
/// Simulates aircraft flying great-circle-like paths across Europe/US airspace.
/// </summary>
public sealed class MockFlightGenerator
{
    private readonly List<SimulatedFlight> _flights = new();
    private readonly Random _rng = new();

    public MockFlightGenerator(int flightCount = 200)
    {
        for (int i = 0; i < flightCount; i++)
        {
            _flights.Add(CreateRandomFlight(i));
        }
    }

    public IReadOnlyList<FlightTelemetry> GenerateFrame()
    {
        var result = new List<FlightTelemetry>(_flights.Count);

        foreach (var flight in _flights)
        {
            flight.Advance();

            result.Add(new FlightTelemetry
            {
                Icao24 = flight.Icao24,
                Callsign = flight.Callsign,
                OriginCountry = flight.OriginCountry,
                Latitude = flight.Latitude,
                Longitude = flight.Longitude,
                BaroAltitude = flight.Altitude,
                Velocity = flight.Velocity,
                TrueTrack = flight.Heading,
                VerticalRate = flight.VerticalRate,
                OnGround = false,
                LastUpdate = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        return result;
    }

    private SimulatedFlight CreateRandomFlight(int index)
    {
        // Spread starting positions across the continental US
        double lat = 30.0 + _rng.NextDouble() * 18.0;   // 30N - 48N
        double lon = -120.0 + _rng.NextDouble() * 55.0;  // 120W - 65W
        double altitude = 28000 + _rng.NextDouble() * 14000; // 28k - 42k ft
        double heading = _rng.NextDouble() * 360.0;
        double velocity = 180 + _rng.NextDouble() * 80;  // 180 - 260 m/s

        string icao = $"{index:X2}{_rng.Next(0x1000):X3}A";
        string callsign = $"ATC{index:D3}";

        // Occasionally cluster two flights near each other to trigger collision alerts
        if (index > 0 && index % 10 == 0)
        {
            var nearby = _flights[index - 1];
            lat = nearby.Latitude + (_rng.NextDouble() - 0.5) * 0.04;  // ~2-4km offset
            lon = nearby.Longitude + (_rng.NextDouble() - 0.5) * 0.04;
            altitude = nearby.Altitude + (_rng.NextDouble() - 0.5) * 600; // within 300ft
        }

        return new SimulatedFlight(icao, callsign, lat, lon, altitude, heading, velocity, _rng);
    }

    private sealed class SimulatedFlight
    {
        private readonly Random _rng;

        public string Icao24 { get; }
        public string Callsign { get; }
        public string OriginCountry => "MockLand";
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        public double Altitude { get; private set; }
        public double Heading { get; private set; }
        public double Velocity { get; private set; }
        public double VerticalRate { get; private set; }

        public SimulatedFlight(string icao24, string callsign,
            double lat, double lon, double alt, double heading, double velocity, Random rng)
        {
            Icao24 = icao24;
            Callsign = callsign;
            Latitude = lat;
            Longitude = lon;
            Altitude = alt;
            Heading = heading;
            Velocity = velocity;
            _rng = rng;
        }

        public void Advance()
        {
            // Small random heading drift
            Heading += (_rng.NextDouble() - 0.5) * 2.0;
            Heading = (Heading + 360.0) % 360.0;

            // Move ~velocity m/s worth of distance (call interval ~5s, scale to degrees)
            double distDeg = Velocity * 5.0 / 111_320.0; // rough m-to-deg at equator
            double headingRad = Heading * Math.PI / 180.0;

            Latitude += distDeg * Math.Cos(headingRad);
            Longitude += distDeg * Math.Sin(headingRad) / Math.Cos(Latitude * Math.PI / 180.0);

            // Keep within bounds
            Latitude = Math.Clamp(Latitude, -85, 85);
            Longitude = ((Longitude + 180) % 360) - 180;

            // Small altitude jitter
            VerticalRate = (_rng.NextDouble() - 0.5) * 200;
            Altitude = Math.Clamp(Altitude + VerticalRate * 0.1, 1000, 45000);

            // Small velocity jitter
            Velocity = Math.Clamp(Velocity + (_rng.NextDouble() - 0.5) * 5, 150, 300);
        }
    }
}
