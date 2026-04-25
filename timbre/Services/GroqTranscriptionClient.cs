using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class GroqTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri Endpoint = new("https://api.groq.com/openai/v1/audio/transcriptions");
    private static readonly IReadOnlyList<KeyValuePair<string, string>> AdditionalFormFields =
    [
        new("response_format", "json"),
    ];

    private readonly TranscriptionHttpExecutor _httpExecutor;

    public GroqTranscriptionClient(HttpClient httpClient)
    {
        _httpExecutor = new TranscriptionHttpExecutor(httpClient);
    }

    public Task<string> TranscribeAsync(
        byte[] audioBytes,
        string apiKey,
        string model,
        string? language,
        CancellationToken cancellationToken = default)
    {
        var resolvedModel = string.IsNullOrWhiteSpace(model)
            ? string.Empty
            : TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Groq, model);
        var requestLanguage = TranscriptionProviderCatalog.NormalizeRequestLanguage(TranscriptionProvider.Groq, language);

        return _httpExecutor.TranscribeAsync(
            audioBytes,
            new TranscriptionHttpRequestSpec
            {
                ProviderName = "Groq",
                Endpoint = Endpoint,
                ApiKey = apiKey,
                Model = resolvedModel,
                Language = requestLanguage,
                Authorization = TranscriptionHttpAuthorization.Bearer(apiKey),
                AdditionalFormFields = AdditionalFormFields,
            },
            cancellationToken);
    }
}
