using System.Threading.RateLimiting;
using ATC.TelemetryProducer;
using Confluent.Kafka;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// --- Kafka producer ---
builder.Services.AddSingleton(new ProducerConfig
{
    BootstrapServers = builder.Configuration.GetValue("Kafka:BootstrapServers", "localhost:9092"),
    Acks = Acks.Leader,
    LingerMs = 5
});

builder.Services.AddSingleton<IProducer<string, string>>(sp =>
{
    var config = sp.GetRequiredService<ProducerConfig>();
    return new ProducerBuilder<string, string>(config).Build();
});

// --- Redis (for last-call timestamp) ---
var redisConnection = builder.Configuration.GetValue("Redis:ConnectionString", "localhost:6379")!;
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnection));

// --- OpenSky credentials (from User Secrets / env vars) ---
var openSkyClientId = builder.Configuration.GetValue<string>("OpenSky:ClientId");
var openSkyClientSecret = builder.Configuration.GetValue<string>("OpenSky:ClientSecret");

// --- Rate limiter: authenticated users get ~4000 req/day, anonymous ~400 ---
var hasCredentials = !string.IsNullOrEmpty(openSkyClientId) && !string.IsNullOrEmpty(openSkyClientSecret);
var replenishSeconds = hasCredentials ? 10 : 22;

builder.Services.AddSingleton(new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
{
    TokenLimit = 1,
    ReplenishmentPeriod = TimeSpan.FromSeconds(replenishSeconds),
    TokensPerPeriod = 1,
    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    QueueLimit = 1,
    AutoReplenishment = true
}));

// --- HttpClient with 429-aware delegating handler ---
builder.Services.AddTransient<RetryAfterHandler>();

builder.Services.AddHttpClient("OpenSky", client =>
{
    client.BaseAddress = new Uri("https://opensky-network.org");
    client.Timeout = TimeSpan.FromSeconds(30);

    // Add Basic Auth if credentials are configured (via User Secrets / env vars)
    if (!string.IsNullOrEmpty(openSkyClientId) && !string.IsNullOrEmpty(openSkyClientSecret))
    {
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.ASCII.GetBytes($"{openSkyClientId}:{openSkyClientSecret}"));
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
    }
}).AddHttpMessageHandler<RetryAfterHandler>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
