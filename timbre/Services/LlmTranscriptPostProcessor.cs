using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using timbre.Models;

namespace timbre.Services;

public sealed class LlmTranscriptPostProcessor
{
    private static readonly Uri CerebrasEndpoint = new("https://api.cerebras.ai/v1/chat/completions");
    private static readonly Uri GroqEndpoint = new("https://api.groq.com/openai/v1/chat/completions");

    private readonly HttpClient _httpClient;

    public LlmTranscriptPostProcessor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> CleanTranscriptAsync(string transcript, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var normalizedTranscript = transcript?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedTranscript) || !settings.LlmPostProcessingEnabled)
        {
            return normalizedTranscript;
        }

        var provider = settings.LlmPostProcessingProvider;
        var apiKey = GetApiKey(settings, provider);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranscriptionException($"The {GetProviderDisplayName(provider)} API key is missing.", false);
        }

        var model = GetModel(settings, provider);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new TranscriptionException($"The {GetProviderDisplayName(provider)} model is missing.", false);
        }

        var prompt = NormalizePrompt(settings.LlmPostProcessingPrompt);
        var endpoint = provider == LlmPostProcessingProvider.Cerebras ? CerebrasEndpoint : GroqEndpoint;

        var requestBody = JsonSerializer.Serialize(new
        {
            model,
            stream = false,
            temperature = 0,
            top_p = 1,
            seed = 0,
            max_completion_tokens = 8192,
            response_format = new
            {
                type = "json_object",
            },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You are a transcript cleanup engine. Return a JSON object with exactly one string property named cleaned_text. Do not include markdown, code fences, or extra keys.",
                },
                new
                {
                    role = "system",
                    content = prompt,
                },
                new
                {
                    role = "user",
                    content = BuildUserMessage(normalizedTranscript),
                },
            },
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            DiagnosticsLogger.Info(
                $"LLM transcript post-processing request starting. Provider={GetProviderDisplayName(provider)}, Endpoint={endpoint}, Model={model}, TranscriptLength={normalizedTranscript.Length}." );

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            DiagnosticsLogger.Info(
                $"LLM transcript post-processing response received. Provider={GetProviderDisplayName(provider)}, Status={(int)response.StatusCode} {response.StatusCode}, BodyLength={responseBody.Length}." );

            if (!response.IsSuccessStatusCode)
            {
                throw new TranscriptionException(
                    ExtractErrorMessage(responseBody, provider, (int)response.StatusCode),
                    IsTransientStatusCode(response.StatusCode),
                    response.StatusCode);
            }

            return ExtractCleanedTranscript(responseBody, provider);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionException("The transcript clean-up request timed out.", true);
        }
        catch (HttpRequestException exception)
        {
            throw new TranscriptionException($"The transcript clean-up request could not reach {GetProviderDisplayName(provider)}.", true, null, exception);
        }
    }

    private static string BuildUserMessage(string transcript)
    {
        return $"Clean this transcript and return the result in the required JSON format.\n\nTranscript:\n\"\"\"\n{transcript}\n\"\"\"";
    }

    private static string NormalizePrompt(string? prompt)
    {
        return string.IsNullOrWhiteSpace(prompt)
            ? LlmPostProcessingCatalog.DefaultPrompt
            : prompt.Trim();
    }

    private static string GetApiKey(AppSettings settings, LlmPostProcessingProvider provider)
    {
        return provider == LlmPostProcessingProvider.Groq
            ? settings.LlmGroqApiKey ?? string.Empty
            : settings.CerebrasApiKey ?? string.Empty;
    }

    private static string GetModel(AppSettings settings, LlmPostProcessingProvider provider)
    {
        return provider == LlmPostProcessingProvider.Groq
            ? settings.LlmGroqModel
            : settings.CerebrasModel;
    }

    private static string ExtractCleanedTranscript(string responseBody, LlmPostProcessingProvider provider)
    {
        try
        {
            using var responseDocument = JsonDocument.Parse(responseBody);
            if (!responseDocument.RootElement.TryGetProperty("choices", out var choicesElement) ||
                choicesElement.ValueKind != JsonValueKind.Array ||
                choicesElement.GetArrayLength() == 0)
            {
                throw new TranscriptionException($"{GetProviderDisplayName(provider)} returned an invalid chat completion response.", true);
            }

            var messageElement = choicesElement[0].GetProperty("message");
            var content = messageElement.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new TranscriptionException($"{GetProviderDisplayName(provider)} returned an empty transcript clean-up response.", false);
            }

            var normalizedContent = content.Trim();

            try
            {
                using var contentDocument = JsonDocument.Parse(normalizedContent);
                if (contentDocument.RootElement.TryGetProperty("cleaned_text", out var cleanedTextElement) &&
                    cleanedTextElement.ValueKind == JsonValueKind.String)
                {
                    return cleanedTextElement.GetString()?.Trim() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
            }

            return normalizedContent;
        }
        catch (KeyNotFoundException exception)
        {
            throw new TranscriptionException($"{GetProviderDisplayName(provider)} returned an invalid transcript clean-up response.", true, null, exception);
        }
        catch (JsonException exception)
        {
            throw new TranscriptionException($"{GetProviderDisplayName(provider)} returned an unreadable transcript clean-up response.", true, null, exception);
        }
    }

    private static string ExtractErrorMessage(string responseBody, LlmPostProcessingProvider provider, int statusCode)
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

                if (errorElement.ValueKind == JsonValueKind.Object &&
                    errorElement.TryGetProperty("message", out var nestedMessage) &&
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

        return $"{GetProviderDisplayName(provider)} returned HTTP {statusCode}.";
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var numericStatusCode = (int)statusCode;
        return numericStatusCode == 408 || numericStatusCode == 429 || numericStatusCode >= 500;
    }

    private static string GetProviderDisplayName(LlmPostProcessingProvider provider)
    {
        return provider == LlmPostProcessingProvider.Groq ? "Groq" : "Cerebras";
    }
}
