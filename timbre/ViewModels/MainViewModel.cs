using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using timbre.Interfaces;
using timbre.Models;
using timbre.Services;

namespace timbre.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<string> CerebrasModels = LlmPostProcessingCatalog.CerebrasModels;
    private static readonly IReadOnlyList<string> LlmGroqModels = LlmPostProcessingCatalog.GroqModels;
    private static readonly string[] GroqModels =
    [
        "whisper-large-v3-turbo",
        "whisper-large-v3",
    ];
    private static readonly string[] FireworksModels =
    [
        "whisper-v3-turbo",
        "whisper-v3",
    ];
    private static readonly string[] DeepgramStreamingModels =
    [
        "flux",
    ];
    private static readonly string[] DeepgramBatchModels =
    [
        "nova-3",
    ];
    private static readonly string[] CohereModels =
    [
        "cohere-transcribe-03-2026",
    ];

    private readonly IAppSettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly ITranscriptHistoryStore _transcriptHistoryStore;
    private readonly IClipboardPasteService _clipboardPasteService;
    private readonly IDictationController _dictationController;
    private readonly ILaunchAtStartupService _launchAtStartupService;
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
    private bool _deepgramStreamingEnabled = true;
    private bool _mistralRealtimeEnabled;
    private bool _pushToTalk = true;
    private bool _launchAtStartup;
    private bool _soundFeedbackEnabled = true;
    private int _transcriptHistoryLimit = 200;
    private double _transcriptHistoryLimitValue = 200;
    private string _llmPostProcessingPrompt = LlmPostProcessingCatalog.DefaultPrompt;
    private string _selectedCerebrasModel = LlmPostProcessingCatalog.DefaultCerebrasModel;
    private string _selectedLlmGroqModel = LlmPostProcessingCatalog.DefaultGroqModel;
    private string _selectedGroqModel = GroqModels[0];
    private string _selectedFireworksModel = FireworksModels[0];
    private string _groqLanguage = "en";
    private string _fireworksLanguage = "en";
    private string _selectedDeepgramModel = DeepgramStreamingModels[0];
    private MistralRealtimeMode _mistralRealtimeMode = MistralRealtimeMode.Fast;
    private string _selectedCohereModel = CohereModels[0];
    private string _cohereLanguage = "en";
    private string _recordingHotkeyDisplay = HotkeyBinding.Default.ToDisplayString();
    private string _pasteLastTranscriptHotkeyDisplay = HotkeyBinding.PasteLastTranscriptDefault.ToDisplayString();
    private string _openHistoryHotkeyDisplay = HotkeyBinding.OpenHistoryDefault.ToDisplayString();
    private string _statusMessage = string.Empty;
    private string _hotkeyWarningMessage = string.Empty;
    private bool _canCancelTranscription;
    private bool _isHistoryEmpty = true;
    private DictationState _dictationState = DictationState.Idle;
    private bool _isInitialized;
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
        IUiDispatcherQueueAccessor uiDispatcherQueueAccessor)
    {
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _transcriptHistoryStore = transcriptHistoryStore;
        _clipboardPasteService = clipboardPasteService;
        _dictationController = dictationController;
        _launchAtStartupService = launchAtStartupService;
        _uiDispatcherQueueAccessor = uiDispatcherQueueAccessor;

        _transcriptHistoryStore.HistoryChanged += OnHistoryChanged;
        _dictationController.StatusChanged += OnDictationStatusChanged;
    }

    public event Action<AppSettings>? SettingsSaved;

    public ObservableCollection<AudioInputDevice> InputDevices { get; } = [];

    public ObservableCollection<TranscriptHistoryItemViewModel> TranscriptHistoryEntries { get; } = [];

    public IReadOnlyList<string> AvailableCerebrasModels => CerebrasModels;

    public IReadOnlyList<string> AvailableLlmGroqModels => LlmGroqModels;

    public IReadOnlyList<string> AvailableGroqModels => GroqModels;

    public IReadOnlyList<string> AvailableFireworksModels => FireworksModels;

    public IReadOnlyList<string> AvailableDeepgramModels => DeepgramStreamingEnabled ? DeepgramStreamingModels : DeepgramBatchModels;

    public IReadOnlyList<string> AvailableCohereModels => CohereModels;

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
                OnPropertyChanged(nameof(GroqSettingsVisibility));
                OnPropertyChanged(nameof(FireworksSettingsVisibility));
                OnPropertyChanged(nameof(DeepgramSettingsVisibility));
                OnPropertyChanged(nameof(MistralSettingsVisibility));
                OnPropertyChanged(nameof(CohereSettingsVisibility));
            }
        }
    }

    public bool IsGroqSelected => SelectedProvider == TranscriptionProvider.Groq;

    public bool IsFireworksSelected => SelectedProvider == TranscriptionProvider.Fireworks;

    public bool IsDeepgramSelected => SelectedProvider == TranscriptionProvider.Deepgram;

    public bool IsMistralSelected => SelectedProvider == TranscriptionProvider.Mistral;

    public bool IsCohereSelected => SelectedProvider == TranscriptionProvider.Cohere;

    public Visibility GroqSettingsVisibility => IsGroqSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FireworksSettingsVisibility => IsFireworksSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeepgramSettingsVisibility => IsDeepgramSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MistralSettingsVisibility => IsMistralSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CohereSettingsVisibility => IsCohereSelected ? Visibility.Visible : Visibility.Collapsed;

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

    public bool DeepgramStreamingEnabled
    {
        get => _deepgramStreamingEnabled;
        set
        {
            if (SetProperty(ref _deepgramStreamingEnabled, value))
            {
                OnPropertyChanged(nameof(AvailableDeepgramModels));

                var defaultModel = GetDefaultDeepgramModel(value);
                if (!AvailableDeepgramModels.Any(model => string.Equals(model, SelectedDeepgramModel, StringComparison.Ordinal)))
                {
                    SelectedDeepgramModel = defaultModel;
                }
            }
        }
    }

    public bool MistralRealtimeEnabled
    {
        get => _mistralRealtimeEnabled;
        set => SetProperty(ref _mistralRealtimeEnabled, value);
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
        set => SetProperty(ref _selectedDeepgramModel, value);
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
        DeepgramStreamingEnabled = settings.DeepgramStreamingEnabled;
        MistralRealtimeEnabled = settings.MistralRealtimeEnabled;
        MistralRealtimeMode = settings.MistralRealtimeMode;
        PushToTalk = settings.PushToTalk;
        LaunchAtStartup = settings.LaunchAtStartup;
        SoundFeedbackEnabled = settings.SoundFeedbackEnabled;
        TranscriptHistoryLimit = settings.TranscriptHistoryLimit;
        TranscriptHistoryLimitValue = settings.TranscriptHistoryLimit;
        LlmPostProcessingPrompt = string.IsNullOrWhiteSpace(settings.LlmPostProcessingPrompt)
            ? LlmPostProcessingCatalog.DefaultPrompt
            : settings.LlmPostProcessingPrompt;
        SelectedCerebrasModel = CerebrasModels.FirstOrDefault(model => model == settings.CerebrasModel) ?? CerebrasModels[0];
        SelectedLlmGroqModel = LlmGroqModels.FirstOrDefault(model => model == settings.LlmGroqModel) ?? LlmGroqModels[0];
        SelectedGroqModel = GroqModels.FirstOrDefault(model => model == settings.GroqModel) ?? GroqModels[0];
        GroqLanguage = NormalizeLanguage(settings.GroqLanguage);
        SelectedFireworksModel = FireworksModels.FirstOrDefault(model => model == settings.FireworksModel) ?? FireworksModels[0];
        FireworksLanguage = NormalizeLanguage(settings.FireworksLanguage);
        SelectedDeepgramModel = AvailableDeepgramModels.FirstOrDefault(model => model == settings.DeepgramModel) ?? GetDefaultDeepgramModel(settings.DeepgramStreamingEnabled);
        SelectedCohereModel = CohereModels.FirstOrDefault(model => model == settings.CohereModel) ?? CohereModels[0];
        CohereLanguage = NormalizeLanguage(settings.CohereLanguage);
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
            CerebrasModel = string.IsNullOrWhiteSpace(SelectedCerebrasModel) ? CerebrasModels[0] : SelectedCerebrasModel,
            LlmGroqModel = string.IsNullOrWhiteSpace(SelectedLlmGroqModel) ? LlmGroqModels[0] : SelectedLlmGroqModel,
            GroqModel = string.IsNullOrWhiteSpace(SelectedGroqModel) ? GroqModels[0] : SelectedGroqModel,
            GroqLanguage = NormalizeLanguage(GroqLanguage),
            FireworksModel = string.IsNullOrWhiteSpace(SelectedFireworksModel) ? FireworksModels[0] : SelectedFireworksModel,
            FireworksLanguage = NormalizeLanguage(FireworksLanguage),
            DeepgramModel = string.IsNullOrWhiteSpace(SelectedDeepgramModel) ? GetDefaultDeepgramModel(DeepgramStreamingEnabled) : SelectedDeepgramModel,
            DeepgramLanguage = "en",
            DeepgramStreamingEnabled = DeepgramStreamingEnabled,
            MistralRealtimeEnabled = MistralRealtimeEnabled,
            MistralRealtimeMode = MistralRealtimeMode,
            CohereModel = string.IsNullOrWhiteSpace(SelectedCohereModel) ? CohereModels[0] : SelectedCohereModel,
            CohereLanguage = NormalizeLanguage(CohereLanguage),
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

    private static string NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "en";
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized == "auto" ? "auto" : normalized;
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
               Equals(left.Hotkey, right.Hotkey) &&
               Equals(left.PasteLastTranscriptHotkey, right.PasteLastTranscriptHotkey) &&
               Equals(left.OpenHistoryHotkey, right.OpenHistoryHotkey) &&
               left.TranscriptHistoryLimit == right.TranscriptHistoryLimit &&
               left.PushToTalk == right.PushToTalk &&
               left.LaunchAtStartup == right.LaunchAtStartup &&
               left.SoundFeedbackEnabled == right.SoundFeedbackEnabled &&
               string.Equals(left.LlmPostProcessingPrompt, right.LlmPostProcessingPrompt, StringComparison.Ordinal) &&
               string.Equals(left.CerebrasModel, right.CerebrasModel, StringComparison.Ordinal) &&
               string.Equals(left.LlmGroqModel, right.LlmGroqModel, StringComparison.Ordinal) &&
               string.Equals(left.GroqModel, right.GroqModel, StringComparison.Ordinal) &&
               string.Equals(left.GroqLanguage, right.GroqLanguage, StringComparison.Ordinal) &&
               string.Equals(left.FireworksModel, right.FireworksModel, StringComparison.Ordinal) &&
               string.Equals(left.FireworksLanguage, right.FireworksLanguage, StringComparison.Ordinal) &&
               string.Equals(left.DeepgramModel, right.DeepgramModel, StringComparison.Ordinal) &&
               string.Equals(left.DeepgramLanguage, right.DeepgramLanguage, StringComparison.Ordinal) &&
               left.DeepgramStreamingEnabled == right.DeepgramStreamingEnabled &&
               left.MistralRealtimeEnabled == right.MistralRealtimeEnabled &&
               left.MistralRealtimeMode == right.MistralRealtimeMode &&
               string.Equals(left.CohereModel, right.CohereModel, StringComparison.Ordinal) &&
               string.Equals(left.CohereLanguage, right.CohereLanguage, StringComparison.Ordinal) &&
               left.HasCompletedInitialSetup == right.HasCompletedInitialSetup;
    }

    private static string GetDefaultDeepgramModel(bool streamingEnabled)
    {
        return streamingEnabled ? DeepgramStreamingModels[0] : DeepgramBatchModels[0];
    }
}
