using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using timbre.Services;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class LlmModelCatalogClientTests
{
    [Fact]
    public async Task FetchCerebrasModelsAsync_ReturnsAllModelIds()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "data": [
                        { "id": "qwen-3-235b-a22b-instruct-2507" },
                        { "id": "gpt-oss-120b" },
                        { "id": "llama3.1-8b" }
                      ]
                    }
                    """, Encoding.UTF8, "application/json"),
            });
        var client = new LlmModelCatalogClient(new HttpClient(handler));

        // Act
        var models = await client.FetchCerebrasModelsAsync(" cerebras-key ");

        // Assert
        models.Should().Equal("qwen-3-235b-a22b-instruct-2507", "gpt-oss-120b", "llama3.1-8b");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri.Should().Be(new Uri("https://api.cerebras.ai/v1/models"));
        handler.Requests[0].Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "cerebras-key"));
    }

    [Fact]
    public async Task FetchGroqModelsAsync_FiltersOutNonChatModels()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "data": [
                        { "id": "openai/gpt-oss-120b" },
                        { "id": "whisper-large-v3-turbo" },
                        { "id": "groq/compound" },
                        { "id": "meta-llama/llama-4-scout-17b-16e-instruct" },
                        { "id": "openai/gpt-oss-safeguard-20b" }
                      ]
                    }
                    """, Encoding.UTF8, "application/json"),
            });
        var client = new LlmModelCatalogClient(new HttpClient(handler));

        // Act
        var models = await client.FetchGroqModelsAsync("groq-key");

        // Assert
        models.Should().Equal("openai/gpt-oss-120b", "meta-llama/llama-4-scout-17b-16e-instruct");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri.Should().Be(new Uri("https://api.groq.com/openai/v1/models"));
    }

    [Fact]
    public async Task FetchGroqModelsAsync_WhenApiReturnsError_ThrowsInvalidOperationException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"message":"Rate limited"}}""", Encoding.UTF8, "application/json"),
            });
        var client = new LlmModelCatalogClient(new HttpClient(handler));

        // Act
        var action = () => client.FetchGroqModelsAsync("groq-key");

        // Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(action);
        exception.Message.Should().Be("Rate limited");
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.RequestUri, request.Headers.Authorization));
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed record CapturedRequest(
        Uri? RequestUri,
        AuthenticationHeaderValue? Authorization);
}
