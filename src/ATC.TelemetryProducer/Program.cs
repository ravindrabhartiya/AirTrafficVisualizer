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

// --- Rate limiter: 1 token per 22 s ≈ 3927 requests/day (under the 4000 limit) ---
builder.Services.AddSingleton(new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
{
    TokenLimit = 1,
    ReplenishmentPeriod = TimeSpan.FromSeconds(22),
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
}).AddHttpMessageHandler<RetryAfterHandler>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
