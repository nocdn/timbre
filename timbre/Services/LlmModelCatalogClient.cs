using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using timbre.Models;

namespace timbre.Services;

public sealed class LlmModelCatalogClient
{
    private static readonly Uri CerebrasModelsEndpoint = new("https://api.cerebras.ai/v1/models");
    private static readonly Uri GroqModelsEndpoint = new("https://api.groq.com/openai/v1/models");

    private readonly HttpClient _httpClient;

    public LlmModelCatalogClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<IReadOnlyList<string>> FetchCerebrasModelsAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        return FetchModelsAsync(
            providerName: "Cerebras",
            endpoint: CerebrasModelsEndpoint,
            apiKey: apiKey,
            filter: static _ => true,
            cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<string>> FetchGroqModelsAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        return FetchModelsAsync(
            providerName: "Groq",
            endpoint: GroqModelsEndpoint,
            apiKey: apiKey,
            filter: IsSupportedGroqChatModel,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<string>> FetchModelsAsync(
        string providerName,
        Uri endpoint,
        string apiKey,
        Func<string, bool> filter,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"Enter a {providerName} API key before fetching models.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            DiagnosticsLogger.Info($"Fetching LLM models. Provider={providerName}, Endpoint={endpoint}.");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            DiagnosticsLogger.Info($"LLM models fetch response received. Provider={providerName}, Status={(int)response.StatusCode} {response.StatusCode}, BodyLength={responseBody.Length}.");

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ExtractErrorMessage(responseBody, providerName, (int)response.StatusCode));
            }

            var models = ExtractModels(responseBody, filter);
            if (models.Count == 0)
            {
                throw new InvalidOperationException($"{providerName} did not return any chat-capable models.");
            }

            return models;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Fetching models from {providerName} timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"The request to fetch models from {providerName} failed.", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"{providerName} returned an unreadable models response.", exception);
        }
    }

    private static List<string> ExtractModels(string responseBody, Func<string, bool> filter)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<string>();
        foreach (var modelElement in dataElement.EnumerateArray())
        {
            if (!modelElement.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var modelId = idElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(modelId) || !filter(modelId) || models.Contains(modelId, StringComparer.Ordinal))
            {
                continue;
            }

            models.Add(modelId);
        }

        return models;
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

    private static string ExtractErrorMessage(string responseBody, string providerName, int statusCode)
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

        return $"{providerName} returned HTTP {statusCode}.";
    }
}
