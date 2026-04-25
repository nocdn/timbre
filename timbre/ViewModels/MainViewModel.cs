using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using timbre.Interfaces;
using timbre.Models;
using timbre.Services;

namespace timbre.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAppSettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly ITranscriptHistoryStore _transcriptHistoryStore;
    private readonly IClipboardPasteService _clipboardPasteService;
    private readonly IDictationController _dictationController;
    private readonly ILaunchAtStartupService _launchAtStartupService;
    private readonly LlmModelCatalogClient _llmModelCatalogClient;
    private readonly IUiDispatcherQueueAccessor _uiDispatcherQueueAccessor;

    private AudioInputDevice? _selectedInputDevice;
    private TranscriptionProvider _selectedProvider = TranscriptionProvider.Groq;
    private bool _llmPostProcessingEnabled;
    private LlmPostProcessingProvider _selectedLlmPostProcessingProvider = LlmPostProcessingCatalog.DefaultProvider;
    private string _groqApiKey = string.Empty;
    private string _cerebrasApiKey = string.Empty;
    private string _llmGroqApiKey = string.Empty;
    private string _fireworksApiKey = string.Empty;
    private string _deepgramApiKey = string.Empty;
    private string _mistralApiKey = string.Empty;
    private string _cohereApiKey = string.Empty;
    private string _elevenLabsApiKey = string.Empty;
    private bool _deepgramStreamingEnabled = true;
    private bool _mistralStreamingEnabled;
    private bool _elevenLabsStreamingEnabled;
    private bool _pushToTalk = true;
    private bool _launchAtStartup;
    private bool _soundFeedbackEnabled = true;
    private int _transcriptHistoryLimit = 200;
    private double _transcriptHistoryLimitValue = 200;
    private string _llmPostProcessingPrompt = LlmPostProcessingCatalog.DefaultPrompt;
    private string _selectedCerebrasModel = LlmPostProcessingCatalog.DefaultCerebrasModel;
    private string _selectedLlmGroqModel = LlmPostProcessingCatalog.DefaultGroqModel;
    private string _selectedGroqModel = TranscriptionProviderCatalog.DefaultGroqModel;
    private string _selectedFireworksModel = TranscriptionProviderCatalog.DefaultFireworksModel;
    private string _groqLanguage = TranscriptionProviderCatalog.Get(TranscriptionProvider.Groq).DefaultLanguage;
    private string _fireworksLanguage = TranscriptionProviderCatalog.Get(TranscriptionProvider.Fireworks).DefaultLanguage;
    private string _selectedDeepgramModel = TranscriptionProviderCatalog.DefaultDeepgramStreamingModel;
    private double _deepgramVadSilenceThresholdSeconds = TranscriptionProviderCatalog.DefaultDeepgramVadSilenceThresholdSeconds;
    private string _selectedMistralModel = TranscriptionProviderCatalog.DefaultMistralNonStreamingModel;
    private MistralRealtimeMode _mistralRealtimeMode = MistralRealtimeMode.Fast;
    private string _selectedCohereModel = TranscriptionProviderCatalog.DefaultCohereModel;
    private string _cohereLanguage = TranscriptionProviderCatalog.Get(TranscriptionProvider.Cohere).DefaultLanguage;
    private string _selectedElevenLabsModel = TranscriptionProviderCatalog.DefaultElevenLabsNonStreamingModel;
    private string _elevenLabsLanguage = TranscriptionProviderCatalog.Get(TranscriptionProvider.ElevenLabs).DefaultLanguage;
    private double _elevenLabsVadSilenceThresholdSeconds = TranscriptionProviderCatalog.DefaultElevenLabsVadSilenceThresholdSeconds;
    private string _recordingHotkeyDisplay = HotkeyBinding.Default.ToDisplayString();
    private string _pasteLastTranscriptHotkeyDisplay = HotkeyBinding.PasteLastTranscriptDefault.ToDisplayString();
    private string _openHistoryHotkeyDisplay = HotkeyBinding.OpenHistoryDefault.ToDisplayString();
    private string _statusMessage = string.Empty;
    private string _hotkeyWarningMessage = string.Empty;
    private bool _canCancelTranscription;
    private bool _isHistoryEmpty = true;
    private DictationState _dictationState = DictationState.Idle;
    private bool _isInitialized;
    private List<string>? _fetchedCerebrasModels;
    private List<string>? _fetchedLlmGroqModels;
    private HotkeyBinding _pendingHotkey = HotkeyBinding.Default;
    private HotkeyBinding _pendingPasteLastTranscriptHotkey = HotkeyBinding.PasteLastTranscriptDefault;
    private HotkeyBinding _pendingOpenHistoryHotkey = HotkeyBinding.OpenHistoryDefault;

    public MainViewModel(
        IAppSettingsStore settingsStore,
        IAudioDeviceService audioDeviceService,
        ITranscriptHistoryStore transcriptHistoryStore,
        IClipboardPasteService clipboardPasteService,
        IDictationController dictationController,
        ILaunchAtStartupService launchAtStartupService,
        LlmModelCatalogClient llmModelCatalogClient,
        IUiDispatcherQueueAccessor uiDispatcherQueueAccessor)
    {
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _transcriptHistoryStore = transcriptHistoryStore;
        _clipboardPasteService = clipboardPasteService;
        _dictationController = dictationController;
        _launchAtStartupService = launchAtStartupService;
        _llmModelCatalogClient = llmModelCatalogClient;
        _uiDispatcherQueueAccessor = uiDispatcherQueueAccessor;

        AvailableCerebrasModels = new ObservableCollection<string>(LlmPostProcessingCatalog.CerebrasModels);
        AvailableLlmGroqModels = new ObservableCollection<string>(LlmPostProcessingCatalog.GroqModels);

        _transcriptHistoryStore.HistoryChanged += OnHistoryChanged;
        _dictationController.StatusChanged += OnDictationStatusChanged;
    }

    public event Action<AppSettings>? SettingsSaved;

    public ObservableCollection<AudioInputDevice> InputDevices { get; } = [];

    public ObservableCollection<TranscriptHistoryItemViewModel> TranscriptHistoryEntries { get; } = [];

    public ObservableCollection<string> AvailableCerebrasModels { get; }

    public ObservableCollection<string> AvailableLlmGroqModels { get; }

    public IReadOnlyList<string> AvailableGroqModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Groq);

    public IReadOnlyList<string> AvailableFireworksModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Fireworks);

    public IReadOnlyList<string> AvailableDeepgramModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Deepgram, DeepgramStreamingEnabled);

    public IReadOnlyList<string> AvailableMistralModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Mistral, MistralStreamingEnabled);

    public IReadOnlyList<string> AvailableCohereModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Cohere);

    public IReadOnlyList<string> AvailableElevenLabsModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.ElevenLabs, ElevenLabsStreamingEnabled);

    public bool LlmPostProcessingEnabled
    {
        get => _llmPostProcessingEnabled;
        set
        {
            if (SetProperty(ref _llmPostProcessingEnabled, value))
            {
                OnPropertyChanged(nameof(LlmPostProcessingSettingsVisibility));
                OnPropertyChanged(nameof(CerebrasLlmSettingsVisibility));
                OnPropertyChanged(nameof(GroqLlmSettingsVisibility));
            }
        }
    }

    public LlmPostProcessingProvider SelectedLlmPostProcessingProvider
    {
        get => _selectedLlmPostProcessingProvider;
        set
        {
            if (SetProperty(ref _selectedLlmPostProcessingProvider, value))
            {
                OnPropertyChanged(nameof(IsCerebrasLlmSelected));
                OnPropertyChanged(nameof(IsGroqLlmSelected));
                OnPropertyChanged(nameof(CerebrasLlmSettingsVisibility));
                OnPropertyChanged(nameof(GroqLlmSettingsVisibility));
            }
        }
    }

    public bool IsCerebrasLlmSelected => SelectedLlmPostProcessingProvider == LlmPostProcessingProvider.Cerebras;

    public bool IsGroqLlmSelected => SelectedLlmPostProcessingProvider == LlmPostProcessingProvider.Groq;

    public Visibility LlmPostProcessingSettingsVisibility => LlmPostProcessingEnabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CerebrasLlmSettingsVisibility => LlmPostProcessingEnabled && IsCerebrasLlmSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GroqLlmSettingsVisibility => LlmPostProcessingEnabled && IsGroqLlmSelected ? Visibility.Visible : Visibility.Collapsed;

    public TranscriptionProvider SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                OnPropertyChanged(nameof(IsGroqSelected));
                OnPropertyChanged(nameof(IsFireworksSelected));
                OnPropertyChanged(nameof(IsDeepgramSelected));
                OnPropertyChanged(nameof(IsMistralSelected));
                OnPropertyChanged(nameof(IsCohereSelected));
                OnPropertyChanged(nameof(IsElevenLabsSelected));
                OnPropertyChanged(nameof(GroqSettingsVisibility));
                OnPropertyChanged(nameof(FireworksSettingsVisibility));
                OnPropertyChanged(nameof(DeepgramSettingsVisibility));
                OnPropertyChanged(nameof(MistralSettingsVisibility));
                OnPropertyChanged(nameof(CohereSettingsVisibility));
                OnPropertyChanged(nameof(ElevenLabsSettingsVisibility));
            }
        }
    }

    public bool IsGroqSelected => SelectedProvider == TranscriptionProvider.Groq;

    public bool IsFireworksSelected => SelectedProvider == TranscriptionProvider.Fireworks;

    public bool IsDeepgramSelected => SelectedProvider == TranscriptionProvider.Deepgram;

    public bool IsMistralSelected => SelectedProvider == TranscriptionProvider.Mistral;

    public bool IsCohereSelected => SelectedProvider == TranscriptionProvider.Cohere;

    public bool IsElevenLabsSelected => SelectedProvider == TranscriptionProvider.ElevenLabs;

    public Visibility GroqSettingsVisibility => IsGroqSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FireworksSettingsVisibility => IsFireworksSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeepgramSettingsVisibility => IsDeepgramSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MistralSettingsVisibility => IsMistralSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CohereSettingsVisibility => IsCohereSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ElevenLabsSettingsVisibility => IsElevenLabsSelected ? Visibility.Visible : Visibility.Collapsed;

    public AudioInputDevice? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set => SetProperty(ref _selectedInputDevice, value);
    }

    public string GroqApiKey
    {
        get => _groqApiKey;
        set => SetProperty(ref _groqApiKey, value);
    }

    public string CerebrasApiKey
    {
        get => _cerebrasApiKey;
        set => SetProperty(ref _cerebrasApiKey, value);
    }

    public string LlmGroqApiKey
    {
        get => _llmGroqApiKey;
        set => SetProperty(ref _llmGroqApiKey, value);
    }

    public string FireworksApiKey
    {
        get => _fireworksApiKey;
        set => SetProperty(ref _fireworksApiKey, value);
    }

    public string DeepgramApiKey
    {
        get => _deepgramApiKey;
        set => SetProperty(ref _deepgramApiKey, value);
    }

    public string MistralApiKey
    {
        get => _mistralApiKey;
        set => SetProperty(ref _mistralApiKey, value);
    }

    public string CohereApiKey
    {
        get => _cohereApiKey;
        set => SetProperty(ref _cohereApiKey, value);
    }

    public string ElevenLabsApiKey
    {
        get => _elevenLabsApiKey;
        set => SetProperty(ref _elevenLabsApiKey, value);
    }

    public bool DeepgramStreamingEnabled
    {
        get => _deepgramStreamingEnabled;
        set
        {
            if (SetProperty(ref _deepgramStreamingEnabled, value))
            {
                OnPropertyChanged(nameof(AvailableDeepgramModels));
                OnPropertyChanged(nameof(DeepgramVadSilenceThresholdVisibility));

                SelectedDeepgramModel = SelectPreferredProviderModel(
                    AvailableDeepgramModels,
                    SelectedDeepgramModel,
                    TranscriptionProviderCatalog.GetDefaultModel(TranscriptionProvider.Deepgram, value));
            }
        }
    }

    public bool MistralStreamingEnabled
    {
        get => _mistralStreamingEnabled;
        set
        {
            if (SetProperty(ref _mistralStreamingEnabled, value))
            {
                OnPropertyChanged(nameof(AvailableMistralModels));

                SelectedMistralModel = SelectPreferredProviderModel(
                    AvailableMistralModels,
                    SelectedMistralModel,
                    TranscriptionProviderCatalog.GetDefaultModel(TranscriptionProvider.Mistral, value));
            }
        }
    }

    public bool ElevenLabsStreamingEnabled
    {
        get => _elevenLabsStreamingEnabled;
        set
        {
            if (SetProperty(ref _elevenLabsStreamingEnabled, value))
            {
                OnPropertyChanged(nameof(AvailableElevenLabsModels));
                OnPropertyChanged(nameof(ElevenLabsVadSilenceThresholdVisibility));

                SelectedElevenLabsModel = SelectPreferredProviderModel(
                    AvailableElevenLabsModels,
                    SelectedElevenLabsModel,
                    TranscriptionProviderCatalog.GetDefaultModel(TranscriptionProvider.ElevenLabs, value));
            }
        }
    }

    public bool PushToTalk
    {
        get => _pushToTalk;
        set => SetProperty(ref _pushToTalk, value);
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set => SetProperty(ref _launchAtStartup, value);
    }

    public bool SoundFeedbackEnabled
    {
        get => _soundFeedbackEnabled;
        set => SetProperty(ref _soundFeedbackEnabled, value);
    }

    public int TranscriptHistoryLimit
    {
        get => _transcriptHistoryLimit;
        private set => SetProperty(ref _transcriptHistoryLimit, value);
    }

    public double TranscriptHistoryLimitValue
    {
        get => _transcriptHistoryLimitValue;
        set
        {
            if (SetProperty(ref _transcriptHistoryLimitValue, value))
            {
                TranscriptHistoryLimit = NormalizeTranscriptHistoryLimit(value);
            }
        }
    }

    public string SelectedGroqModel
    {
        get => _selectedGroqModel;
        set => SetProperty(ref _selectedGroqModel, value);
    }

    public string LlmPostProcessingPrompt
    {
        get => _llmPostProcessingPrompt;
        set => SetProperty(ref _llmPostProcessingPrompt, value);
    }

    public string SelectedCerebrasModel
    {
        get => _selectedCerebrasModel;
        set => SetProperty(ref _selectedCerebrasModel, value);
    }

    public string SelectedLlmGroqModel
    {
        get => _selectedLlmGroqModel;
        set => SetProperty(ref _selectedLlmGroqModel, value);
    }

    public string GroqLanguage
    {
        get => _groqLanguage;
        set => SetProperty(ref _groqLanguage, value);
    }

    public string SelectedFireworksModel
    {
        get => _selectedFireworksModel;
        set => SetProperty(ref _selectedFireworksModel, value);
    }

    public string FireworksLanguage
    {
        get => _fireworksLanguage;
        set => SetProperty(ref _fireworksLanguage, value);
    }

    public string SelectedDeepgramModel
    {
        get => _selectedDeepgramModel;
        set
        {
            if (SetProperty(ref _selectedDeepgramModel, value))
            {
                OnPropertyChanged(nameof(DeepgramVadSilenceThresholdVisibility));
            }
        }
    }

    public double DeepgramVadSilenceThresholdSeconds
    {
        get => _deepgramVadSilenceThresholdSeconds;
        set => SetProperty(
            ref _deepgramVadSilenceThresholdSeconds,
            TranscriptionProviderCatalog.NormalizeDeepgramVadSilenceThresholdSeconds(value));
    }

    public string SelectedMistralModel
    {
        get => _selectedMistralModel;
        set => SetProperty(ref _selectedMistralModel, value);
    }

    public MistralRealtimeMode MistralRealtimeMode
    {
        get => _mistralRealtimeMode;
        set => SetProperty(ref _mistralRealtimeMode, value);
    }

    public string SelectedCohereModel
    {
        get => _selectedCohereModel;
        set => SetProperty(ref _selectedCohereModel, value);
    }

    public string CohereLanguage
    {
        get => _cohereLanguage;
        set => SetProperty(ref _cohereLanguage, value);
    }

    public string SelectedElevenLabsModel
    {
        get => _selectedElevenLabsModel;
        set
        {
            if (SetProperty(ref _selectedElevenLabsModel, value))
            {
                OnPropertyChanged(nameof(ElevenLabsVadSilenceThresholdVisibility));
            }
        }
    }

    public string ElevenLabsLanguage
    {
        get => _elevenLabsLanguage;
        set => SetProperty(ref _elevenLabsLanguage, value);
    }

    public double ElevenLabsVadSilenceThresholdSeconds
    {
        get => _elevenLabsVadSilenceThresholdSeconds;
        set => SetProperty(
            ref _elevenLabsVadSilenceThresholdSeconds,
            TranscriptionProviderCatalog.NormalizeElevenLabsVadSilenceThresholdSeconds(value));
    }

    public Visibility DeepgramVadSilenceThresholdVisibility => TranscriptionProviderCatalog.SupportsVadSilenceThreshold(
        TranscriptionProvider.Deepgram,
        DeepgramStreamingEnabled)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ElevenLabsVadSilenceThresholdVisibility => TranscriptionProviderCatalog.SupportsVadSilenceThreshold(
        TranscriptionProvider.ElevenLabs,
        ElevenLabsStreamingEnabled)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string RecordingHotkeyDisplay
    {
        get => _recordingHotkeyDisplay;
        private set => SetProperty(ref _recordingHotkeyDisplay, value);
    }

    public string PasteLastTranscriptHotkeyDisplay
    {
        get => _pasteLastTranscriptHotkeyDisplay;
        private set => SetProperty(ref _pasteLastTranscriptHotkeyDisplay, value);
    }

    public string OpenHistoryHotkeyDisplay
    {
        get => _openHistoryHotkeyDisplay;
        private set => SetProperty(ref _openHistoryHotkeyDisplay, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string HotkeyWarningMessage
    {
        get => _hotkeyWarningMessage;
        private set
        {
            if (SetProperty(ref _hotkeyWarningMessage, value))
            {
                OnPropertyChanged(nameof(HotkeyWarningVisibility));
            }
        }
    }

    public Visibility HotkeyWarningVisibility => string.IsNullOrWhiteSpace(HotkeyWarningMessage)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool CanCancelTranscription
    {
        get => _canCancelTranscription;
        private set
        {
            if (SetProperty(ref _canCancelTranscription, value))
            {
                OnPropertyChanged(nameof(CancelTranscriptionVisibility));
            }
        }
    }

    public Visibility CancelTranscriptionVisibility => CanCancelTranscription
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsHistoryEmpty
    {
        get => _isHistoryEmpty;
        private set
        {
            if (SetProperty(ref _isHistoryEmpty, value))
            {
                OnPropertyChanged(nameof(HistoryEmptyVisibility));
            }
        }
    }

    public Visibility HistoryEmptyVisibility => IsHistoryEmpty
        ? Visibility.Visible
        : Visibility.Collapsed;

    public DictationState DictationState
    {
        get => _dictationState;
        private set => SetProperty(ref _dictationState, value);
    }

    public async Task InitializeAsync(bool forceReload = false)
    {
        var settings = await _settingsStore.LoadAsync(forceReload);
        ApplySettings(settings);
        LaunchAtStartup = _launchAtStartupService.IsEnabled();
        await ReloadDevicesAsync();
        await LoadHistoryAsync();

        if (!_isInitialized)
        {
            _isInitialized = true;
        }
    }

    public Task ReloadDevicesAsync()
    {
        var selectedId = SelectedInputDevice?.Id ?? _settingsStore.CurrentSettings.SelectedInputDeviceId;
        var devices = _audioDeviceService.GetInputDevices();

        InputDevices.Clear();
        foreach (var device in devices)
        {
            InputDevices.Add(device);
        }

        SelectedInputDevice = devices.FirstOrDefault(device => device.Id == selectedId)
            ?? devices.FirstOrDefault(device => device.IsDefault)
            ?? devices.FirstOrDefault();

        if (devices.Count == 0)
        {
            StatusMessage = "No input devices are currently available.";
        }
        else if (string.Equals(StatusMessage, "No input devices are currently available.", StringComparison.Ordinal))
        {
            StatusMessage = string.Empty;
        }

        return Task.CompletedTask;
    }

    public async Task<bool> SaveSettingsAsync()
    {
        var validation = HotkeyValidationService.Validate(_pendingHotkey, _pendingPasteLastTranscriptHotkey, _pendingOpenHistoryHotkey);
        HotkeyWarningMessage = string.Join(" ", validation.Warnings);

        if (validation.HasErrors)
        {
            StatusMessage = string.Join(" ", validation.Errors);
            return false;
        }

        var settings = CreateSettingsSnapshot();
        var currentSettings = _settingsStore.CurrentSettings;
        var settingsChanged = !AreSettingsEquivalent(currentSettings, settings);
        var shouldUpdateLaunchAtStartup = currentSettings.LaunchAtStartup != settings.LaunchAtStartup;

        if (shouldUpdateLaunchAtStartup)
        {
            _launchAtStartupService.SetEnabled(LaunchAtStartup);
        }

        if (!settingsChanged)
        {
            StatusMessage = string.Empty;
            return true;
        }

        await _settingsStore.SaveAsync(settings);
        SettingsSaved?.Invoke(settings);
        StatusMessage = string.Empty;
        return true;
    }

    public async Task CancelTranscriptionAsync()
    {
        await _dictationController.CancelTranscriptionAsync();
    }

    public async Task CopyHistoryEntryAsync(TranscriptHistoryItemViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        await _clipboardPasteService.CopyTextAsync(entry.Text);
        StatusMessage = "Transcript copied to the clipboard.";
    }

    public async Task DeleteHistoryEntryAsync(TranscriptHistoryItemViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        await _transcriptHistoryStore.DeleteAsync(entry.EntryId);
        StatusMessage = "Transcript deleted.";
    }

    public async Task ClearHistoryAsync()
    {
        await _transcriptHistoryStore.ClearAsync();
        StatusMessage = "Transcript history cleared.";
    }

    public async Task FetchCerebrasModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await _llmModelCatalogClient.FetchCerebrasModelsAsync(CerebrasApiKey, cancellationToken);
        ReplaceModels(AvailableCerebrasModels, models);
        _fetchedCerebrasModels = [.. models];
        SelectedCerebrasModel = SelectPreferredModel(AvailableCerebrasModels, SelectedCerebrasModel, LlmPostProcessingCatalog.DefaultCerebrasModel);
    }

    public async Task FetchLlmGroqModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await _llmModelCatalogClient.FetchGroqModelsAsync(LlmGroqApiKey, cancellationToken);
        ReplaceModels(AvailableLlmGroqModels, models);
        _fetchedLlmGroqModels = [.. models];
        SelectedLlmGroqModel = SelectPreferredModel(AvailableLlmGroqModels, SelectedLlmGroqModel, LlmPostProcessingCatalog.DefaultGroqModel);
    }

    public void ResetLlmPostProcessingPrompt()
    {
        LlmPostProcessingPrompt = LlmPostProcessingCatalog.DefaultPrompt;
    }

    public void ApplyRecordingHotkey(HotkeyBinding hotkey)
    {
        _pendingHotkey = hotkey;
        RecordingHotkeyDisplay = hotkey.ToDisplayString();
        RefreshHotkeyWarnings();
        StatusMessage = string.Empty;
    }

    public void ApplyPasteLastTranscriptHotkey(HotkeyBinding hotkey)
    {
        _pendingPasteLastTranscriptHotkey = hotkey;
        PasteLastTranscriptHotkeyDisplay = hotkey.ToDisplayString();
        RefreshHotkeyWarnings();
        StatusMessage = string.Empty;
    }

    public void ApplyOpenHistoryHotkey(HotkeyBinding hotkey)
    {
        _pendingOpenHistoryHotkey = hotkey;
        OpenHistoryHotkeyDisplay = hotkey.ToDisplayString();
        RefreshHotkeyWarnings();
        StatusMessage = string.Empty;
    }

    public void Dispose()
    {
        _transcriptHistoryStore.HistoryChanged -= OnHistoryChanged;
        _dictationController.StatusChanged -= OnDictationStatusChanged;
    }

    private void ApplySettings(AppSettings settings)
    {
        SelectedProvider = settings.Provider;
        LlmPostProcessingEnabled = settings.LlmPostProcessingEnabled;
        SelectedLlmPostProcessingProvider = settings.LlmPostProcessingProvider;
        GroqApiKey = settings.GroqApiKey ?? string.Empty;
        CerebrasApiKey = settings.CerebrasApiKey ?? string.Empty;
        LlmGroqApiKey = settings.LlmGroqApiKey ?? string.Empty;
        FireworksApiKey = settings.FireworksApiKey ?? string.Empty;
        DeepgramApiKey = settings.DeepgramApiKey ?? string.Empty;
        MistralApiKey = settings.MistralApiKey ?? string.Empty;
        CohereApiKey = settings.CohereApiKey ?? string.Empty;
        ElevenLabsApiKey = settings.ElevenLabsApiKey ?? string.Empty;
        DeepgramStreamingEnabled = settings.DeepgramStreamingEnabled;
        MistralStreamingEnabled = settings.MistralStreamingEnabled;
        MistralRealtimeMode = settings.MistralRealtimeMode;
        ElevenLabsStreamingEnabled = settings.ElevenLabsStreamingEnabled;
        PushToTalk = settings.PushToTalk;
        LaunchAtStartup = settings.LaunchAtStartup;
        SoundFeedbackEnabled = settings.SoundFeedbackEnabled;
        TranscriptHistoryLimit = settings.TranscriptHistoryLimit;
        TranscriptHistoryLimitValue = settings.TranscriptHistoryLimit;
        LlmPostProcessingPrompt = string.IsNullOrWhiteSpace(settings.LlmPostProcessingPrompt)
            ? LlmPostProcessingCatalog.DefaultPrompt
            : settings.LlmPostProcessingPrompt;
        ReplaceModels(AvailableCerebrasModels, settings.FetchedCerebrasModels?.Count > 0 ? settings.FetchedCerebrasModels : LlmPostProcessingCatalog.CerebrasModels);
        ReplaceModels(AvailableLlmGroqModels, settings.FetchedLlmGroqModels?.Count > 0 ? settings.FetchedLlmGroqModels : LlmPostProcessingCatalog.GroqModels);
        _fetchedCerebrasModels = settings.FetchedCerebrasModels?.Count > 0 ? [.. settings.FetchedCerebrasModels] : null;
        _fetchedLlmGroqModels = settings.FetchedLlmGroqModels?.Count > 0 ? [.. settings.FetchedLlmGroqModels] : null;
        EnsureModelAvailable(AvailableCerebrasModels, settings.CerebrasModel, LlmPostProcessingCatalog.DefaultCerebrasModel);
        EnsureModelAvailable(AvailableLlmGroqModels, settings.LlmGroqModel, LlmPostProcessingCatalog.DefaultGroqModel);
        SelectedCerebrasModel = SelectPreferredModel(AvailableCerebrasModels, settings.CerebrasModel, LlmPostProcessingCatalog.DefaultCerebrasModel);
        SelectedLlmGroqModel = SelectPreferredModel(AvailableLlmGroqModels, settings.LlmGroqModel, LlmPostProcessingCatalog.DefaultGroqModel);
        SelectedGroqModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Groq, settings.GroqModel);
        GroqLanguage = TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.Groq, settings.GroqLanguage);
        SelectedFireworksModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Fireworks, settings.FireworksModel);
        FireworksLanguage = TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.Fireworks, settings.FireworksLanguage);
        SelectedDeepgramModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Deepgram, settings.DeepgramModel, settings.DeepgramStreamingEnabled);
        DeepgramVadSilenceThresholdSeconds = TranscriptionProviderCatalog.NormalizeDeepgramVadSilenceThresholdSeconds(settings.DeepgramVadSilenceThresholdSeconds);
        SelectedMistralModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Mistral, settings.MistralModel, settings.MistralStreamingEnabled);
        SelectedCohereModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Cohere, settings.CohereModel);
        CohereLanguage = TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.Cohere, settings.CohereLanguage);
        SelectedElevenLabsModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.ElevenLabs, settings.ElevenLabsModel, settings.ElevenLabsStreamingEnabled);
        ElevenLabsLanguage = TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.ElevenLabs, settings.ElevenLabsLanguage);
        ElevenLabsVadSilenceThresholdSeconds = TranscriptionProviderCatalog.NormalizeElevenLabsVadSilenceThresholdSeconds(settings.ElevenLabsVadSilenceThresholdSeconds);
        _pendingHotkey = settings.Hotkey;
        _pendingPasteLastTranscriptHotkey = settings.PasteLastTranscriptHotkey;
        _pendingOpenHistoryHotkey = settings.OpenHistoryHotkey;
        RecordingHotkeyDisplay = _pendingHotkey.ToDisplayString();
        PasteLastTranscriptHotkeyDisplay = _pendingPasteLastTranscriptHotkey.ToDisplayString();
        OpenHistoryHotkeyDisplay = _pendingOpenHistoryHotkey.ToDisplayString();
        RefreshHotkeyWarnings();
    }

    private AppSettings CreateSettingsSnapshot()
    {
        return new AppSettings
        {
            SelectedInputDeviceId = SelectedInputDevice?.Id,
            Provider = SelectedProvider,
            LlmPostProcessingEnabled = LlmPostProcessingEnabled,
            LlmPostProcessingProvider = SelectedLlmPostProcessingProvider,
            GroqApiKey = GroqApiKey.Trim(),
            CerebrasApiKey = CerebrasApiKey.Trim(),
            LlmGroqApiKey = LlmGroqApiKey.Trim(),
            FireworksApiKey = FireworksApiKey.Trim(),
            DeepgramApiKey = DeepgramApiKey.Trim(),
            MistralApiKey = MistralApiKey.Trim(),
            CohereApiKey = CohereApiKey.Trim(),
            ElevenLabsApiKey = ElevenLabsApiKey.Trim(),
            Hotkey = _pendingHotkey,
            PasteLastTranscriptHotkey = _pendingPasteLastTranscriptHotkey,
            OpenHistoryHotkey = _pendingOpenHistoryHotkey,
            TranscriptHistoryLimit = TranscriptHistoryLimit,
            PushToTalk = PushToTalk,
            LaunchAtStartup = LaunchAtStartup,
            SoundFeedbackEnabled = SoundFeedbackEnabled,
            LlmPostProcessingPrompt = string.IsNullOrWhiteSpace(LlmPostProcessingPrompt)
                ? LlmPostProcessingCatalog.DefaultPrompt
                : LlmPostProcessingPrompt.Trim(),
            FetchedCerebrasModels = _fetchedCerebrasModels,
            FetchedLlmGroqModels = _fetchedLlmGroqModels,
            CerebrasModel = string.IsNullOrWhiteSpace(SelectedCerebrasModel) ? AvailableCerebrasModels[0] : SelectedCerebrasModel,
            LlmGroqModel = string.IsNullOrWhiteSpace(SelectedLlmGroqModel) ? AvailableLlmGroqModels[0] : SelectedLlmGroqModel,
            GroqModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Groq, SelectedGroqModel),
            GroqLanguage = TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.Groq, GroqLanguage),
            FireworksModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Fireworks, SelectedFireworksModel),
            FireworksLanguage = TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.Fireworks, FireworksLanguage),
            DeepgramModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Deepgram, SelectedDeepgramModel, DeepgramStreamingEnabled),
            DeepgramLanguage = TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.Deepgram, null),
            DeepgramStreamingEnabled = DeepgramStreamingEnabled,
            DeepgramVadSilenceThresholdSeconds = TranscriptionProviderCatalog.NormalizeDeepgramVadSilenceThresholdSeconds(DeepgramVadSilenceThresholdSeconds),
            MistralModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Mistral, SelectedMistralModel, MistralStreamingEnabled),
            MistralStreamingEnabled = MistralStreamingEnabled,
            MistralRealtimeMode = MistralRealtimeMode,
            CohereModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Cohere, SelectedCohereModel),
            CohereLanguage = TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.Cohere, CohereLanguage),
            ElevenLabsModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.ElevenLabs, SelectedElevenLabsModel, ElevenLabsStreamingEnabled),
            ElevenLabsStreamingEnabled = ElevenLabsStreamingEnabled,
            ElevenLabsLanguage = TranscriptionProviderCatalog.NormalizeLanguage(TranscriptionProvider.ElevenLabs, ElevenLabsLanguage),
            ElevenLabsVadSilenceThresholdSeconds = TranscriptionProviderCatalog.NormalizeElevenLabsVadSilenceThresholdSeconds(ElevenLabsVadSilenceThresholdSeconds),
            HasCompletedInitialSetup = true,
        };
    }

    private async Task LoadHistoryAsync()
    {
        var entries = await _transcriptHistoryStore.GetEntriesAsync();
        TranscriptHistoryEntries.Clear();

        foreach (var entry in entries)
        {
            TranscriptHistoryEntries.Add(new TranscriptHistoryItemViewModel(entry));
        }

        IsHistoryEmpty = TranscriptHistoryEntries.Count == 0;
    }

    private void RefreshHotkeyWarnings()
    {
        var validation = HotkeyValidationService.Validate(_pendingHotkey, _pendingPasteLastTranscriptHotkey, _pendingOpenHistoryHotkey);
        HotkeyWarningMessage = string.Join(" ", validation.Warnings);
    }

    private async void OnHistoryChanged(object? sender, EventArgs e)
    {
        await RunOnUiThreadAsync(LoadHistoryAsync);
    }

    private async void OnDictationStatusChanged(object? sender, DictationStatusChangedEventArgs e)
    {
        await RunOnUiThreadAsync(() =>
        {
            DictationState = e.State;
            StatusMessage = e.Message;
            CanCancelTranscription = e.CanCancel;
            return Task.CompletedTask;
        });
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        var dispatcherQueue = _uiDispatcherQueueAccessor.DispatcherQueue;

        if (dispatcherQueue is null)
        {
            return action();
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    completionSource.TrySetResult();
                }
                catch (Exception exception)
                {
                    completionSource.TrySetException(exception);
                }
            }))
        {
            return action();
        }

        return completionSource.Task;
    }

    private static int NormalizeTranscriptHistoryLimit(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 200;
        }

        return Math.Clamp((int)Math.Round(value), 0, 500);
    }

    private static bool AreSettingsEquivalent(AppSettings left, AppSettings right)
    {
        return string.Equals(left.SelectedInputDeviceId, right.SelectedInputDeviceId, StringComparison.Ordinal) &&
               left.Provider == right.Provider &&
               left.LlmPostProcessingEnabled == right.LlmPostProcessingEnabled &&
               left.LlmPostProcessingProvider == right.LlmPostProcessingProvider &&
               string.Equals(left.GroqApiKey, right.GroqApiKey, StringComparison.Ordinal) &&
               string.Equals(left.CerebrasApiKey, right.CerebrasApiKey, StringComparison.Ordinal) &&
               string.Equals(left.LlmGroqApiKey, right.LlmGroqApiKey, StringComparison.Ordinal) &&
               string.Equals(left.FireworksApiKey, right.FireworksApiKey, StringComparison.Ordinal) &&
               string.Equals(left.DeepgramApiKey, right.DeepgramApiKey, StringComparison.Ordinal) &&
               string.Equals(left.MistralApiKey, right.MistralApiKey, StringComparison.Ordinal) &&
               string.Equals(left.CohereApiKey, right.CohereApiKey, StringComparison.Ordinal) &&
               string.Equals(left.ElevenLabsApiKey, right.ElevenLabsApiKey, StringComparison.Ordinal) &&
               Equals(left.Hotkey, right.Hotkey) &&
               Equals(left.PasteLastTranscriptHotkey, right.PasteLastTranscriptHotkey) &&
               Equals(left.OpenHistoryHotkey, right.OpenHistoryHotkey) &&
               left.TranscriptHistoryLimit == right.TranscriptHistoryLimit &&
               left.PushToTalk == right.PushToTalk &&
               left.LaunchAtStartup == right.LaunchAtStartup &&
               left.SoundFeedbackEnabled == right.SoundFeedbackEnabled &&
               string.Equals(left.LlmPostProcessingPrompt, right.LlmPostProcessingPrompt, StringComparison.Ordinal) &&
               ModelListsAreEquivalent(left.FetchedCerebrasModels, right.FetchedCerebrasModels) &&
               ModelListsAreEquivalent(left.FetchedLlmGroqModels, right.FetchedLlmGroqModels) &&
               string.Equals(left.CerebrasModel, right.CerebrasModel, StringComparison.Ordinal) &&
               string.Equals(left.LlmGroqModel, right.LlmGroqModel, StringComparison.Ordinal) &&
               string.Equals(left.GroqModel, right.GroqModel, StringComparison.Ordinal) &&
               string.Equals(left.GroqLanguage, right.GroqLanguage, StringComparison.Ordinal) &&
               string.Equals(left.FireworksModel, right.FireworksModel, StringComparison.Ordinal) &&
               string.Equals(left.FireworksLanguage, right.FireworksLanguage, StringComparison.Ordinal) &&
               string.Equals(left.DeepgramModel, right.DeepgramModel, StringComparison.Ordinal) &&
               string.Equals(left.DeepgramLanguage, right.DeepgramLanguage, StringComparison.Ordinal) &&
               left.DeepgramStreamingEnabled == right.DeepgramStreamingEnabled &&
               Math.Abs(left.DeepgramVadSilenceThresholdSeconds - right.DeepgramVadSilenceThresholdSeconds) < 0.0001 &&
               string.Equals(left.MistralModel, right.MistralModel, StringComparison.Ordinal) &&
               left.MistralStreamingEnabled == right.MistralStreamingEnabled &&
               left.MistralRealtimeMode == right.MistralRealtimeMode &&
               string.Equals(left.CohereModel, right.CohereModel, StringComparison.Ordinal) &&
               string.Equals(left.CohereLanguage, right.CohereLanguage, StringComparison.Ordinal) &&
               string.Equals(left.ElevenLabsModel, right.ElevenLabsModel, StringComparison.Ordinal) &&
               left.ElevenLabsStreamingEnabled == right.ElevenLabsStreamingEnabled &&
               string.Equals(left.ElevenLabsLanguage, right.ElevenLabsLanguage, StringComparison.Ordinal) &&
               Math.Abs(left.ElevenLabsVadSilenceThresholdSeconds - right.ElevenLabsVadSilenceThresholdSeconds) < 0.0001 &&
               left.HasCompletedInitialSetup == right.HasCompletedInitialSetup;
    }

    private static void ReplaceModels(ObservableCollection<string> target, IReadOnlyList<string> source)
    {
        target.Clear();
        foreach (var model in source)
        {
            target.Add(model);
        }
    }

    private static void EnsureModelAvailable(ObservableCollection<string> models, string? selectedModel, string fallbackModel)
    {
        var normalizedSelectedModel = string.IsNullOrWhiteSpace(selectedModel) ? fallbackModel : selectedModel.Trim();
        if (models.Any(model => string.Equals(model, normalizedSelectedModel, StringComparison.Ordinal)))
        {
            return;
        }

        models.Add(normalizedSelectedModel);
    }

    private static string SelectPreferredModel(ObservableCollection<string> models, string? selectedModel, string fallbackModel)
    {
        var normalizedSelectedModel = string.IsNullOrWhiteSpace(selectedModel) ? fallbackModel : selectedModel.Trim();
        return models.FirstOrDefault(model => string.Equals(model, normalizedSelectedModel, StringComparison.Ordinal))
            ?? models.FirstOrDefault()
            ?? fallbackModel;
    }

    private static string SelectPreferredProviderModel(IReadOnlyList<string> models, string? selectedModel, string fallbackModel)
    {
        var normalizedSelectedModel = string.IsNullOrWhiteSpace(selectedModel) ? fallbackModel : selectedModel.Trim();
        return models.FirstOrDefault(model => string.Equals(model, normalizedSelectedModel, StringComparison.Ordinal))
            ?? models.FirstOrDefault()
            ?? fallbackModel;
    }

    private static bool ModelListsAreEquivalent(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || left.Count == 0)
        {
            return right is null || right.Count == 0;
        }

        if (right is null || left.Count != right.Count)
        {
            return false;
        }

        return left.SequenceEqual(right, StringComparer.Ordinal);
    }
}
