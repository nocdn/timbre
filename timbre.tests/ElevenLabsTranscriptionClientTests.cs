using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using timbre.Models;
using timbre.Services;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class ElevenLabsTranscriptionClientTests
{
    [Fact]
    public async Task TranscribeAsync_SendsMultipartRequestAndReturnsTranscript()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"Hello from Scribe"}""", Encoding.UTF8, "application/json"),
            });
        var client = new ElevenLabsTranscriptionClient(new HttpClient(handler));

        // Act
        var result = await client.TranscribeAsync(
            Encoding.ASCII.GetBytes("fake wav"),
            " test-key ",
            "scribe_v2",
            "ENG");

        // Assert
        result.Should().Be("Hello from Scribe");
        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Be(new Uri("https://api.elevenlabs.io/v1/speech-to-text"));
        request.ApiKey.Should().Be("test-key");
        request.Body.Should().Contain("name=model_id");
        request.Body.Should().Contain("scribe_v2");
        request.Body.Should().Contain("name=language_code");
        request.Body.Should().Contain("eng");
        request.Body.Should().Contain("name=file");
        request.Body.Should().Contain("filename=recording.wav");
        request.Body.IndexOf("name=model_id", StringComparison.Ordinal).Should().BeLessThan(
            request.Body.IndexOf("name=language_code", StringComparison.Ordinal));
        request.Body.IndexOf("name=language_code", StringComparison.Ordinal).Should().BeLessThan(
            request.Body.IndexOf("name=file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TranscribeAsync_WhenLanguageIsAuto_OmitsLanguageCodeAndUsesDefaultModel()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"Hi"}""", Encoding.UTF8, "application/json"),
            });
        var client = new ElevenLabsTranscriptionClient(new HttpClient(handler));

        // Act
        await client.TranscribeAsync(
            Encoding.ASCII.GetBytes("fake wav"),
            "test-key",
            "",
            "auto");

        // Assert
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Body.Should().Contain("name=model_id");
        handler.Requests[0].Body.Should().Contain("scribe_v2");
        handler.Requests[0].Body.Should().NotContain("name=language_code");
    }

    [Fact]
    public async Task TranscribeAsync_WhenRealtimeModelIsSelected_ThrowsNonTransientException()
    {
        // Arrange
        var client = new ElevenLabsTranscriptionClient(new HttpClient(new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK))));

        // Act
        var action = () => client.TranscribeAsync(
            Encoding.ASCII.GetBytes("fake wav"),
            "test-key",
            "scribe_v2_realtime",
            "en");

        // Assert
        var exception = await Assert.ThrowsAsync<TranscriptionException>(action);
        exception.Message.Should().Contain("requires streaming mode");
        exception.IsTransient.Should().BeFalse();
    }

    [Fact]
    public async Task TranscribeAsync_WhenApiReturnsTransientError_ThrowsTranscriptionException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"detail":{"message":"Rate limited"}}""", Encoding.UTF8, "application/json"),
            });
        var client = new ElevenLabsTranscriptionClient(new HttpClient(handler));

        // Act
        var action = () => client.TranscribeAsync(
            Encoding.ASCII.GetBytes("fake wav"),
            "test-key",
            "scribe_v2",
            "en");

        // Assert
        var exception = await Assert.ThrowsAsync<TranscriptionException>(action);
        exception.Message.Should().Be("Rate limited");
        exception.IsTransient.Should().BeTrue();
        exception.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var apiKey = request.Headers.TryGetValues("xi-api-key", out var values)
                ? values.SingleOrDefault()
                : null;

            Requests.Add(new CapturedRequest(request.Method, request.RequestUri, request.Headers.Authorization, apiKey, body));
            return _responseFactory(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        AuthenticationHeaderValue? Authorization,
        string? ApiKey,
        string Body);
}
