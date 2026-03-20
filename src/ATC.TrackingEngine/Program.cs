using ATC.Shared;
using ATC.TrackingEngine;
using Confluent.Kafka;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

var kafkaBootstrap = builder.Configuration.GetValue("Kafka:BootstrapServers", "localhost:9092")!;
var redisConnection = builder.Configuration.GetValue("Redis:ConnectionString", "localhost:6379")!;
var signalRUrl = builder.Configuration.GetValue("SignalR:HubUrl", "http://localhost:5000/flighthub")!;
var dbPath = builder.Configuration.GetValue("Sqlite:DbPath", "flights.db")!;

builder.Services.AddSingleton(new ConsumerConfig
{
    BootstrapServers = kafkaBootstrap,
    GroupId = "tracking-engine",
    AutoOffsetReset = AutoOffsetReset.Latest,
    EnableAutoCommit = true
});

builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConnection));
builder.Services.AddSingleton<IFlightSnapshotStore>(new SqliteFlightSnapshotStore(dbPath));

builder.Services.AddSingleton(new SignalRConfig(signalRUrl));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
