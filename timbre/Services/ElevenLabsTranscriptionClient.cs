using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class ElevenLabsTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri Endpoint = new("https://api.elevenlabs.io/v1/speech-to-text");
    private const string DefaultModel = "scribe_v2";
    private const string RealtimeModel = "scribe_v2_realtime";

    private readonly HttpClient _httpClient;

    public ElevenLabsTranscriptionClient(HttpClient httpClient)
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
            throw new TranscriptionException("The ElevenLabs API key is missing.", false);
        }

        var resolvedModel = ResolveModel(model);
        if (string.Equals(resolvedModel, RealtimeModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new TranscriptionException("ElevenLabs Scribe v2 Realtime requires streaming mode. Select Scribe v2 for non-streaming transcription.", false);
        }

        var normalizedLanguage = NormalizeLanguage(language);

        try
        {
            using var form = new MultipartFormDataContent();
            using var audioContent = new ByteArrayContent(audioBytes);

            form.Add(new StringContent(resolvedModel), "model_id");

            if (!string.IsNullOrWhiteSpace(normalizedLanguage))
            {
                form.Add(new StringContent(normalizedLanguage), "language_code");
            }

            audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
            form.Add(audioContent, "file", "recording.wav");

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = form,
            };

            request.Headers.TryAddWithoutValidation("xi-api-key", apiKey.Trim());

            DiagnosticsLogger.Info(
                $"ElevenLabs transcription request starting. Endpoint={Endpoint}, Model={resolvedModel}, AudioBytes={audioBytes.Length}, Language='{normalizedLanguage ?? "auto"}'.");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            DiagnosticsLogger.Info(
                $"ElevenLabs transcription response received. Status={(int)response.StatusCode} {response.StatusCode}, BodyLength={responseBody.Length}.");

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
                throw new TranscriptionException("ElevenLabs returned an empty transcription.", false);
            }

            return transcription.Text.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException("The transcription request timed out.", true);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionException("The transcription request could not reach ElevenLabs.", true, null, exception);
        }
    }

    private static string ResolveModel(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DefaultModel : value.Trim();
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

            if (TryReadStringProperty(root, "message", out var message))
            {
                return message;
            }

            if (TryReadStringProperty(root, "detail", out var detail))
            {
                return detail;
            }

            if (root.TryGetProperty("detail", out var detailObject) && detailObject.ValueKind == JsonValueKind.Object)
            {
                if (TryReadStringProperty(detailObject, "message", out var detailMessage))
                {
                    return detailMessage;
                }

                if (TryReadStringProperty(detailObject, "msg", out var detailMsg))
                {
                    return detailMsg;
                }
            }

            if (root.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errorElement.GetString()))
                {
                    return errorElement.GetString()!;
                }

                if (errorElement.ValueKind == JsonValueKind.Object)
                {
                    if (TryReadStringProperty(errorElement, "message", out var nestedMessage))
                    {
                        return nestedMessage;
                    }

                    if (TryReadStringProperty(errorElement, "detail", out var nestedDetail))
                    {
                        return nestedDetail;
                    }
                }
            }

            if (root.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.Array)
            {
                var firstMessage = detailElement.EnumerateArray()
                    .Select(item => TryReadStringProperty(item, "msg", out var itemMessage) ? itemMessage : null)
                    .FirstOrDefault(itemMessage => !string.IsNullOrWhiteSpace(itemMessage));

                if (!string.IsNullOrWhiteSpace(firstMessage))
                {
                    return firstMessage!;
                }
            }
        }
        catch (JsonException)
        {
        }

        return $"ElevenLabs returned HTTP {statusCode}.";
    }

    private static bool TryReadStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!;
        return true;
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
