using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using timbre.Models;

namespace timbre.Services;

public sealed class LlmModelCatalogClient
{
    private readonly HttpClient _httpClient;

    public LlmModelCatalogClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<IReadOnlyList<string>> FetchCerebrasModelsAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        return FetchModelsAsync(LlmPostProcessingProvider.Cerebras, apiKey, cancellationToken);
    }

    public Task<IReadOnlyList<string>> FetchGroqModelsAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        return FetchModelsAsync(LlmPostProcessingProvider.Groq, apiKey, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> FetchModelsAsync(
        LlmPostProcessingProvider provider,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var definition = LlmPostProcessingCatalog.Get(provider);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"Enter a {definition.DisplayName} API key before fetching models.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, definition.ModelsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            DiagnosticsLogger.Info($"Fetching LLM models. Provider={definition.DisplayName}, Endpoint={definition.ModelsEndpoint}.");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            DiagnosticsLogger.Info($"LLM models fetch response received. Provider={definition.DisplayName}, Status={(int)response.StatusCode} {response.StatusCode}, BodyLength={responseBody.Length}.");

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(JsonErrorMessageExtractor.Extract(responseBody, definition.DisplayName, (int)response.StatusCode));
            }

            var models = ExtractModels(responseBody, definition.ModelFilter);
            if (models.Count == 0)
            {
                throw new InvalidOperationException($"{definition.DisplayName} did not return any chat-capable models.");
            }

            return models;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Fetching models from {definition.DisplayName} timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"The request to fetch models from {definition.DisplayName} failed.", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"{definition.DisplayName} returned an unreadable models response.", exception);
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

}
