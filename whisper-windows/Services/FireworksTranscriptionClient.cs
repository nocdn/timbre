using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using whisper_windows.Interfaces;
using whisper_windows.Models;

namespace whisper_windows.Services;

public sealed class FireworksTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri ProdEndpoint = new("https://audio-prod.api.fireworks.ai/v1/audio/transcriptions");
    private static readonly Uri TurboEndpoint = new("https://audio-turbo.api.fireworks.ai/v1/audio/transcriptions");

    private readonly HttpClient _httpClient;

    public FireworksTranscriptionClient(HttpClient httpClient)
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
            throw new TranscriptionException("The Fireworks API key is missing.", false);
        }

        var normalizedModel = string.IsNullOrWhiteSpace(model) ? "whisper-v3-turbo" : model.Trim();
        var endpoint = normalizedModel == "whisper-v3" ? ProdEndpoint : TurboEndpoint;

        try
        {
            using var form = new MultipartFormDataContent();
            using var audioContent = new ByteArrayContent(audioBytes);

            audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
            form.Add(audioContent, "file", "recording.wav");
            form.Add(new StringContent(normalizedModel), "model");
            form.Add(new StringContent("json"), "response_format");

            var normalizedLanguage = NormalizeLanguage(language);
            if (!string.IsNullOrWhiteSpace(normalizedLanguage))
            {
                form.Add(new StringContent(normalizedLanguage), "language");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = form,
            };

            request.Headers.Add("Authorization", apiKey.Trim());

            DiagnosticsLogger.Info(
                $"Fireworks request starting. Endpoint={endpoint}, Model={normalizedModel}, ResponseFormat=json, AudioBytes={audioBytes.Length}, Language='{normalizedLanguage ?? "auto"}'.");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            DiagnosticsLogger.Info(
                $"Fireworks response received. Status={(int)response.StatusCode} {response.StatusCode}, BodyLength={responseBody.Length}.");

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
                throw new TranscriptionException("Fireworks returned an empty transcription.", false);
            }

            return transcription.Text.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException("The transcription request timed out.", true);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionException("The transcription request could not reach Fireworks.", true, null, exception);
        }
    }

    private static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "en";
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized == "auto" ? null : normalized;
    }

    private static string ExtractErrorMessage(string responseBody, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);

            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errorElement.GetString()))
                {
                    return errorElement.GetString()!;
                }

                if (errorElement.TryGetProperty("message", out var nestedMessage) &&
                    nestedMessage.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nestedMessage.GetString()))
                {
                    return nestedMessage.GetString()!;
                }
            }

            if (document.RootElement.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(messageElement.GetString()))
            {
                return messageElement.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return $"Fireworks returned HTTP {statusCode}.";
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
