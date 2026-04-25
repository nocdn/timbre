using System.Text;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class DeepgramTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri Endpoint = new("https://api.deepgram.com/v1/listen");

    private readonly TranscriptionHttpExecutor _httpExecutor;

    public DeepgramTranscriptionClient(HttpClient httpClient)
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
        var requestedModel = string.IsNullOrWhiteSpace(model)
            ? TranscriptionProviderCatalog.DefaultDeepgramNonStreamingModel
            : model.Trim().ToLowerInvariant();

        if (requestedModel.StartsWith("flux", StringComparison.OrdinalIgnoreCase))
        {
            throw new TranscriptionException("Deepgram Flux requires streaming mode. Turn on streaming in Deepgram settings to use Flux.", false);
        }

        var resolvedModel = TranscriptionProviderCatalog.NormalizeModel(
            TranscriptionProvider.Deepgram,
            requestedModel,
            streamingEnabled: false);
        var requestLanguage = TranscriptionProviderCatalog.NormalizeRequestLanguage(TranscriptionProvider.Deepgram, language)
            ?? TranscriptionProviderCatalog.Get(TranscriptionProvider.Deepgram).DefaultLanguage;
        var endpoint = BuildEndpoint(resolvedModel, requestLanguage);

        return _httpExecutor.TranscribeAsync(
            audioBytes,
            new TranscriptionHttpRequestSpec
            {
                ProviderName = "Deepgram",
                Endpoint = endpoint,
                ApiKey = apiKey,
                Model = resolvedModel,
                Language = requestLanguage,
                Authorization = TranscriptionHttpAuthorization.SchemeHeader("Token", apiKey),
                BodyKind = TranscriptionHttpBodyKind.RawAudio,
                TranscriptExtractor = TranscriptionHttpResponseParsers.ExtractDeepgramTranscript,
            },
            cancellationToken);
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
}
