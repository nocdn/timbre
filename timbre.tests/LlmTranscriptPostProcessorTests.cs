using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using timbre.Models;
using timbre.Services;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class LlmTranscriptPostProcessorTests
{
    [Fact]
    public async Task CleanTranscriptAsync_UsesCerebrasChatCompletionsAndReturnsCleanedText()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "{\"cleaned_text\":\"This is the corrected sentence.\"}"
                          }
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json"),
            });
        var postProcessor = new LlmTranscriptPostProcessor(new HttpClient(handler));
        var settings = new AppSettings
        {
            LlmPostProcessingEnabled = true,
            LlmPostProcessingProvider = LlmPostProcessingProvider.Cerebras,
            CerebrasApiKey = " cerebras-key ",
            CerebrasModel = "gpt-oss-120b",
            LlmPostProcessingPrompt = "Remove filler words.",
        };

        // Act
        var result = await postProcessor.CleanTranscriptAsync("um this is the corrected sentence", settings);

        // Assert
        result.Should().Be("This is the corrected sentence.");
        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Be(new Uri("https://api.cerebras.ai/v1/chat/completions"));
        request.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "cerebras-key"));
        request.JsonBody.GetProperty("model").GetString().Should().Be("gpt-oss-120b");
        request.JsonBody.GetProperty("response_format").GetProperty("type").GetString().Should().Be("json_object");
        request.JsonBody.GetProperty("messages")[1].GetProperty("content").GetString().Should().Be("Remove filler words.");
    }

    [Fact]
    public async Task CleanTranscriptAsync_UsesGroqChatCompletionsAndFallsBackToPlainTextContent()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "Cleaned transcript"
                          }
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json"),
            });
        var postProcessor = new LlmTranscriptPostProcessor(new HttpClient(handler));
        var settings = new AppSettings
        {
            LlmPostProcessingEnabled = true,
            LlmPostProcessingProvider = LlmPostProcessingProvider.Groq,
            LlmGroqApiKey = "groq-key",
            LlmGroqModel = "openai/gpt-oss-120b",
        };

        // Act
        var result = await postProcessor.CleanTranscriptAsync("uh cleaned transcript", settings);

        // Assert
        result.Should().Be("Cleaned transcript");
        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.RequestUri.Should().Be(new Uri("https://api.groq.com/openai/v1/chat/completions"));
        request.JsonBody.GetProperty("model").GetString().Should().Be("openai/gpt-oss-120b");
    }

    [Fact]
    public async Task CleanTranscriptAsync_WhenPromptMissing_UsesDefaultPrompt()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "{\"cleaned_text\":\"hello\"}"
                          }
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json"),
            });
        var postProcessor = new LlmTranscriptPostProcessor(new HttpClient(handler));
        var settings = new AppSettings
        {
            LlmPostProcessingEnabled = true,
            LlmPostProcessingProvider = LlmPostProcessingProvider.Cerebras,
            CerebrasApiKey = "cerebras-key",
            LlmPostProcessingPrompt = " ",
        };

        // Act
        await postProcessor.CleanTranscriptAsync("hello", settings);

        // Assert
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].JsonBody.GetProperty("messages")[1].GetProperty("content").GetString().Should().Be(LlmPostProcessingCatalog.DefaultPrompt);
    }

    [Fact]
    public async Task CleanTranscriptAsync_WhenApiReturnsTransientError_ThrowsTranscriptionException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"message":"Slow down"}}""", Encoding.UTF8, "application/json"),
            });
        var postProcessor = new LlmTranscriptPostProcessor(new HttpClient(handler));
        var settings = new AppSettings
        {
            LlmPostProcessingEnabled = true,
            LlmPostProcessingProvider = LlmPostProcessingProvider.Groq,
            LlmGroqApiKey = "groq-key",
        };

        // Act
        var action = () => postProcessor.CleanTranscriptAsync("test", settings);

        // Assert
        var exception = await Assert.ThrowsAsync<TranscriptionException>(action);
        exception.Message.Should().Be("Slow down");
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

            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                JsonDocument.Parse(body).RootElement.Clone()));

            return _responseFactory(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        AuthenticationHeaderValue? Authorization,
        JsonElement JsonBody);
}
