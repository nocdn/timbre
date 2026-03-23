using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class AppSettingsStore : IAppSettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("timbre-settings");
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("whisper-windows-settings");
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private readonly string _settingsPath;
    private AppSettings _currentSettings = new();
    private bool _hasLoadedSettings;

    public AppSettingsStore()
    {
        var settingsDirectory = DiagnosticsLogger.GetAppDataDirectory();
        MigrateLegacySettingsFile(settingsDirectory);

        Directory.CreateDirectory(settingsDirectory);
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
    }

    public AppSettings CurrentSettings => _currentSettings;

    public async Task<AppSettings> LoadAsync(bool forceReload = false, CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            if (!forceReload && _hasLoadedSettings)
            {
                return _currentSettings;
            }

            DiagnosticsLogger.Info($"Loading settings from '{_settingsPath}'.");
            if (!File.Exists(_settingsPath))
            {
                DiagnosticsLogger.Info("Settings file does not exist yet.");
                _currentSettings = new AppSettings();
                _hasLoadedSettings = true;
                return _currentSettings;
            }

            var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken);
            var storedSettings = JsonSerializer.Deserialize<StoredSettings>(json, _serializerOptions) ?? new StoredSettings();
            var deepgramStreamingEnabled = storedSettings.DeepgramStreamingEnabled ?? InferDeepgramStreamingEnabled(storedSettings.DeepgramModel);
            var deepgramModel = NormalizeDeepgramModel(storedSettings.DeepgramModel, deepgramStreamingEnabled);

            _currentSettings = new AppSettings
            {
                SelectedInputDeviceId = storedSettings.SelectedInputDeviceId,
                Provider = storedSettings.Provider ?? TranscriptionProvider.Groq,
                GroqApiKey = Decrypt(storedSettings.EncryptedGroqApiKey),
                FireworksApiKey = Decrypt(storedSettings.EncryptedFireworksApiKey),
                DeepgramApiKey = Decrypt(storedSettings.EncryptedDeepgramApiKey),
                MistralApiKey = Decrypt(storedSettings.EncryptedMistralApiKey),
                Hotkey = storedSettings.Hotkey ?? HotkeyBinding.Default,
                PasteLastTranscriptHotkey = storedSettings.PasteLastTranscriptHotkey ?? HotkeyBinding.PasteLastTranscriptDefault,
                OpenHistoryHotkey = storedSettings.OpenHistoryHotkey ?? HotkeyBinding.OpenHistoryDefault,
                TranscriptHistoryLimit = storedSettings.TranscriptHistoryLimit is null or < 0 ? 200 : storedSettings.TranscriptHistoryLimit.Value,
                PushToTalk = storedSettings.PushToTalk ?? true,
                LaunchAtStartup = storedSettings.LaunchAtStartup ?? false,
                SoundFeedbackEnabled = storedSettings.SoundFeedbackEnabled ?? true,
                GroqModel = string.IsNullOrWhiteSpace(storedSettings.GroqModel)
                    ? "whisper-large-v3-turbo"
                    : storedSettings.GroqModel,
                GroqLanguage = NormalizeLanguage(storedSettings.GroqLanguage),
                FireworksModel = string.IsNullOrWhiteSpace(storedSettings.FireworksModel)
                    ? "whisper-v3-turbo"
                    : storedSettings.FireworksModel,
                FireworksLanguage = NormalizeLanguage(storedSettings.FireworksLanguage),
                DeepgramModel = deepgramModel,
                DeepgramLanguage = NormalizeLanguage(storedSettings.DeepgramLanguage),
                DeepgramStreamingEnabled = deepgramStreamingEnabled,
                MistralRealtimeEnabled = storedSettings.MistralRealtimeEnabled ?? false,
                MistralRealtimeMode = NormalizeMistralRealtimeMode(storedSettings.MistralRealtimeMode),
                HasCompletedInitialSetup = storedSettings.HasCompletedInitialSetup ?? false,
            };

            DiagnosticsLogger.Info(
                $"Settings loaded. SelectedInputDeviceId='{_currentSettings.SelectedInputDeviceId}', Provider='{_currentSettings.Provider}', HasGroqApiKey={!string.IsNullOrWhiteSpace(_currentSettings.GroqApiKey)}, HasFireworksApiKey={!string.IsNullOrWhiteSpace(_currentSettings.FireworksApiKey)}, HasDeepgramApiKey={!string.IsNullOrWhiteSpace(_currentSettings.DeepgramApiKey)}, HasMistralApiKey={!string.IsNullOrWhiteSpace(_currentSettings.MistralApiKey)}, Hotkey='{_currentSettings.Hotkey.ToDisplayString()}', PasteLastTranscriptHotkey='{_currentSettings.PasteLastTranscriptHotkey.ToDisplayString()}', OpenHistoryHotkey='{_currentSettings.OpenHistoryHotkey.ToDisplayString()}', TranscriptHistoryLimit={_currentSettings.TranscriptHistoryLimit}, PushToTalk={_currentSettings.PushToTalk}, LaunchAtStartup={_currentSettings.LaunchAtStartup}, SoundFeedbackEnabled={_currentSettings.SoundFeedbackEnabled}, GroqModel='{_currentSettings.GroqModel}', GroqLanguage='{_currentSettings.GroqLanguage}', FireworksModel='{_currentSettings.FireworksModel}', FireworksLanguage='{_currentSettings.FireworksLanguage}', DeepgramModel='{_currentSettings.DeepgramModel}', DeepgramLanguage='{_currentSettings.DeepgramLanguage}', DeepgramStreamingEnabled={_currentSettings.DeepgramStreamingEnabled}, MistralRealtimeEnabled={_currentSettings.MistralRealtimeEnabled}, MistralRealtimeMode={_currentSettings.MistralRealtimeMode}, HasCompletedInitialSetup={_currentSettings.HasCompletedInitialSetup}.");

            _hasLoadedSettings = true;
            return _currentSettings;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The saved settings file is invalid. Open Settings and save it again.", exception);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("A saved API key could not be read. Open Settings and save it again.", exception);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            DiagnosticsLogger.Info(
                $"Saving settings. SelectedInputDeviceId='{settings.SelectedInputDeviceId}', Provider='{settings.Provider}', HasGroqApiKey={!string.IsNullOrWhiteSpace(settings.GroqApiKey)}, HasFireworksApiKey={!string.IsNullOrWhiteSpace(settings.FireworksApiKey)}, HasDeepgramApiKey={!string.IsNullOrWhiteSpace(settings.DeepgramApiKey)}, HasMistralApiKey={!string.IsNullOrWhiteSpace(settings.MistralApiKey)}, Hotkey='{settings.Hotkey.ToDisplayString()}', PasteLastTranscriptHotkey='{settings.PasteLastTranscriptHotkey.ToDisplayString()}', OpenHistoryHotkey='{settings.OpenHistoryHotkey.ToDisplayString()}', TranscriptHistoryLimit={settings.TranscriptHistoryLimit}, PushToTalk={settings.PushToTalk}, LaunchAtStartup={settings.LaunchAtStartup}, SoundFeedbackEnabled={settings.SoundFeedbackEnabled}, GroqModel='{settings.GroqModel}', GroqLanguage='{settings.GroqLanguage}', FireworksModel='{settings.FireworksModel}', FireworksLanguage='{settings.FireworksLanguage}', DeepgramModel='{settings.DeepgramModel}', DeepgramLanguage='{settings.DeepgramLanguage}', DeepgramStreamingEnabled={settings.DeepgramStreamingEnabled}, MistralRealtimeEnabled={settings.MistralRealtimeEnabled}, MistralRealtimeMode={settings.MistralRealtimeMode}.");
            var storedSettings = new StoredSettings
            {
                SelectedInputDeviceId = settings.SelectedInputDeviceId,
                Provider = settings.Provider,
                EncryptedGroqApiKey = Encrypt(settings.GroqApiKey),
                EncryptedFireworksApiKey = Encrypt(settings.FireworksApiKey),
                EncryptedDeepgramApiKey = Encrypt(settings.DeepgramApiKey),
                EncryptedMistralApiKey = Encrypt(settings.MistralApiKey),
                Hotkey = settings.Hotkey,
                PasteLastTranscriptHotkey = settings.PasteLastTranscriptHotkey,
                OpenHistoryHotkey = settings.OpenHistoryHotkey,
                TranscriptHistoryLimit = settings.TranscriptHistoryLimit,
                PushToTalk = settings.PushToTalk,
                LaunchAtStartup = settings.LaunchAtStartup,
                SoundFeedbackEnabled = settings.SoundFeedbackEnabled,
                GroqModel = settings.GroqModel,
                GroqLanguage = NormalizeLanguage(settings.GroqLanguage),
                FireworksModel = settings.FireworksModel,
                FireworksLanguage = NormalizeLanguage(settings.FireworksLanguage),
                DeepgramModel = NormalizeDeepgramModel(settings.DeepgramModel, settings.DeepgramStreamingEnabled),
                DeepgramLanguage = NormalizeLanguage(settings.DeepgramLanguage),
                DeepgramStreamingEnabled = settings.DeepgramStreamingEnabled,
                MistralRealtimeEnabled = settings.MistralRealtimeEnabled,
                MistralRealtimeMode = NormalizeMistralRealtimeMode(settings.MistralRealtimeMode),
                HasCompletedInitialSetup = settings.HasCompletedInitialSetup,
            };

            var json = JsonSerializer.Serialize(storedSettings, _serializerOptions);
            await File.WriteAllTextAsync(_settingsPath, json, cancellationToken);
            _currentSettings = new AppSettings
            {
                SelectedInputDeviceId = settings.SelectedInputDeviceId,
                Provider = settings.Provider,
                GroqApiKey = settings.GroqApiKey,
                FireworksApiKey = settings.FireworksApiKey,
                DeepgramApiKey = settings.DeepgramApiKey,
                MistralApiKey = settings.MistralApiKey,
                Hotkey = settings.Hotkey,
                PasteLastTranscriptHotkey = settings.PasteLastTranscriptHotkey,
                OpenHistoryHotkey = settings.OpenHistoryHotkey,
                TranscriptHistoryLimit = settings.TranscriptHistoryLimit,
                PushToTalk = settings.PushToTalk,
                LaunchAtStartup = settings.LaunchAtStartup,
                SoundFeedbackEnabled = settings.SoundFeedbackEnabled,
                GroqModel = settings.GroqModel,
                GroqLanguage = NormalizeLanguage(settings.GroqLanguage),
                FireworksModel = settings.FireworksModel,
                FireworksLanguage = NormalizeLanguage(settings.FireworksLanguage),
                DeepgramModel = NormalizeDeepgramModel(settings.DeepgramModel, settings.DeepgramStreamingEnabled),
                DeepgramLanguage = NormalizeLanguage(settings.DeepgramLanguage),
                DeepgramStreamingEnabled = settings.DeepgramStreamingEnabled,
                MistralRealtimeEnabled = settings.MistralRealtimeEnabled,
                MistralRealtimeMode = NormalizeMistralRealtimeMode(settings.MistralRealtimeMode),
                HasCompletedInitialSetup = settings.HasCompletedInitialSetup,
            };
            _hasLoadedSettings = true;
            DiagnosticsLogger.Info($"Settings saved to '{_settingsPath}'.");
        }
        finally
        {
            _stateLock.Release();
        }
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

    private static bool InferDeepgramStreamingEnabled(string? model)
    {
        var normalized = model?.Trim().ToLowerInvariant();

        return normalized switch
        {
            "nova-3" or "nova-3-general" => false,
            _ => true,
        };
    }

    private static string NormalizeDeepgramModel(string? model, bool streamingEnabled)
    {
        return streamingEnabled ? "flux" : "nova-3";
    }

    private static MistralRealtimeMode NormalizeMistralRealtimeMode(MistralRealtimeMode? mode)
    {
        return mode == MistralRealtimeMode.Slow ? MistralRealtimeMode.Slow : MistralRealtimeMode.Fast;
    }

    private static string? Encrypt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var plainBytes = Encoding.UTF8.GetBytes(value.Trim());
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string? Decrypt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var protectedBytes = Convert.FromBase64String(value);
        try
        {
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            var plainBytes = ProtectedData.Unprotect(protectedBytes, LegacyEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
    }

    private static void MigrateLegacySettingsFile(string settingsDirectory)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacySettingsPath = Path.Combine(localAppData, "WhisperWindows", "settings.json");
        var newSettingsPath = Path.Combine(settingsDirectory, "settings.json");

        if (!File.Exists(legacySettingsPath) || File.Exists(newSettingsPath))
        {
            return;
        }

        File.Copy(legacySettingsPath, newSettingsPath);
    }

    private sealed class StoredSettings
    {
        public string? SelectedInputDeviceId { get; set; }

        public string? EncryptedGroqApiKey { get; set; }

        public string? EncryptedFireworksApiKey { get; set; }

        public string? EncryptedDeepgramApiKey { get; set; }

        public string? EncryptedMistralApiKey { get; set; }

        public TranscriptionProvider? Provider { get; set; }

        public HotkeyBinding? Hotkey { get; set; }

        public HotkeyBinding? PasteLastTranscriptHotkey { get; set; }

        public HotkeyBinding? OpenHistoryHotkey { get; set; }

        public int? TranscriptHistoryLimit { get; set; }

        public bool? PushToTalk { get; set; }

        public bool? LaunchAtStartup { get; set; }

        public bool? SoundFeedbackEnabled { get; set; }

        public string? GroqModel { get; set; }

        public string? GroqLanguage { get; set; }

        public string? FireworksModel { get; set; }

        public string? FireworksLanguage { get; set; }

        public string? DeepgramModel { get; set; }

        public string? DeepgramLanguage { get; set; }

        public bool? DeepgramStreamingEnabled { get; set; }

        public bool? MistralRealtimeEnabled { get; set; }

        public MistralRealtimeMode? MistralRealtimeMode { get; set; }

        public bool? HasCompletedInitialSetup { get; set; }
    }
}
