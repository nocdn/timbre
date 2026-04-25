using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using timbre.Models;

namespace timbre.tests.Models;

[ExcludeFromCodeCoverage]
public sealed class TranscriptionProviderCatalogTests
{
    [Fact]
    public void Providers_DefinesEveryTranscriptionProvider()
    {
        // Arrange
        var expectedProviders = Enum.GetValues<TranscriptionProvider>();

        // Act
        var providers = TranscriptionProviderCatalog.Providers.Select(definition => definition.Provider);

        // Assert
        providers.Should().BeEquivalentTo(expectedProviders);
    }

    [Theory]
    [InlineData(TranscriptionProvider.Deepgram)]
    [InlineData(TranscriptionProvider.Mistral)]
    [InlineData(TranscriptionProvider.ElevenLabs)]
    public void GetModelIds_WhenProviderSupportsStreaming_FiltersByStreamingCompatibility(TranscriptionProvider provider)
    {
        // Arrange
        var definition = TranscriptionProviderCatalog.Get(provider);

        // Act
        var streamingModels = TranscriptionProviderCatalog.GetModelIds(provider, streamingEnabled: true);
        var nonStreamingModels = TranscriptionProviderCatalog.GetModelIds(provider, streamingEnabled: false);

        // Assert
        definition.SupportsStreaming.Should().BeTrue();
        streamingModels.Should().OnlyContain(model => definition.Models.Single(candidate => candidate.Id == model).SupportsStreaming);
        nonStreamingModels.Should().OnlyContain(model => !definition.Models.Single(candidate => candidate.Id == model).SupportsStreaming);
        streamingModels.Should().NotIntersectWith(nonStreamingModels);
    }

    [Fact]
    public void InferStreamingEnabled_UsesCatalogModelCompatibilityAndProviderDefaults()
    {
        // Assert
        TranscriptionProviderCatalog.InferStreamingEnabled(TranscriptionProvider.Deepgram, null).Should().BeTrue();
        TranscriptionProviderCatalog.InferStreamingEnabled(TranscriptionProvider.Deepgram, "nova-3-general").Should().BeFalse();
        TranscriptionProviderCatalog.InferStreamingEnabled(TranscriptionProvider.Mistral, null).Should().BeFalse();
        TranscriptionProviderCatalog.InferStreamingEnabled(TranscriptionProvider.Mistral, TranscriptionProviderCatalog.DefaultMistralStreamingModel).Should().BeTrue();
        TranscriptionProviderCatalog.InferStreamingEnabled(TranscriptionProvider.ElevenLabs, TranscriptionProviderCatalog.DefaultElevenLabsNonStreamingModel).Should().BeFalse();
    }

    [Fact]
    public void NormalizeLanguage_UsesProviderLanguageMode()
    {
        // Assert
        TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.Groq, null).Should().Be("auto");
        TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.Fireworks, "AUTO").Should().Be("auto");
        TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.ElevenLabs, " ENG ").Should().Be("eng");
        TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.Cohere, "auto").Should().Be("en");
        TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.Deepgram, "fr").Should().Be("en");
    }

    [Fact]
    public void SupportsVadSilenceThreshold_UsesStreamingModelCapabilities()
    {
        // Assert
        TranscriptionProviderCatalog.SupportsVadSilenceThreshold(TranscriptionProvider.Deepgram, streamingEnabled: true).Should().BeTrue();
        TranscriptionProviderCatalog.SupportsVadSilenceThreshold(TranscriptionProvider.Deepgram, streamingEnabled: false).Should().BeFalse();
        TranscriptionProviderCatalog.SupportsVadSilenceThreshold(TranscriptionProvider.ElevenLabs, streamingEnabled: true).Should().BeTrue();
        TranscriptionProviderCatalog.SupportsVadSilenceThreshold(TranscriptionProvider.ElevenLabs, streamingEnabled: false).Should().BeFalse();
        TranscriptionProviderCatalog.SupportsVadSilenceThreshold(TranscriptionProvider.Mistral, streamingEnabled: true).Should().BeFalse();
    }

    [Fact]
    public void NormalizeVadSilenceThresholdSeconds_UsesProviderRangesAndCleansFloatingPointNoise()
    {
        // Assert
        TranscriptionProviderCatalog.NormalizeDeepgramVadSilenceThresholdSeconds(null).Should().Be(5.0);
        TranscriptionProviderCatalog.NormalizeDeepgramVadSilenceThresholdSeconds(0.1).Should().Be(0.5);
        TranscriptionProviderCatalog.NormalizeDeepgramVadSilenceThresholdSeconds(12).Should().Be(10.0);
        TranscriptionProviderCatalog.NormalizeElevenLabsVadSilenceThresholdSeconds(0.7000000015).Should().Be(0.7);
        TranscriptionProviderCatalog.NormalizeElevenLabsVadSilenceThresholdSeconds(0.654321).Should().Be(0.654321);
    }

    [Fact]
    public void TryGetUploadLimitBytes_UsesProviderCatalog()
    {
        // Act
        var hasGroqLimit = TranscriptionProviderCatalog.TryGetUploadLimitBytes(TranscriptionProvider.Groq, out var groqLimit);
        var hasMistralLimit = TranscriptionProviderCatalog.TryGetUploadLimitBytes(TranscriptionProvider.Mistral, out var mistralLimit);

        // Assert
        hasGroqLimit.Should().BeTrue();
        groqLimit.Should().Be(25L * 1024 * 1024);
        hasMistralLimit.Should().BeFalse();
        mistralLimit.Should().Be(0);
    }

    [Fact]
    public void TranscriptionModelCatalog_DelegatesToProviderCatalog()
    {
        // Assert
        TranscriptionModelCatalog.GroqModels.Should().Equal(TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Groq));
        TranscriptionModelCatalog.GetDeepgramModels(streamingEnabled: true)
            .Should()
            .Equal(TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Deepgram, streamingEnabled: true));
        TranscriptionModelCatalog.NormalizeElevenLabsModel("missing-model", streamingEnabled: false)
            .Should()
            .Be(TranscriptionProviderCatalog.DefaultElevenLabsNonStreamingModel);
    }
}
