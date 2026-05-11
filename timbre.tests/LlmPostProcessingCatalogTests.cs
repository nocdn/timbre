using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using timbre.Models;

namespace timbre.tests;

[ExcludeFromCodeCoverage]
public sealed class LlmPostProcessingCatalogTests
{
    [Fact]
    public void Get_ReturnsProviderDefinitionWithEndpointsAndDefaults()
    {
        var definition = LlmPostProcessingCatalog.Get(LlmPostProcessingProvider.Groq);

        definition.DisplayName.Should().Be("Groq");
        definition.ChatCompletionsEndpoint.Should().Be(new Uri("https://api.groq.com/openai/v1/chat/completions"));
        definition.ModelsEndpoint.Should().Be(new Uri("https://api.groq.com/openai/v1/models"));
        definition.DefaultModel.Should().Be(LlmPostProcessingCatalog.DefaultGroqModel);
        definition.BuiltInModels.Should().Contain(LlmPostProcessingCatalog.DefaultGroqModel);
    }

    [Fact]
    public void ModelFilter_RemovesKnownNonChatGroqModels()
    {
        var definition = LlmPostProcessingCatalog.Get(LlmPostProcessingProvider.Groq);

        definition.ModelFilter("openai/gpt-oss-120b").Should().BeTrue();
        definition.ModelFilter("whisper-large-v3").Should().BeFalse();
        definition.ModelFilter("groq/compound").Should().BeFalse();
    }

    [Fact]
    public void GetSettings_NormalizesSelectedProviderApiKeyAndModel()
    {
        var settings = new AppSettings
        {
            LlmPostProcessingProvider = LlmPostProcessingProvider.Groq,
            LlmGroqApiKey = " groq-key ",
            LlmGroqModel = " ",
        };

        var providerSettings = settings.GetSettings();

        providerSettings.Provider.Should().Be(LlmPostProcessingProvider.Groq);
        providerSettings.DisplayName.Should().Be("Groq");
        providerSettings.ApiKey.Should().Be("groq-key");
        providerSettings.Model.Should().Be(LlmPostProcessingCatalog.DefaultGroqModel);
    }
}
