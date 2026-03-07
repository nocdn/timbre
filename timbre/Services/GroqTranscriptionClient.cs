using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class GroqTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri Endpoint = new("https://api.groq.com/openai/v1/audio/transcriptions");

    private readonly HttpClient _httpClient;

    public GroqTranscriptionClient(HttpClient httpClient)
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
            throw new TranscriptionException("The Groq API key is missing.", false);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new TranscriptionException("The Groq model is missing.", false);
        }

        try
        {
            using var form = new MultipartFormDataContent();
            using var audioContent = new ByteArrayContent(audioBytes);

            audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
            form.Add(audioContent, "file", "recording.wav");
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent("json"), "response_format");

            var normalizedLanguage = NormalizeLanguage(language);
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
                $"Groq request starting. Endpoint={Endpoint}, Model={model}, ResponseFormat=json, AudioBytes={audioBytes.Length}, Language='{normalizedLanguage ?? "auto"}'.");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            DiagnosticsLogger.Info(
                $"Groq response received. Status={(int)response.StatusCode} {response.StatusCode}, BodyLength={responseBody.Length}.");

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
                throw new TranscriptionException("Groq returned an empty transcription.", false);
            }

            return transcription.Text.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException("The transcription request timed out.", true);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionException("The transcription request could not reach Groq.", true, null, exception);
        }
    }

    private static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "en";
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == "auto")
        {
            return null;
        }

        return normalized;
    }

    private static string ExtractErrorMessage(string responseBody, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);

            if (document.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.TryGetProperty("message", out var nestedMessage) &&
                nestedMessage.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(nestedMessage.GetString()))
            {
                return nestedMessage.GetString()!;
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

        return $"Groq returned HTTP {statusCode}.";
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
