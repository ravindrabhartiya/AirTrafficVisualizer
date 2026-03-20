namespace ATC.Tests;

using System.Net;

public sealed class RetryAfterHandlerTests
{
    [Fact]
    public async Task NormalResponse_PassesThrough()
    {
        var innerHandler = new TestHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var handler = CreateHandler(innerHandler);
        var client = new HttpClient(handler);

        var response = await client.GetAsync("http://test.local/api");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, innerHandler.CallCount);
    }

    [Fact]
    public async Task TooManyRequests_RetryAfterHeader_RetriesAfterDelay()
    {
        var retryResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retryResponse.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));

        var successResponse = new HttpResponseMessage(HttpStatusCode.OK);
        var innerHandler = new TestHandler(retryResponse, successResponse);
        var handler = CreateHandler(innerHandler);
        var client = new HttpClient(handler);

        var response = await client.GetAsync("http://test.local/api");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, innerHandler.CallCount);
    }

    private static ATC.TelemetryProducer.RetryAfterHandler CreateHandler(HttpMessageHandler inner)
    {
        var loggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<ATC.TelemetryProducer.RetryAfterHandler>();

        var handler = new ATC.TelemetryProducer.RetryAfterHandler(logger)
        {
            InnerHandler = inner
        };
        return handler;
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public int CallCount { get; private set; }

        public TestHandler(params HttpResponseMessage[] responses)
        {
            foreach (var r in responses) _responses.Enqueue(r);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            var response = _responses.Count > 0 ? _responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.OK);
            return Task.FromResult(response);
        }
    }
}
