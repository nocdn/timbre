using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using timbre.Models;
using timbre.Services;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class AquaVoiceTranscriptionClientTests
{
    [Fact]
    public async Task TranscribeAsync_SendsMultipartRequestAndReturnsTranscript()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"Hello from AquaVoice"}""", Encoding.UTF8, "application/json"),
            });
        var client = new AquaVoiceTranscriptionClient(new HttpClient(handler));

        // Act
        var result = await client.TranscribeAsync(
            Encoding.ASCII.GetBytes("fake wav"),
            " test-key ",
            "avalon-v1-en",
            "EN");

        // Assert
        result.Should().Be("Hello from AquaVoice");
        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Be(new Uri("https://api.aquavoice.com/api/v1/audio/transcriptions"));
        request.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "test-key"));
        request.Body.Should().Contain("name=model");
        request.Body.Should().Contain("avalon-v1-en");
        request.Body.Should().Contain("name=language");
        request.Body.Should().Contain("en");
        request.Body.Should().Contain("name=file");
        request.Body.Should().Contain("filename=recording.wav");
        request.Body.IndexOf("name=file", StringComparison.Ordinal).Should().BeLessThan(
            request.Body.IndexOf("name=model", StringComparison.Ordinal));
        request.Body.IndexOf("name=model", StringComparison.Ordinal).Should().BeLessThan(
            request.Body.IndexOf("name=language", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TranscribeAsync_WhenLanguageMissing_UsesEnglish()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"Hi"}""", Encoding.UTF8, "application/json"),
            });
        var client = new AquaVoiceTranscriptionClient(new HttpClient(handler));

        // Act
        await client.TranscribeAsync(
            Encoding.ASCII.GetBytes("fake wav"),
            "test-key",
            "",
            null);

        // Assert
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Body.Should().Contain("avalon-v1-en");
        handler.Requests[0].Body.Should().Contain("name=language");
        handler.Requests[0].Body.Should().Contain("en");
    }

    [Fact]
    public async Task TranscribeAsync_WhenApiReturnsTransientError_ThrowsTranscriptionException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"message":"Too many requests"}""", Encoding.UTF8, "application/json"),
            });
        var client = new AquaVoiceTranscriptionClient(new HttpClient(handler));

        // Act
        var action = () => client.TranscribeAsync(
            Encoding.ASCII.GetBytes("fake wav"),
            "test-key",
            "avalon-v1-en",
            "en");

        // Assert
        var exception = await Assert.ThrowsAsync<TranscriptionException>(action);
        exception.Message.Should().Be("Too many requests");
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

            Requests.Add(new CapturedRequest(request.Method, request.RequestUri, request.Headers.Authorization, body));
            return _responseFactory(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        AuthenticationHeaderValue? Authorization,
        string Body);
}
