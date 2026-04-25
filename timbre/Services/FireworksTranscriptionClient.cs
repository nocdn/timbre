using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class FireworksTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri ProdEndpoint = new("https://audio-prod.api.fireworks.ai/v1/audio/transcriptions");
    private static readonly Uri TurboEndpoint = new("https://audio-turbo.api.fireworks.ai/v1/audio/transcriptions");
    private static readonly IReadOnlyList<KeyValuePair<string, string>> AdditionalFormFields =
    [
        new("response_format", "json"),
    ];

    private readonly TranscriptionHttpExecutor _httpExecutor;

    public FireworksTranscriptionClient(HttpClient httpClient)
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
        var resolvedModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Fireworks, model);
        var endpoint = string.Equals(resolvedModel, "whisper-v3", StringComparison.OrdinalIgnoreCase)
            ? ProdEndpoint
            : TurboEndpoint;
        var requestLanguage = TranscriptionProviderCatalog.NormalizeRequestLanguage(TranscriptionProvider.Fireworks, language);

        return _httpExecutor.TranscribeAsync(
            audioBytes,
            new TranscriptionHttpRequestSpec
            {
                ProviderName = "Fireworks",
                Endpoint = endpoint,
                ApiKey = apiKey,
                Model = resolvedModel,
                Language = requestLanguage,
                Authorization = TranscriptionHttpAuthorization.RawHeader("Authorization", apiKey),
                AdditionalFormFields = AdditionalFormFields,
            },
            cancellationToken);
    }
}
