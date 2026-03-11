using System.Net;
using System.Text;
using System.Text.Json;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class DeepgramTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri Endpoint = new("https://api.deepgram.com/v1/listen");

    private readonly HttpClient _httpClient;

    public DeepgramTranscriptionClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> TranscribeAsync(byte[] audioBytes, string apiKey, string model, string? language, CancellationToken cancellationToken = default)
    {
        if (audioBytes.Length == 0)
        {
            throw new TranscriptionException("No audio was captured.", false);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranscriptionException("The Deepgram API key is missing.", false);
        }

        var normalizedModel = NormalizeModel(model);
        if (normalizedModel.StartsWith("flux", StringComparison.OrdinalIgnoreCase))
        {
            throw new TranscriptionException("Deepgram Flux requires streaming mode. Turn on streaming in Deepgram settings to use Flux.", false);
        }

        var normalizedLanguage = NormalizeLanguage(language);
        var endpoint = BuildEndpoint(normalizedModel, normalizedLanguage);

        try
        {
            using var audioContent = new ByteArrayContent(audioBytes);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = audioContent,
            };

            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            request.Headers.TryAddWithoutValidation("Authorization", $"Token {apiKey.Trim()}");

            DiagnosticsLogger.Info(
                $"Deepgram pre-recorded request starting. Endpoint={endpoint}, Model={normalizedModel}, AudioBytes={audioBytes.Length}, Language='{normalizedLanguage}'.");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            DiagnosticsLogger.Info(
                $"Deepgram pre-recorded response received. Status={(int)response.StatusCode} {response.StatusCode}, BodyLength={responseBody.Length}.");

            if (!response.IsSuccessStatusCode)
            {
                throw new TranscriptionException(
                    ExtractErrorMessage(responseBody, (int)response.StatusCode),
                    IsTransientStatusCode(response.StatusCode),
                    response.StatusCode);
            }

            var transcription = JsonSerializer.Deserialize<TranscriptionResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            var transcript = NormalizeTranscriptText(transcription?.Results?.Channels?.FirstOrDefault()?.Alternatives?.FirstOrDefault()?.Transcript);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw new TranscriptionException("Deepgram returned an empty transcription.", false);
            }

            return transcript;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException("The transcription request timed out.", true);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionException("The transcription request could not reach Deepgram.", true, null, exception);
        }
    }

    private static Uri BuildEndpoint(string model, string language)
    {
        var query = new StringBuilder();
        AppendQuery(query, "model", model);
        AppendQuery(query, "language", language);
        AppendQuery(query, "smart_format", "true");

        return new UriBuilder(Endpoint) { Query = query.ToString() }.Uri;
    }

    private static void AppendQuery(StringBuilder builder, string key, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append('&');
        }

        builder.Append(Uri.EscapeDataString(key));
        builder.Append('=');
        builder.Append(Uri.EscapeDataString(value));
    }

    private static string NormalizeModel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "nova-3";
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "en";
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeTranscriptText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string ExtractErrorMessage(string responseBody, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (root.TryGetProperty("err_msg", out var legacyMessage) &&
                legacyMessage.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(legacyMessage.GetString()))
            {
                return legacyMessage.GetString()!;
            }

            if (root.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(messageElement.GetString()))
            {
                return messageElement.GetString()!;
            }

            if (root.TryGetProperty("details", out var detailsElement) &&
                detailsElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(detailsElement.GetString()))
            {
                return detailsElement.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return $"Deepgram returned HTTP {statusCode}.";
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var numericStatusCode = (int)statusCode;
        return numericStatusCode == 408 || numericStatusCode == 429 || numericStatusCode >= 500;
    }

    private sealed class TranscriptionResponse
    {
        public ResultSection? Results { get; set; }
    }

    private sealed class ResultSection
    {
        public List<Channel>? Channels { get; set; }
    }

    private sealed class Channel
    {
        public List<Alternative>? Alternatives { get; set; }
    }

    private sealed class Alternative
    {
        public string? Transcript { get; set; }
    }
}
