using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using timbre.Interfaces;
using timbre.Models;
using timbre.Services;

namespace timbre.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
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

    private readonly IAppSettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly ITranscriptHistoryStore _transcriptHistoryStore;
    private readonly IClipboardPasteService _clipboardPasteService;
    private readonly IDictationController _dictationController;
    private readonly IUiDispatcherQueueAccessor _uiDispatcherQueueAccessor;

    private AudioInputDevice? _selectedInputDevice;
    private TranscriptionProvider _selectedProvider = TranscriptionProvider.Groq;
    private string _groqApiKey = string.Empty;
    private string _fireworksApiKey = string.Empty;
    private bool _pushToTalk = true;
    private int _transcriptHistoryLimit = 20;
    private double _transcriptHistoryLimitValue = 20;
    private string _selectedGroqModel = GroqModels[0];
    private string _selectedFireworksModel = FireworksModels[0];
    private string _groqLanguage = "en";
    private string _fireworksLanguage = "en";
    private string _recordingHotkeyDisplay = HotkeyBinding.Default.ToDisplayString();
    private string _pasteLastTranscriptHotkeyDisplay = HotkeyBinding.PasteLastTranscriptDefault.ToDisplayString();
    private string _statusMessage = string.Empty;
    private string _hotkeyWarningMessage = string.Empty;
    private bool _canCancelTranscription;
    private bool _isHistoryEmpty = true;
    private DictationState _dictationState = DictationState.Idle;
    private bool _isInitialized;
    private HotkeyBinding _pendingHotkey = HotkeyBinding.Default;
    private HotkeyBinding _pendingPasteLastTranscriptHotkey = HotkeyBinding.PasteLastTranscriptDefault;

    public MainViewModel(
        IAppSettingsStore settingsStore,
        IAudioDeviceService audioDeviceService,
        ITranscriptHistoryStore transcriptHistoryStore,
        IClipboardPasteService clipboardPasteService,
        IDictationController dictationController,
        IUiDispatcherQueueAccessor uiDispatcherQueueAccessor)
    {
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _transcriptHistoryStore = transcriptHistoryStore;
        _clipboardPasteService = clipboardPasteService;
        _dictationController = dictationController;
        _uiDispatcherQueueAccessor = uiDispatcherQueueAccessor;

        _transcriptHistoryStore.HistoryChanged += OnHistoryChanged;
        _dictationController.StatusChanged += OnDictationStatusChanged;
    }

    public event Action<AppSettings>? SettingsSaved;

    public ObservableCollection<AudioInputDevice> InputDevices { get; } = [];

    public ObservableCollection<TranscriptHistoryItemViewModel> TranscriptHistoryEntries { get; } = [];

    public IReadOnlyList<string> AvailableGroqModels => GroqModels;

    public IReadOnlyList<string> AvailableFireworksModels => FireworksModels;

    public TranscriptionProvider SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                OnPropertyChanged(nameof(IsGroqSelected));
                OnPropertyChanged(nameof(IsFireworksSelected));
                OnPropertyChanged(nameof(GroqSettingsVisibility));
                OnPropertyChanged(nameof(FireworksSettingsVisibility));
            }
        }
    }

    public bool IsGroqSelected => SelectedProvider == TranscriptionProvider.Groq;

    public bool IsFireworksSelected => SelectedProvider == TranscriptionProvider.Fireworks;

    public Visibility GroqSettingsVisibility => IsGroqSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FireworksSettingsVisibility => IsFireworksSelected ? Visibility.Visible : Visibility.Collapsed;

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

    public string FireworksApiKey
    {
        get => _fireworksApiKey;
        set => SetProperty(ref _fireworksApiKey, value);
    }

    public bool PushToTalk
    {
        get => _pushToTalk;
        set => SetProperty(ref _pushToTalk, value);
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
        var validation = HotkeyValidationService.Validate(_pendingHotkey, _pendingPasteLastTranscriptHotkey);
        HotkeyWarningMessage = string.Join(" ", validation.Warnings);

        if (validation.HasErrors)
        {
            StatusMessage = string.Join(" ", validation.Errors);
            return false;
        }

        var settings = new AppSettings
        {
            SelectedInputDeviceId = SelectedInputDevice?.Id,
            Provider = SelectedProvider,
            GroqApiKey = GroqApiKey.Trim(),
            FireworksApiKey = FireworksApiKey.Trim(),
            Hotkey = _pendingHotkey,
            PasteLastTranscriptHotkey = _pendingPasteLastTranscriptHotkey,
            TranscriptHistoryLimit = TranscriptHistoryLimit,
            PushToTalk = PushToTalk,
            GroqModel = string.IsNullOrWhiteSpace(SelectedGroqModel) ? GroqModels[0] : SelectedGroqModel,
            GroqLanguage = NormalizeLanguage(GroqLanguage),
            FireworksModel = string.IsNullOrWhiteSpace(SelectedFireworksModel) ? FireworksModels[0] : SelectedFireworksModel,
            FireworksLanguage = NormalizeLanguage(FireworksLanguage),
            HasCompletedInitialSetup = true,
        };

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

    public void Dispose()
    {
        _transcriptHistoryStore.HistoryChanged -= OnHistoryChanged;
        _dictationController.StatusChanged -= OnDictationStatusChanged;
    }

    private void ApplySettings(AppSettings settings)
    {
        SelectedProvider = settings.Provider;
        GroqApiKey = settings.GroqApiKey ?? string.Empty;
        FireworksApiKey = settings.FireworksApiKey ?? string.Empty;
        PushToTalk = settings.PushToTalk;
        TranscriptHistoryLimit = settings.TranscriptHistoryLimit;
        TranscriptHistoryLimitValue = settings.TranscriptHistoryLimit;
        SelectedGroqModel = GroqModels.FirstOrDefault(model => model == settings.GroqModel) ?? GroqModels[0];
        GroqLanguage = NormalizeLanguage(settings.GroqLanguage);
        SelectedFireworksModel = FireworksModels.FirstOrDefault(model => model == settings.FireworksModel) ?? FireworksModels[0];
        FireworksLanguage = NormalizeLanguage(settings.FireworksLanguage);
        _pendingHotkey = settings.Hotkey;
        _pendingPasteLastTranscriptHotkey = settings.PasteLastTranscriptHotkey;
        RecordingHotkeyDisplay = _pendingHotkey.ToDisplayString();
        PasteLastTranscriptHotkeyDisplay = _pendingPasteLastTranscriptHotkey.ToDisplayString();
        RefreshHotkeyWarnings();
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
        var validation = HotkeyValidationService.Validate(_pendingHotkey, _pendingPasteLastTranscriptHotkey);
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
            return 20;
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
}
