using ATC.DashboardApi;
using ATC.Shared;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var redisConnection = builder.Configuration.GetValue("Redis:ConnectionString", "localhost:6379")!;
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnection));

builder.Services.AddSignalR();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              .SetIsOriginAllowed(_ => true);
    });
});

var app = builder.Build();

app.UseCors();
app.UseStaticFiles();

app.MapHub<FlightHub>("/flighthub");

app.MapGet("/radar", async (IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();

    // The geo key is a sorted set under the hood — get all members
    var members = await db.SortedSetRangeByRankAsync(Constants.RedisGeoKey);

    if (members is null || members.Length == 0)
        return Results.Ok(Array.Empty<FlightPosition>());

    var flights = new List<FlightPosition>();

    foreach (var member in members)
    {
        var icao = member.ToString();
        var hash = await db.HashGetAllAsync($"flight:{icao}");
        if (hash.Length == 0) continue;

        var dict = hash.ToDictionary(
            h => h.Name.ToString(),
            h => h.Value.ToString());

        flights.Add(new FlightPosition
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
        });
    }

    return Results.Ok(flights);
});

app.MapFallbackToFile("index.html");

// Route lookup: proxies to OpenSky /api/routes and caches in Redis for 1 hour
app.MapGet("/route/{callsign}", async (string callsign, IConnectionMultiplexer redis) =>
{
    // Sanitise input — callsigns are alphanumeric only
    var clean = new string(callsign.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToUpperInvariant();
    if (string.IsNullOrEmpty(clean))
        return Results.BadRequest("Invalid callsign");

    var db = redis.GetDatabase();
    var cacheKey = $"route:{clean}";

    // Check Redis cache first
    var cached = await db.StringGetAsync(cacheKey);
    if (cached.HasValue)
        return Results.Content(cached.ToString(), "application/json");

    // Fetch from OpenSky routes API
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    try
    {
        var resp = await http.GetAsync($"https://opensky-network.org/api/routes?callsign={clean}");
        if (!resp.IsSuccessStatusCode)
            return Results.Json(new { callsign = clean, route = Array.Empty<string>(), error = "not_found" });

        var json = await resp.Content.ReadAsStringAsync();
        await db.StringSetAsync(cacheKey, json, TimeSpan.FromHours(1));
        return Results.Content(json, "application/json");
    }
    catch
    {
        return Results.Json(new { callsign = clean, route = Array.Empty<string>(), error = "lookup_failed" });
    }
});

app.Run();
