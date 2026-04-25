using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class MistralTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri Endpoint = new("https://api.mistral.ai/v1/audio/transcriptions");
    private const string DefaultModel = TranscriptionProviderCatalog.DefaultMistralNonStreamingModel;

    private readonly HttpClient _httpClient;

    public MistralTranscriptionClient(HttpClient httpClient)
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
            throw new TranscriptionException("The Mistral API key is missing.", false);
        }

        var resolvedModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        var normalizedLanguage = NormalizeLanguage(language);

        try
        {
            using var form = new MultipartFormDataContent();
            using var audioContent = new ByteArrayContent(audioBytes);

            audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
            form.Add(audioContent, "file", "recording.wav");
            form.Add(new StringContent(resolvedModel), "model");

            if (!string.IsNullOrWhiteSpace(normalizedLanguage))
            {
                form.Add(new StringContent(normalizedLanguage), "language");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = form,
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            DiagnosticsLogger.Info(
                $"Mistral transcription request starting. Endpoint={Endpoint}, Model={resolvedModel}, AudioBytes={audioBytes.Length}, Language='{normalizedLanguage ?? "auto"}'.");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            DiagnosticsLogger.Info(
                $"Mistral transcription response received. Status={(int)response.StatusCode} {response.StatusCode}, BodyLength={responseBody.Length}.");

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
                throw new TranscriptionException("Mistral returned an empty transcription.", false);
            }

            return transcription.Text.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException("The transcription request timed out.", true);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionException("The transcription request could not reach Mistral.", true, null, exception);
        }
    }

    private static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized == "auto" ? null : normalized;
    }

    private static string ExtractErrorMessage(string responseBody, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errorElement.GetString()))
                {
                    return errorElement.GetString()!;
                }

                if (errorElement.ValueKind == JsonValueKind.Object)
                {
                    if (errorElement.TryGetProperty("message", out var nestedMessage) &&
                        nestedMessage.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(nestedMessage.GetString()))
                    {
                        return nestedMessage.GetString()!;
                    }
                }
            }

            if (root.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(messageElement.GetString()))
            {
                return messageElement.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return $"Mistral returned HTTP {statusCode}.";
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
