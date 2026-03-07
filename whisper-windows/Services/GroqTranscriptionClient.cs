using System.Net.Http.Headers;
using System.Text.Json;

namespace whisper_windows.Services;

public sealed class GroqTranscriptionClient
{
    private static readonly Uri Endpoint = new("https://api.groq.com/openai/v1/audio/transcriptions");
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    public async Task<string> TranscribeAsync(byte[] audioBytes, string apiKey, string model, CancellationToken cancellationToken = default)
    {
        if (audioBytes.Length == 0)
        {
            throw new InvalidOperationException("No audio was captured.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("The Groq API key is missing.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("The Groq model is missing.");
        }

        using var form = new MultipartFormDataContent();
        using var audioContent = new ByteArrayContent(audioBytes);

        audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
        form.Add(audioContent, "file", "recording.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = form,
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        DiagnosticsLogger.Info(
            $"Groq request starting. Endpoint={Endpoint}, Model={model}, ResponseFormat=json, AudioBytes={audioBytes.Length}.");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        DiagnosticsLogger.Info(
            $"Groq response received. Status={(int)response.StatusCode} {response.StatusCode}, Body={Truncate(responseBody, 2000)}");

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractErrorMessage(responseBody, (int)response.StatusCode));
        }

        var transcription = JsonSerializer.Deserialize<TranscriptionResponse>(responseBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        if (string.IsNullOrWhiteSpace(transcription?.Text))
        {
            throw new InvalidOperationException("Groq returned an empty transcription.");
        }

        return transcription.Text.Trim();
    }

    private static string Truncate(string value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
        {
            return value;
        }

        return value[..maximumLength];
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

    private sealed class TranscriptionResponse
    {
        public string? Text { get; set; }
    }
}
