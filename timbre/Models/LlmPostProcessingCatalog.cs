namespace timbre.Models;

public sealed class LlmProviderDefinition
{
    public LlmProviderDefinition(
        LlmPostProcessingProvider provider,
        string displayName,
        Uri chatCompletionsEndpoint,
        Uri modelsEndpoint,
        string defaultModel,
        IReadOnlyList<string> builtInModels,
        Func<string, bool>? modelFilter = null)
    {
        Provider = provider;
        DisplayName = displayName;
        ChatCompletionsEndpoint = chatCompletionsEndpoint;
        ModelsEndpoint = modelsEndpoint;
        DefaultModel = defaultModel;
        BuiltInModels = builtInModels;
        ModelFilter = modelFilter ?? (_ => true);
    }

    public LlmPostProcessingProvider Provider { get; }

    public string DisplayName { get; }

    public Uri ChatCompletionsEndpoint { get; }

    public Uri ModelsEndpoint { get; }

    public string DefaultModel { get; }

    public IReadOnlyList<string> BuiltInModels { get; }

    public Func<string, bool> ModelFilter { get; }
}

public sealed record LlmPostProcessingProviderSettings(
    LlmPostProcessingProvider Provider,
    string ApiKey,
    string Model)
{
    public LlmProviderDefinition Definition => LlmPostProcessingCatalog.Get(Provider);

    public string DisplayName => Definition.DisplayName;
}

public static class LlmPostProcessingCatalog
{
    public const LlmPostProcessingProvider DefaultProvider = LlmPostProcessingProvider.Cerebras;
    public const string DefaultCerebrasModel = "qwen-3-235b-a22b-instruct-2507";
    public const string DefaultGroqModel = "openai/gpt-oss-120b";

    public const string DefaultPrompt = """
        Clean this raw speech-to-text transcript so it reads naturally while preserving the speaker's meaning.

        Rules:
        - Remove filler words and hesitation sounds such as "um", "uh", "ah", "er", and obvious stutters.
        - Remove false starts, abandoned fragments, and speech disfluencies when the speaker restarts a thought.
        - If the speaker corrects themselves, keep only the corrected wording and remove the earlier mistaken wording.
        - Remove accidental duplicated words or phrases caused by speaking naturally, unless the repetition is clearly intentional emphasis.
        - Lightly fix punctuation, capitalization, and spacing so the transcript reads cleanly.
        - Correct obvious speech-to-text renderings of technical, product, company, API, acronym, and special terms to their conventional spelling, capitalization, and spacing when the intended term is clear.
        - Preserve the original meaning, detail, tone, names, numbers, and technical terms.
        - Do not summarize, add new information, or rewrite the transcript into a different style.
        - Keep the transcript in the same language as the input.
        """;

    public static IReadOnlyList<string> CerebrasModels { get; } =
    [
        DefaultCerebrasModel,
        "llama3.1-8b",
        "gpt-oss-120b",
        "zai-glm-4.7",
    ];

    public static IReadOnlyList<string> GroqModels { get; } =
    [
        DefaultGroqModel,
        "openai/gpt-oss-20b",
        "llama-3.3-70b-versatile",
        "llama-3.1-8b-instant",
        "meta-llama/llama-4-scout-17b-16e-instruct",
        "qwen/qwen3-32b",
    ];

    public static IReadOnlyList<LlmProviderDefinition> Providers { get; } =
    [
        new(
            LlmPostProcessingProvider.Cerebras,
            "Cerebras",
            new Uri("https://api.cerebras.ai/v1/chat/completions"),
            new Uri("https://api.cerebras.ai/v1/models"),
            DefaultCerebrasModel,
            CerebrasModels),
        new(
            LlmPostProcessingProvider.Groq,
            "Groq",
            new Uri("https://api.groq.com/openai/v1/chat/completions"),
            new Uri("https://api.groq.com/openai/v1/models"),
            DefaultGroqModel,
            GroqModels,
            IsSupportedGroqChatModel),
    ];

    public static LlmProviderDefinition Get(LlmPostProcessingProvider provider)
    {
        return Providers.FirstOrDefault(definition => definition.Provider == provider)
            ?? Providers.First(definition => definition.Provider == DefaultProvider);
    }

    public static LlmPostProcessingProviderSettings GetSettings(this AppSettings settings)
    {
        var provider = settings.LlmPostProcessingProvider;
        var definition = Get(provider);
        var apiKey = provider == LlmPostProcessingProvider.Groq
            ? settings.LlmGroqApiKey ?? string.Empty
            : settings.CerebrasApiKey ?? string.Empty;
        var model = provider == LlmPostProcessingProvider.Groq
            ? settings.LlmGroqModel
            : settings.CerebrasModel;

        return new LlmPostProcessingProviderSettings(
            provider,
            string.IsNullOrWhiteSpace(apiKey) ? string.Empty : apiKey.Trim(),
            string.IsNullOrWhiteSpace(model) ? definition.DefaultModel : model.Trim());
    }

    public static string NormalizeModel(LlmPostProcessingProvider provider, string? model)
    {
        var definition = Get(provider);
        return string.IsNullOrWhiteSpace(model) ? definition.DefaultModel : model.Trim();
    }

    private static bool IsSupportedGroqChatModel(string modelId)
    {
        var normalized = modelId.Trim().ToLowerInvariant();
        return !normalized.Contains("whisper", StringComparison.Ordinal) &&
               !normalized.Contains("prompt-guard", StringComparison.Ordinal) &&
               !normalized.Contains("safeguard", StringComparison.Ordinal) &&
               !normalized.Contains("orpheus", StringComparison.Ordinal) &&
               !normalized.StartsWith("groq/compound", StringComparison.Ordinal);
    }
}
