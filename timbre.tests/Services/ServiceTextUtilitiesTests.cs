using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using FluentAssertions;
using timbre.Services;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class ServiceTextUtilitiesTests
{
    [Theory]
    [InlineData("  hello\r\nthere   friend ", "hello there friend")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void NormalizeWhitespace_CollapsesWhitespace(string? value, string expected)
    {
        TranscriptText.NormalizeWhitespace(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("hello", "world", " world")]
    [InlineData("hello", ", world", ", world")]
    [InlineData("hello-", "world", "world")]
    [InlineData("", "hello", "hello")]
    public void BuildAppendChunk_UsesSharedSeparatorRules(string existingTranscript, string nextTranscript, string expected)
    {
        TranscriptText.BuildAppendChunk(existingTranscript, nextTranscript).Should().Be(expected);
    }

    [Fact]
    public void BuildRevisionSuffix_ReturnsOnlyNewSuffixForCompatibleRevision()
    {
        TranscriptText.BuildRevisionSuffix("hello", "hello world").Should().Be(" world");
        TranscriptText.BuildRevisionSuffix("hello", "different").Should().BeEmpty();
    }

    [Fact]
    public void UriQuery_BuildsAndRedactsEscapedQueryParameters()
    {
        var uri = UriQuery.Build(
            new Uri("https://example.test/path"),
            ("token", "secret value"),
            ("language_code", "en"),
            ("ignored", null));

        uri.Query.Should().Contain("token=secret%20value");
        uri.Query.Should().Contain("language_code=en");
        uri.Query.Should().NotContain("ignored=");

        UriQuery.Redact(uri, "token").Query.Should().Contain("token=%3Credacted%3E");
    }

    [Fact]
    public void JsonErrorMessageExtractor_ReadsCommonErrorShapes()
    {
        using var document = JsonDocument.Parse("""
            {
                "detail": [
                    { "msg": "first detail" }
                ]
            }
            """);

        JsonErrorMessageExtractor.TryExtract(document.RootElement, out var message).Should().BeTrue();
        message.Should().Be("first detail");
    }
}
