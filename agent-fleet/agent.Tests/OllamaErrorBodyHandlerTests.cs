using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AgentFleet.Tests;

public sealed class OllamaErrorBodyHandlerTests
{
    private sealed class Respond(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class ReadTrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) { WasRead = true; return base.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { WasRead = true; return base.Read(buffer); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { WasRead = true; return base.ReadAsync(buffer, cancellationToken); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { WasRead = true; return base.ReadAsync(buffer, offset, count, cancellationToken); }
    }

    private static HttpClient Client(HttpResponseMessage response, CollectingLogger logger) =>
        new(new OllamaErrorBodyHandler(new Respond(response), logger, "hub/model"));

    [Fact]
    public async Task Logs_a_chat_error_and_restores_its_body_and_status()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"boom\"}", Encoding.UTF8, "application/json")
        };
        var logger = new CollectingLogger();
        using HttpClient client = Client(response, logger);

        using HttpResponseMessage actual = await client.GetAsync("http://ollama.example.test/api/chat");

        Assert.Equal(HttpStatusCode.InternalServerError, actual.StatusCode);
        Assert.Equal("{\"error\":\"boom\"}", await actual.Content.ReadAsStringAsync());
        Assert.Equal("application/json; charset=utf-8", actual.Content.Headers.ContentType?.ToString());
        string message = Assert.Single(logger.Messages);
        Assert.Contains("hub/model", message);
        Assert.Contains("500", message);
        Assert.Contains("/api/chat", message);
        Assert.Contains("{\"error\":\"boom\"}", message);
    }

    [Fact]
    public async Task Leaves_a_successful_streaming_response_unread_and_unlogged()
    {
        var stream = new ReadTrackingStream(Encoding.UTF8.GetBytes("stream"));
        var content = new StreamContent(stream);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        var logger = new CollectingLogger();
        using HttpClient client = Client(response, logger);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://ollama.example.test/api/chat");

        using HttpResponseMessage actual = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Same(content, actual.Content);
        Assert.False(stream.WasRead);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task Does_not_log_errors_outside_chat_or_generate()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("missing") };
        var logger = new CollectingLogger();
        using HttpClient client = Client(response, logger);

        using HttpResponseMessage actual = await client.GetAsync("http://ollama.example.test/api/show");

        Assert.Equal(HttpStatusCode.NotFound, actual.StatusCode);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task Truncates_the_logged_body_to_1000_characters()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(new string('x', 1000) + "z")
        };
        var logger = new CollectingLogger();
        using HttpClient client = Client(response, logger);

        using HttpResponseMessage actual = await client.GetAsync("http://ollama.example.test/api/generate");

        string fullBody = await actual.Content.ReadAsStringAsync();
        string message = Assert.Single(logger.Messages);
        Assert.Equal(1001, fullBody.Length);
        Assert.EndsWith(new string('x', 1000), message);
        Assert.DoesNotContain("z", message);
    }
}
