using System.Net.Http.Headers;
using System.Text.Json;
using timbre.Models;

namespace timbre.Services;

internal enum TranscriptionHttpBodyKind
{
    MultipartFormData,
    RawAudio,
}

internal sealed class TranscriptionHttpRequestSpec
{
    public required string ProviderName { get; init; }

    public required Uri Endpoint { get; init; }

    public required string ApiKey { get; init; }

    public required string Model { get; init; }

    public string? Language { get; init; }

    public required TranscriptionHttpAuthorization Authorization { get; init; }

    public TranscriptionHttpBodyKind BodyKind { get; init; } = TranscriptionHttpBodyKind.MultipartFormData;

    public string FileFieldName { get; init; } = "file";

    public string FileName { get; init; } = "recording.wav";

    public string AudioContentType { get; init; } = "audio/wav";

    public string? ModelFormFieldName { get; init; } = "model";

    public string? LanguageFormFieldName { get; init; } = "language";

    public IReadOnlyList<KeyValuePair<string, string>> AdditionalFormFields { get; init; } = [];

    public Func<string, string?> TranscriptExtractor { get; init; } = TranscriptionHttpResponseParsers.ExtractRootText;
}

internal sealed record TranscriptionHttpAuthorization(
    string HeaderName,
    string? Scheme,
    string Value,
    bool UseTypedAuthorizationHeader)
{
    public static TranscriptionHttpAuthorization Bearer(string value)
    {
        return new TranscriptionHttpAuthorization("Authorization", "Bearer", value, UseTypedAuthorizationHeader: true);
    }

    public static TranscriptionHttpAuthorization SchemeHeader(string scheme, string value)
    {
        return new TranscriptionHttpAuthorization("Authorization", scheme, value, UseTypedAuthorizationHeader: true);
    }

    public static TranscriptionHttpAuthorization RawHeader(string headerName, string value)
    {
        return new TranscriptionHttpAuthorization(headerName, Scheme: null, value, UseTypedAuthorizationHeader: false);
    }

    public void Apply(HttpRequestMessage request)
    {
        var normalizedValue = Value.Trim();

        if (UseTypedAuthorizationHeader && string.Equals(HeaderName, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(Scheme))
            {
                throw new InvalidOperationException("A typed Authorization header requires a scheme.");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue(Scheme, normalizedValue);
            return;
        }

        var headerValue = string.IsNullOrWhiteSpace(Scheme)
            ? normalizedValue
            : $"{Scheme} {normalizedValue}";
        request.Headers.TryAddWithoutValidation(HeaderName, headerValue);
    }
}

internal sealed class TranscriptionHttpExecutor
{
    private readonly HttpClient _httpClient;

    public TranscriptionHttpExecutor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> TranscribeAsync(
        byte[] audioBytes,
        TranscriptionHttpRequestSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioBytes);
        ArgumentNullException.ThrowIfNull(spec);

        if (audioBytes.Length == 0)
        {
            throw new TranscriptionException("No audio was captured.", false);
        }

        if (string.IsNullOrWhiteSpace(spec.ApiKey))
        {
            throw new TranscriptionException($"The {spec.ProviderName} API key is missing.", false);
        }

        if (string.IsNullOrWhiteSpace(spec.Model))
        {
            throw new TranscriptionException($"The {spec.ProviderName} model is missing.", false);
        }

        try
        {
            using var request = CreateRequest(audioBytes, spec);
            spec.Authorization.Apply(request);

            DiagnosticsLogger.Info(
                $"{spec.ProviderName} transcription request starting. Endpoint={spec.Endpoint}, Model={spec.Model}, AudioBytes={audioBytes.Length}, Language='{spec.Language ?? "auto"}'.");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            DiagnosticsLogger.Info(
                $"{spec.ProviderName} transcription response received. Status={(int)response.StatusCode} {response.StatusCode}, BodyLength={responseBody.Length}.");

            if (!response.IsSuccessStatusCode)
            {
                throw new TranscriptionException(
                    TranscriptionHttpResponseParsers.ExtractErrorMessage(responseBody, spec.ProviderName, (int)response.StatusCode),
                    HttpStatusUtilities.IsTransient(response.StatusCode),
                    response.StatusCode);
            }

            var transcript = spec.TranscriptExtractor(responseBody);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw new TranscriptionException($"{spec.ProviderName} returned an empty transcription.", false);
            }

            return transcript.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException("The transcription request timed out.", true);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionException($"The transcription request could not reach {spec.ProviderName}.", true, null, exception);
        }
        catch (JsonException exception)
        {
            throw new TranscriptionException($"{spec.ProviderName} returned an unreadable transcription response.", true, null, exception);
        }
    }

    private static HttpRequestMessage CreateRequest(byte[] audioBytes, TranscriptionHttpRequestSpec spec)
    {
        return new HttpRequestMessage(HttpMethod.Post, spec.Endpoint)
        {
            Content = spec.BodyKind == TranscriptionHttpBodyKind.RawAudio
                ? CreateRawAudioContent(audioBytes, spec)
                : CreateMultipartContent(audioBytes, spec),
        };
    }

    private static HttpContent CreateRawAudioContent(byte[] audioBytes, TranscriptionHttpRequestSpec spec)
    {
        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse(spec.AudioContentType);
        return audioContent;
    }

    private static HttpContent CreateMultipartContent(byte[] audioBytes, TranscriptionHttpRequestSpec spec)
    {
        var form = new MultipartFormDataContent();

        if (!string.IsNullOrWhiteSpace(spec.ModelFormFieldName))
        {
            form.Add(new StringContent(spec.Model), spec.ModelFormFieldName);
        }

        foreach (var field in spec.AdditionalFormFields)
        {
            form.Add(new StringContent(field.Value), field.Key);
        }

        if (!string.IsNullOrWhiteSpace(spec.LanguageFormFieldName) && !string.IsNullOrWhiteSpace(spec.Language))
        {
            form.Add(new StringContent(spec.Language), spec.LanguageFormFieldName);
        }

        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse(spec.AudioContentType);
        form.Add(audioContent, spec.FileFieldName, spec.FileName);
        return form;
    }
}

internal static class TranscriptionHttpResponseParsers
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string? ExtractRootText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        return JsonErrorMessageExtractor.TryReadStringProperty(document.RootElement, "text", out var text) ? text : null;
    }

    public static string? ExtractDeepgramTranscript(string responseBody)
    {
        var response = JsonSerializer.Deserialize<DeepgramTranscriptionResponse>(responseBody, SerializerOptions);
        return TranscriptText.NormalizeWhitespace(response?.Results?.Channels?.FirstOrDefault()?.Alternatives?.FirstOrDefault()?.Transcript);
    }

    public static string ExtractErrorMessage(string responseBody, string providerName, int statusCode)
    {
        return JsonErrorMessageExtractor.Extract(responseBody, providerName, statusCode);
    }

    private sealed class DeepgramTranscriptionResponse
    {
        public DeepgramResultSection? Results { get; set; }
    }

    private sealed class DeepgramResultSection
    {
        public List<DeepgramChannel>? Channels { get; set; }
    }

    private sealed class DeepgramChannel
    {
        public List<DeepgramAlternative>? Alternatives { get; set; }
    }

    private sealed class DeepgramAlternative
    {
        public string? Transcript { get; set; }
    }
}
