using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using timbre.Models;
using timbre.Services;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class AppSettingsStoreTests
{
    [Fact]
    public async Task LoadAsync_WhenLegacyMistralRealtimeEnabledExists_MapsItToStreamingEnabled()
    {
        var settingsDirectory = CreateSettingsDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(settingsDirectory, "settings.json"),
            $$"""
            {
              "provider": {{(int)TranscriptionProvider.Mistral}},
              "mistralRealtimeEnabled": true,
              "mistralModel": "{{TranscriptionProviderCatalog.DefaultMistralStreamingModel}}"
            }
            """);
        var store = new AppSettingsStore(settingsDirectory);

        var settings = await store.LoadAsync();

        settings.Provider.Should().Be(TranscriptionProvider.Mistral);
        settings.MistralStreamingEnabled.Should().BeTrue();
        settings.MistralModel.Should().Be(TranscriptionProviderCatalog.DefaultMistralStreamingModel);
    }

    [Fact]
    public async Task LoadAsync_WhenStreamingFlagsAreMissing_InfersThemFromStoredModels()
    {
        var settingsDirectory = CreateSettingsDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(settingsDirectory, "settings.json"),
            $$"""
            {
              "deepgramModel": "nova-3-general",
              "elevenLabsModel": "{{TranscriptionProviderCatalog.DefaultElevenLabsStreamingModel}}"
            }
            """);
        var store = new AppSettingsStore(settingsDirectory);

        var settings = await store.LoadAsync();

        settings.DeepgramStreamingEnabled.Should().BeFalse();
        settings.DeepgramModel.Should().Be(TranscriptionProviderCatalog.DefaultDeepgramNonStreamingModel);
        settings.ElevenLabsStreamingEnabled.Should().BeTrue();
        settings.ElevenLabsModel.Should().Be(TranscriptionProviderCatalog.DefaultElevenLabsStreamingModel);
    }

    [Fact]
    public async Task SaveAndLoadAsync_EncryptsApiKeysAndRestoresTrimmedValues()
    {
        var settingsDirectory = CreateSettingsDirectory();
        var store = new AppSettingsStore(settingsDirectory);

        await store.SaveAsync(new AppSettings
        {
            GroqApiKey = " groq-secret ",
            DeepgramApiKey = "deepgram-secret",
        });

        var json = await File.ReadAllTextAsync(Path.Combine(settingsDirectory, "settings.json"));
        json.Should().NotContain("groq-secret");
        json.Should().NotContain("deepgram-secret");

        var reloadedStore = new AppSettingsStore(settingsDirectory);
        var reloadedSettings = await reloadedStore.LoadAsync();

        reloadedSettings.GroqApiKey.Should().Be("groq-secret");
        reloadedSettings.DeepgramApiKey.Should().Be("deepgram-secret");
    }

    private static string CreateSettingsDirectory()
    {
        var settingsDirectory = Path.Combine(Path.GetTempPath(), "timbre-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDirectory);
        return settingsDirectory;
    }
}
