using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class CohereTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri Endpoint = new("https://api.cohere.com/v2/audio/transcriptions");
    private const string DefaultModel = TranscriptionProviderCatalog.DefaultCohereModel;

    private readonly HttpClient _httpClient;

    public CohereTranscriptionClient(HttpClient httpClient)
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
            throw new TranscriptionException("The Cohere API key is missing.", false);
        }

        var resolvedModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        var resolvedLanguage = NormalizeLanguage(language);

        try
        {
            using var form = new MultipartFormDataContent();
            using var audioContent = new ByteArrayContent(audioBytes);

            form.Add(new StringContent(resolvedModel), "model");
            form.Add(new StringContent(resolvedLanguage), "language");
            audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
            form.Add(audioContent, "file", "recording.wav");

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = form,
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            DiagnosticsLogger.Info(
                $"Cohere transcription request starting. Endpoint={Endpoint}, Model={resolvedModel}, AudioBytes={audioBytes.Length}, Language='{resolvedLanguage}'.");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            DiagnosticsLogger.Info(
                $"Cohere transcription response received. Status={(int)response.StatusCode} {response.StatusCode}, BodyLength={responseBody.Length}.");

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

            if (string.IsNullOrWhiteSpace(transcription?.Text))
            {
                throw new TranscriptionException("Cohere returned an empty transcription.", false);
            }

            return transcription.Text.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException("The transcription request timed out.", true);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionException("The transcription request could not reach Cohere.", true, null, exception);
        }
    }

    private static string NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "en";
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string ExtractErrorMessage(string responseBody, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (root.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(messageElement.GetString()))
            {
                return messageElement.GetString()!;
            }

            if (root.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errorElement.GetString()))
                {
                    return errorElement.GetString()!;
                }

                if (errorElement.ValueKind == JsonValueKind.Object &&
                    errorElement.TryGetProperty("message", out var nestedMessage) &&
                    nestedMessage.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nestedMessage.GetString()))
                {
                    return nestedMessage.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
        }

        return $"Cohere returned HTTP {statusCode}.";
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var numericStatusCode = (int)statusCode;
        return numericStatusCode == 408 || numericStatusCode == 429 || numericStatusCode >= 500;
    }

    private sealed class TranscriptionResponse
    {
        public string? Text { get; set; }
    }
}
