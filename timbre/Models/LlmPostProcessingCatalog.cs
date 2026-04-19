namespace timbre.Models;

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
}
