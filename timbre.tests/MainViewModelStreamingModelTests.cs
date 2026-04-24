using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using timbre.Interfaces;
using timbre.Models;
using timbre.Services;
using timbre.ViewModels;

namespace timbre.tests.ViewModels;

[ExcludeFromCodeCoverage]
public sealed class MainViewModelStreamingModelTests
{
    [Fact]
    public void DeepgramStreamingEnabled_FiltersModelListAndSelection()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act
        viewModel.DeepgramStreamingEnabled = false;

        // Assert
        viewModel.AvailableDeepgramModels.Should().Equal(TranscriptionModelCatalog.DefaultDeepgramNonStreamingModel);
        viewModel.SelectedDeepgramModel.Should().Be(TranscriptionModelCatalog.DefaultDeepgramNonStreamingModel);

        // Act
        viewModel.DeepgramStreamingEnabled = true;

        // Assert
        viewModel.AvailableDeepgramModels.Should().Equal(TranscriptionModelCatalog.DefaultDeepgramStreamingModel);
        viewModel.SelectedDeepgramModel.Should().Be(TranscriptionModelCatalog.DefaultDeepgramStreamingModel);
    }

    [Fact]
    public void MistralStreamingEnabled_FiltersModelListAndSelection()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act
        viewModel.MistralStreamingEnabled = true;

        // Assert
        viewModel.AvailableMistralModels.Should().Equal(TranscriptionModelCatalog.DefaultMistralStreamingModel);
        viewModel.SelectedMistralModel.Should().Be(TranscriptionModelCatalog.DefaultMistralStreamingModel);

        // Act
        viewModel.MistralStreamingEnabled = false;

        // Assert
        viewModel.AvailableMistralModels.Should().Equal(TranscriptionModelCatalog.DefaultMistralNonStreamingModel);
        viewModel.SelectedMistralModel.Should().Be(TranscriptionModelCatalog.DefaultMistralNonStreamingModel);
    }

    [Fact]
    public void ElevenLabsStreamingEnabled_FiltersModelListAndSelection()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Act
        viewModel.ElevenLabsStreamingEnabled = true;

        // Assert
        viewModel.AvailableElevenLabsModels.Should().Equal(TranscriptionModelCatalog.DefaultElevenLabsStreamingModel);
        viewModel.SelectedElevenLabsModel.Should().Be(TranscriptionModelCatalog.DefaultElevenLabsStreamingModel);

        // Act
        viewModel.ElevenLabsStreamingEnabled = false;

        // Assert
        viewModel.AvailableElevenLabsModels.Should().Equal(TranscriptionModelCatalog.DefaultElevenLabsNonStreamingModel);
        viewModel.SelectedElevenLabsModel.Should().Be(TranscriptionModelCatalog.DefaultElevenLabsNonStreamingModel);
    }

    [Fact]
    public async Task SaveSettingsAsync_PersistsStreamingFilteredProviderModels()
    {
        // Arrange
        var settingsStore = new FakeAppSettingsStore();
        var viewModel = CreateViewModel(settingsStore);
        viewModel.DeepgramStreamingEnabled = false;
        viewModel.MistralStreamingEnabled = true;
        viewModel.ElevenLabsStreamingEnabled = true;

        // Act
        var saved = await viewModel.SaveSettingsAsync();

        // Assert
        saved.Should().BeTrue();
        settingsStore.CurrentSettings.DeepgramStreamingEnabled.Should().BeFalse();
        settingsStore.CurrentSettings.DeepgramModel.Should().Be(TranscriptionModelCatalog.DefaultDeepgramNonStreamingModel);
        settingsStore.CurrentSettings.MistralStreamingEnabled.Should().BeTrue();
        settingsStore.CurrentSettings.MistralModel.Should().Be(TranscriptionModelCatalog.DefaultMistralStreamingModel);
        settingsStore.CurrentSettings.ElevenLabsStreamingEnabled.Should().BeTrue();
        settingsStore.CurrentSettings.ElevenLabsModel.Should().Be(TranscriptionModelCatalog.DefaultElevenLabsStreamingModel);
    }

    [Fact]
    public async Task SaveSettingsAsync_WhenAutoDetectLanguageIsBlank_PersistsAuto()
    {
        // Arrange
        var settingsStore = new FakeAppSettingsStore();
        var viewModel = CreateViewModel(settingsStore);
        viewModel.GroqLanguage = "";
        viewModel.FireworksLanguage = "";
        viewModel.ElevenLabsLanguage = "";
        viewModel.CohereLanguage = "";

        // Act
        var saved = await viewModel.SaveSettingsAsync();

        // Assert
        saved.Should().BeTrue();
        settingsStore.CurrentSettings.GroqLanguage.Should().Be("auto");
        settingsStore.CurrentSettings.FireworksLanguage.Should().Be("auto");
        settingsStore.CurrentSettings.ElevenLabsLanguage.Should().Be("auto");
        settingsStore.CurrentSettings.CohereLanguage.Should().Be("en");
    }

    private static MainViewModel CreateViewModel(FakeAppSettingsStore? settingsStore = null)
    {
        return new MainViewModel(
            settingsStore ?? new FakeAppSettingsStore(),
            new FakeAudioDeviceService(),
            new FakeTranscriptHistoryStore(),
            new FakeClipboardPasteService(),
            new FakeDictationController(),
            new FakeLaunchAtStartupService(),
            new LlmModelCatalogClient(new HttpClient()),
            new FakeUiDispatcherQueueAccessor());
    }

    private sealed class FakeAppSettingsStore : IAppSettingsStore
    {
        public AppSettings CurrentSettings { get; private set; } = new();

        public Task<AppSettings> LoadAsync(bool forceReload = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CurrentSettings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            CurrentSettings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAudioDeviceService : IAudioDeviceService
    {
        public IReadOnlyList<AudioInputDevice> GetInputDevices()
        {
            return [];
        }

        public MMDevice OpenPreferredCaptureDevice(string? selectedDeviceId)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class FakeTranscriptHistoryStore : ITranscriptHistoryStore
    {
        public event EventHandler? HistoryChanged;

        public Task AppendAsync(string transcript, int maxEntries, CancellationToken cancellationToken = default)
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranscriptHistoryEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TranscriptHistoryEntry>>([]);
        }

        public Task<string?> GetLatestTranscriptAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task DeleteAsync(string entryId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task EnforceRetentionAsync(int maxEntries, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClipboardPasteService : IClipboardPasteService
    {
        public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDictationController : IDictationController
    {
        public event EventHandler<DictationStatusChangedEventArgs>? StatusChanged;

        public Task<bool> StartDictationAsync()
        {
            return Task.FromResult(false);
        }

        public Task<bool> StopDictationAsync()
        {
            return Task.FromResult(false);
        }

        public Task<bool> CancelTranscriptionAsync()
        {
            return Task.FromResult(false);
        }

        public Task<bool> PasteLastTranscriptAsync(HotkeyBinding? triggeringHotkey = null)
        {
            StatusChanged?.Invoke(this, new DictationStatusChangedEventArgs(DictationState.Idle, string.Empty, false));
            return Task.FromResult(false);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeLaunchAtStartupService : ILaunchAtStartupService
    {
        public bool IsEnabled()
        {
            return false;
        }

        public void SetEnabled(bool isEnabled)
        {
        }
    }

    private sealed class FakeUiDispatcherQueueAccessor : IUiDispatcherQueueAccessor
    {
        public DispatcherQueue? DispatcherQueue { get; set; }
    }
}
