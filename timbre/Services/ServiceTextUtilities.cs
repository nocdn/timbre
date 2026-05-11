using System.Text.Json;

namespace timbre.Services;

internal static class TranscriptText
{
    public static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static string BuildRevisionSuffix(string existingTranscript, string nextTranscript)
    {
        if (string.IsNullOrWhiteSpace(nextTranscript))
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(existingTranscript))
        {
            return nextTranscript;
        }

        if (!nextTranscript.StartsWith(existingTranscript, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return nextTranscript[existingTranscript.Length..];
    }

    public static string BuildAppendChunk(string existingTranscript, string nextTranscript)
    {
        if (string.IsNullOrWhiteSpace(nextTranscript))
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(existingTranscript))
        {
            return nextTranscript;
        }

        return NeedsSeparator(existingTranscript[^1], nextTranscript[0])
            ? $" {nextTranscript}"
            : nextTranscript;
    }

    public static string Preview(string? value, int maxLength = 120)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static bool NeedsSeparator(char previousCharacter, char nextCharacter)
    {
        if (char.IsWhiteSpace(previousCharacter) || char.IsWhiteSpace(nextCharacter))
        {
            return false;
        }

        return !IsLeadingPunctuation(nextCharacter) && !IsTrailingPunctuation(previousCharacter);
    }

    private static bool IsLeadingPunctuation(char value)
    {
        return value is '.' or ',' or '!' or '?' or ';' or ':' or ')' or ']' or '}' or '\'' or '"';
    }

    private static bool IsTrailingPunctuation(char value)
    {
        return value is '(' or '[' or '{' or '/' or '-' or '\'' or '"';
    }
}

internal static class UriQuery
{
    public static Uri Build(Uri baseUri, params (string Key, string? Value)[] parameters)
    {
        return new UriBuilder(baseUri)
        {
            Query = BuildQuery(parameters),
        }.Uri;
    }

    public static Uri Redact(Uri uri, params string[] keysToRedact)
    {
        var redactedKeys = new HashSet<string>(keysToRedact, StringComparer.OrdinalIgnoreCase);
        var parameters = Parse(uri.Query)
            .Select(pair => redactedKeys.Contains(pair.Key)
                ? (pair.Key, Value: (string?)"<redacted>")
                : (pair.Key, Value: (string?)pair.Value))
            .ToArray();

        return Build(uri, parameters);
    }

    private static string BuildQuery(IEnumerable<(string Key, string? Value)> parameters)
    {
        return string.Join(
            "&",
            parameters
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
                .Select(parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value!)}"));
    }

    private static IReadOnlyList<(string Key, string Value)> Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var results = new List<(string Key, string Value)>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex < 0)
            {
                results.Add((Uri.UnescapeDataString(pair), string.Empty));
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separatorIndex]);
            var value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            results.Add((key, value));
        }

        return results;
    }
}

internal static class JsonErrorMessageExtractor
{
    public static string Extract(string responseBody, string providerName, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (TryExtract(document.RootElement, out var message))
            {
                return message;
            }
        }
        catch (JsonException)
        {
        }

        return $"{providerName} returned HTTP {statusCode}.";
    }

    public static bool TryExtract(JsonElement root, out string message)
    {
        if (root.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(root.GetString()))
        {
            message = root.GetString()!.Trim();
            return true;
        }

        if (TryReadStringProperty(root, "err_msg", out message) ||
            TryReadStringProperty(root, "message", out message) ||
            TryReadStringProperty(root, "detail", out message) ||
            TryReadStringProperty(root, "details", out message))
        {
            return true;
        }

        if (root.TryGetProperty("error", out var errorElement) && TryReadErrorElement(errorElement, out message))
        {
            return true;
        }

        if (root.TryGetProperty("detail", out var detailElement))
        {
            if (detailElement.ValueKind == JsonValueKind.Object && TryReadErrorElement(detailElement, out message))
            {
                return true;
            }

            if (detailElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in detailElement.EnumerateArray())
                {
                    if (TryReadStringProperty(item, "msg", out message) ||
                        TryReadStringProperty(item, "message", out message) ||
                        TryReadStringProperty(item, "detail", out message))
                    {
                        return true;
                    }
                }
            }
        }

        message = string.Empty;
        return false;
    }

    public static bool TryReadStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!.Trim();
        return true;
    }

    private static bool TryReadErrorElement(JsonElement element, out string value)
    {
        value = string.Empty;

        if (element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
        {
            value = element.GetString()!.Trim();
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryReadStringProperty(element, "message", out value) ||
            TryReadStringProperty(element, "detail", out value) ||
            TryReadStringProperty(element, "msg", out value);
    }
}
