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
            var deepgramStreamingEnabled = storedSettings.DeepgramStreamingEnabled ?? TranscriptionModelCatalog.InferDeepgramStreamingEnabled(storedSettings.DeepgramModel);
            var deepgramModel = TranscriptionModelCatalog.NormalizeDeepgramModel(storedSettings.DeepgramModel, deepgramStreamingEnabled);
            var mistralStreamingEnabled = storedSettings.MistralStreamingEnabled
                ?? storedSettings.MistralRealtimeEnabled
                ?? TranscriptionModelCatalog.InferMistralStreamingEnabled(storedSettings.MistralModel);
            var mistralModel = TranscriptionModelCatalog.NormalizeMistralModel(storedSettings.MistralModel, mistralStreamingEnabled);
            var elevenLabsStreamingEnabled = storedSettings.ElevenLabsStreamingEnabled ?? TranscriptionModelCatalog.InferElevenLabsStreamingEnabled(storedSettings.ElevenLabsModel);
            var elevenLabsModel = TranscriptionModelCatalog.NormalizeElevenLabsModel(storedSettings.ElevenLabsModel, elevenLabsStreamingEnabled);

            _currentSettings = new AppSettings
            {
                SelectedInputDeviceId = storedSettings.SelectedInputDeviceId,
                Provider = storedSettings.Provider ?? TranscriptionProvider.Groq,
                LlmPostProcessingEnabled = storedSettings.LlmPostProcessingEnabled ?? false,
                LlmPostProcessingProvider = storedSettings.LlmPostProcessingProvider ?? LlmPostProcessingCatalog.DefaultProvider,
                GroqApiKey = Decrypt(storedSettings.EncryptedGroqApiKey),
                CerebrasApiKey = Decrypt(storedSettings.EncryptedCerebrasApiKey),
                LlmGroqApiKey = Decrypt(storedSettings.EncryptedLlmGroqApiKey),
                FireworksApiKey = Decrypt(storedSettings.EncryptedFireworksApiKey),
                DeepgramApiKey = Decrypt(storedSettings.EncryptedDeepgramApiKey),
                MistralApiKey = Decrypt(storedSettings.EncryptedMistralApiKey),
                CohereApiKey = Decrypt(storedSettings.EncryptedCohereApiKey),
                ElevenLabsApiKey = Decrypt(storedSettings.EncryptedElevenLabsApiKey),
                Hotkey = storedSettings.Hotkey ?? HotkeyBinding.Default,
                PasteLastTranscriptHotkey = storedSettings.PasteLastTranscriptHotkey ?? HotkeyBinding.PasteLastTranscriptDefault,
                OpenHistoryHotkey = storedSettings.OpenHistoryHotkey ?? HotkeyBinding.OpenHistoryDefault,
                TranscriptHistoryLimit = storedSettings.TranscriptHistoryLimit is null or < 0 ? 200 : storedSettings.TranscriptHistoryLimit.Value,
                PushToTalk = storedSettings.PushToTalk ?? true,
                LaunchAtStartup = storedSettings.LaunchAtStartup ?? false,
                SoundFeedbackEnabled = storedSettings.SoundFeedbackEnabled ?? true,
                LlmPostProcessingPrompt = NormalizeLlmPostProcessingPrompt(storedSettings.LlmPostProcessingPrompt),
                FetchedCerebrasModels = NormalizeFetchedModelList(storedSettings.FetchedCerebrasModels),
                FetchedLlmGroqModels = NormalizeFetchedModelList(storedSettings.FetchedLlmGroqModels),
                CerebrasModel = string.IsNullOrWhiteSpace(storedSettings.CerebrasModel)
                    ? LlmPostProcessingCatalog.DefaultCerebrasModel
                    : storedSettings.CerebrasModel,
                LlmGroqModel = string.IsNullOrWhiteSpace(storedSettings.LlmGroqModel)
                    ? LlmPostProcessingCatalog.DefaultGroqModel
                    : storedSettings.LlmGroqModel,
                GroqModel = string.IsNullOrWhiteSpace(storedSettings.GroqModel)
                    ? TranscriptionModelCatalog.DefaultGroqModel
                    : storedSettings.GroqModel,
                GroqLanguage = NormalizeAutoDetectLanguage(storedSettings.GroqLanguage),
                FireworksModel = string.IsNullOrWhiteSpace(storedSettings.FireworksModel)
                    ? TranscriptionModelCatalog.DefaultFireworksModel
                    : storedSettings.FireworksModel,
                FireworksLanguage = NormalizeAutoDetectLanguage(storedSettings.FireworksLanguage),
                DeepgramModel = deepgramModel,
                DeepgramLanguage = NormalizeExplicitLanguage(storedSettings.DeepgramLanguage),
                DeepgramStreamingEnabled = deepgramStreamingEnabled,
                MistralModel = mistralModel,
                MistralStreamingEnabled = mistralStreamingEnabled,
                MistralRealtimeMode = NormalizeMistralRealtimeMode(storedSettings.MistralRealtimeMode),
                CohereModel = string.IsNullOrWhiteSpace(storedSettings.CohereModel)
                    ? TranscriptionModelCatalog.DefaultCohereModel
                    : storedSettings.CohereModel,
                CohereLanguage = NormalizeExplicitLanguage(storedSettings.CohereLanguage),
                ElevenLabsModel = elevenLabsModel,
                ElevenLabsStreamingEnabled = elevenLabsStreamingEnabled,
                ElevenLabsLanguage = NormalizeAutoDetectLanguage(storedSettings.ElevenLabsLanguage),
                HasCompletedInitialSetup = storedSettings.HasCompletedInitialSetup ?? false,
            };

            DiagnosticsLogger.Info(
                $"Settings loaded. SelectedInputDeviceId='{_currentSettings.SelectedInputDeviceId}', Provider='{_currentSettings.Provider}', LlmPostProcessingEnabled={_currentSettings.LlmPostProcessingEnabled}, LlmPostProcessingProvider='{_currentSettings.LlmPostProcessingProvider}', HasGroqApiKey={!string.IsNullOrWhiteSpace(_currentSettings.GroqApiKey)}, HasCerebrasApiKey={!string.IsNullOrWhiteSpace(_currentSettings.CerebrasApiKey)}, HasLlmGroqApiKey={!string.IsNullOrWhiteSpace(_currentSettings.LlmGroqApiKey)}, HasFireworksApiKey={!string.IsNullOrWhiteSpace(_currentSettings.FireworksApiKey)}, HasDeepgramApiKey={!string.IsNullOrWhiteSpace(_currentSettings.DeepgramApiKey)}, HasMistralApiKey={!string.IsNullOrWhiteSpace(_currentSettings.MistralApiKey)}, HasCohereApiKey={!string.IsNullOrWhiteSpace(_currentSettings.CohereApiKey)}, Hotkey='{_currentSettings.Hotkey.ToDisplayString()}', PasteLastTranscriptHotkey='{_currentSettings.PasteLastTranscriptHotkey.ToDisplayString()}', OpenHistoryHotkey='{_currentSettings.OpenHistoryHotkey.ToDisplayString()}', TranscriptHistoryLimit={_currentSettings.TranscriptHistoryLimit}, PushToTalk={_currentSettings.PushToTalk}, LaunchAtStartup={_currentSettings.LaunchAtStartup}, SoundFeedbackEnabled={_currentSettings.SoundFeedbackEnabled}, LlmPromptLength={_currentSettings.LlmPostProcessingPrompt.Length}, FetchedCerebrasModelCount={_currentSettings.FetchedCerebrasModels?.Count ?? 0}, FetchedLlmGroqModelCount={_currentSettings.FetchedLlmGroqModels?.Count ?? 0}, CerebrasModel='{_currentSettings.CerebrasModel}', LlmGroqModel='{_currentSettings.LlmGroqModel}', GroqModel='{_currentSettings.GroqModel}', GroqLanguage='{_currentSettings.GroqLanguage}', FireworksModel='{_currentSettings.FireworksModel}', FireworksLanguage='{_currentSettings.FireworksLanguage}', DeepgramModel='{_currentSettings.DeepgramModel}', DeepgramLanguage='{_currentSettings.DeepgramLanguage}', DeepgramStreamingEnabled={_currentSettings.DeepgramStreamingEnabled}, MistralStreamingEnabled={_currentSettings.MistralStreamingEnabled}, MistralRealtimeMode={_currentSettings.MistralRealtimeMode}, CohereModel='{_currentSettings.CohereModel}', CohereLanguage='{_currentSettings.CohereLanguage}', HasCompletedInitialSetup={_currentSettings.HasCompletedInitialSetup}.");

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
                $"Saving settings. SelectedInputDeviceId='{settings.SelectedInputDeviceId}', Provider='{settings.Provider}', LlmPostProcessingEnabled={settings.LlmPostProcessingEnabled}, LlmPostProcessingProvider='{settings.LlmPostProcessingProvider}', HasGroqApiKey={!string.IsNullOrWhiteSpace(settings.GroqApiKey)}, HasCerebrasApiKey={!string.IsNullOrWhiteSpace(settings.CerebrasApiKey)}, HasLlmGroqApiKey={!string.IsNullOrWhiteSpace(settings.LlmGroqApiKey)}, HasFireworksApiKey={!string.IsNullOrWhiteSpace(settings.FireworksApiKey)}, HasDeepgramApiKey={!string.IsNullOrWhiteSpace(settings.DeepgramApiKey)}, HasMistralApiKey={!string.IsNullOrWhiteSpace(settings.MistralApiKey)}, HasCohereApiKey={!string.IsNullOrWhiteSpace(settings.CohereApiKey)}, Hotkey='{settings.Hotkey.ToDisplayString()}', PasteLastTranscriptHotkey='{settings.PasteLastTranscriptHotkey.ToDisplayString()}', OpenHistoryHotkey='{settings.OpenHistoryHotkey.ToDisplayString()}', TranscriptHistoryLimit={settings.TranscriptHistoryLimit}, PushToTalk={settings.PushToTalk}, LaunchAtStartup={settings.LaunchAtStartup}, SoundFeedbackEnabled={settings.SoundFeedbackEnabled}, LlmPromptLength={settings.LlmPostProcessingPrompt.Length}, FetchedCerebrasModelCount={settings.FetchedCerebrasModels?.Count ?? 0}, FetchedLlmGroqModelCount={settings.FetchedLlmGroqModels?.Count ?? 0}, CerebrasModel='{settings.CerebrasModel}', LlmGroqModel='{settings.LlmGroqModel}', GroqModel='{settings.GroqModel}', GroqLanguage='{settings.GroqLanguage}', FireworksModel='{settings.FireworksModel}', FireworksLanguage='{settings.FireworksLanguage}', DeepgramModel='{settings.DeepgramModel}', DeepgramLanguage='{settings.DeepgramLanguage}', DeepgramStreamingEnabled={settings.DeepgramStreamingEnabled}, MistralStreamingEnabled={settings.MistralStreamingEnabled}, MistralRealtimeMode={settings.MistralRealtimeMode}, CohereModel='{settings.CohereModel}', CohereLanguage='{settings.CohereLanguage}'.");
            var storedSettings = new StoredSettings
            {
                SelectedInputDeviceId = settings.SelectedInputDeviceId,
                Provider = settings.Provider,
                LlmPostProcessingEnabled = settings.LlmPostProcessingEnabled,
                LlmPostProcessingProvider = settings.LlmPostProcessingProvider,
                EncryptedGroqApiKey = Encrypt(settings.GroqApiKey),
                EncryptedCerebrasApiKey = Encrypt(settings.CerebrasApiKey),
                EncryptedLlmGroqApiKey = Encrypt(settings.LlmGroqApiKey),
                EncryptedFireworksApiKey = Encrypt(settings.FireworksApiKey),
                EncryptedDeepgramApiKey = Encrypt(settings.DeepgramApiKey),
                EncryptedMistralApiKey = Encrypt(settings.MistralApiKey),
                EncryptedCohereApiKey = Encrypt(settings.CohereApiKey),
                EncryptedElevenLabsApiKey = Encrypt(settings.ElevenLabsApiKey),
                Hotkey = settings.Hotkey,
                PasteLastTranscriptHotkey = settings.PasteLastTranscriptHotkey,
                OpenHistoryHotkey = settings.OpenHistoryHotkey,
                TranscriptHistoryLimit = settings.TranscriptHistoryLimit,
                PushToTalk = settings.PushToTalk,
                LaunchAtStartup = settings.LaunchAtStartup,
                SoundFeedbackEnabled = settings.SoundFeedbackEnabled,
                LlmPostProcessingPrompt = NormalizeLlmPostProcessingPrompt(settings.LlmPostProcessingPrompt),
                FetchedCerebrasModels = NormalizeFetchedModelList(settings.FetchedCerebrasModels),
                FetchedLlmGroqModels = NormalizeFetchedModelList(settings.FetchedLlmGroqModels),
                CerebrasModel = string.IsNullOrWhiteSpace(settings.CerebrasModel) ? LlmPostProcessingCatalog.DefaultCerebrasModel : settings.CerebrasModel,
                LlmGroqModel = string.IsNullOrWhiteSpace(settings.LlmGroqModel) ? LlmPostProcessingCatalog.DefaultGroqModel : settings.LlmGroqModel,
                GroqModel = settings.GroqModel,
                GroqLanguage = NormalizeAutoDetectLanguage(settings.GroqLanguage),
                FireworksModel = settings.FireworksModel,
                FireworksLanguage = NormalizeAutoDetectLanguage(settings.FireworksLanguage),
                DeepgramModel = TranscriptionModelCatalog.NormalizeDeepgramModel(settings.DeepgramModel, settings.DeepgramStreamingEnabled),
                DeepgramLanguage = NormalizeExplicitLanguage(settings.DeepgramLanguage),
                DeepgramStreamingEnabled = settings.DeepgramStreamingEnabled,
                MistralModel = TranscriptionModelCatalog.NormalizeMistralModel(settings.MistralModel, settings.MistralStreamingEnabled),
                MistralStreamingEnabled = settings.MistralStreamingEnabled,
                MistralRealtimeEnabled = settings.MistralStreamingEnabled,
                MistralRealtimeMode = NormalizeMistralRealtimeMode(settings.MistralRealtimeMode),
                CohereModel = string.IsNullOrWhiteSpace(settings.CohereModel) ? TranscriptionModelCatalog.DefaultCohereModel : settings.CohereModel,
                CohereLanguage = NormalizeExplicitLanguage(settings.CohereLanguage),
                ElevenLabsModel = TranscriptionModelCatalog.NormalizeElevenLabsModel(settings.ElevenLabsModel, settings.ElevenLabsStreamingEnabled),
                ElevenLabsStreamingEnabled = settings.ElevenLabsStreamingEnabled,
                ElevenLabsLanguage = NormalizeAutoDetectLanguage(settings.ElevenLabsLanguage),
                HasCompletedInitialSetup = settings.HasCompletedInitialSetup,
            };

            var json = JsonSerializer.Serialize(storedSettings, _serializerOptions);
            await File.WriteAllTextAsync(_settingsPath, json, cancellationToken);
            _currentSettings = new AppSettings
            {
                SelectedInputDeviceId = settings.SelectedInputDeviceId,
                Provider = settings.Provider,
                LlmPostProcessingEnabled = settings.LlmPostProcessingEnabled,
                LlmPostProcessingProvider = settings.LlmPostProcessingProvider,
                GroqApiKey = settings.GroqApiKey,
                CerebrasApiKey = settings.CerebrasApiKey,
                LlmGroqApiKey = settings.LlmGroqApiKey,
                FireworksApiKey = settings.FireworksApiKey,
                DeepgramApiKey = settings.DeepgramApiKey,
                MistralApiKey = settings.MistralApiKey,
                CohereApiKey = settings.CohereApiKey,
                ElevenLabsApiKey = settings.ElevenLabsApiKey,
                Hotkey = settings.Hotkey,
                PasteLastTranscriptHotkey = settings.PasteLastTranscriptHotkey,
                OpenHistoryHotkey = settings.OpenHistoryHotkey,
                TranscriptHistoryLimit = settings.TranscriptHistoryLimit,
                PushToTalk = settings.PushToTalk,
                LaunchAtStartup = settings.LaunchAtStartup,
                SoundFeedbackEnabled = settings.SoundFeedbackEnabled,
                LlmPostProcessingPrompt = NormalizeLlmPostProcessingPrompt(settings.LlmPostProcessingPrompt),
                FetchedCerebrasModels = NormalizeFetchedModelList(settings.FetchedCerebrasModels),
                FetchedLlmGroqModels = NormalizeFetchedModelList(settings.FetchedLlmGroqModels),
                CerebrasModel = string.IsNullOrWhiteSpace(settings.CerebrasModel) ? LlmPostProcessingCatalog.DefaultCerebrasModel : settings.CerebrasModel,
                LlmGroqModel = string.IsNullOrWhiteSpace(settings.LlmGroqModel) ? LlmPostProcessingCatalog.DefaultGroqModel : settings.LlmGroqModel,
                GroqModel = settings.GroqModel,
                GroqLanguage = NormalizeAutoDetectLanguage(settings.GroqLanguage),
                FireworksModel = settings.FireworksModel,
                FireworksLanguage = NormalizeAutoDetectLanguage(settings.FireworksLanguage),
                DeepgramModel = TranscriptionModelCatalog.NormalizeDeepgramModel(settings.DeepgramModel, settings.DeepgramStreamingEnabled),
                DeepgramLanguage = NormalizeExplicitLanguage(settings.DeepgramLanguage),
                DeepgramStreamingEnabled = settings.DeepgramStreamingEnabled,
                MistralModel = TranscriptionModelCatalog.NormalizeMistralModel(settings.MistralModel, settings.MistralStreamingEnabled),
                MistralStreamingEnabled = settings.MistralStreamingEnabled,
                MistralRealtimeMode = NormalizeMistralRealtimeMode(settings.MistralRealtimeMode),
                CohereModel = string.IsNullOrWhiteSpace(settings.CohereModel) ? TranscriptionModelCatalog.DefaultCohereModel : settings.CohereModel,
                CohereLanguage = NormalizeExplicitLanguage(settings.CohereLanguage),
                ElevenLabsModel = TranscriptionModelCatalog.NormalizeElevenLabsModel(settings.ElevenLabsModel, settings.ElevenLabsStreamingEnabled),
                ElevenLabsStreamingEnabled = settings.ElevenLabsStreamingEnabled,
                ElevenLabsLanguage = NormalizeAutoDetectLanguage(settings.ElevenLabsLanguage),
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

    private static string NormalizeAutoDetectLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "auto";
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized == "auto" ? "auto" : normalized;
    }

    private static string NormalizeExplicitLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeLlmPostProcessingPrompt(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? LlmPostProcessingCatalog.DefaultPrompt
            : value.Trim();
    }

    private static List<string>? NormalizeFetchedModelList(IReadOnlyList<string>? models)
    {
        if (models is null || models.Count == 0)
        {
            return null;
        }

        var normalizedModels = new List<string>();
        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            var normalizedModel = model.Trim();
            if (normalizedModels.Contains(normalizedModel, StringComparer.Ordinal))
            {
                continue;
            }

            normalizedModels.Add(normalizedModel);
        }

        return normalizedModels.Count == 0 ? null : normalizedModels;
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

        public string? EncryptedCerebrasApiKey { get; set; }

        public string? EncryptedLlmGroqApiKey { get; set; }

        public string? EncryptedFireworksApiKey { get; set; }

        public string? EncryptedDeepgramApiKey { get; set; }

        public string? EncryptedMistralApiKey { get; set; }

        public string? EncryptedCohereApiKey { get; set; }

        public string? EncryptedElevenLabsApiKey { get; set; }

        public TranscriptionProvider? Provider { get; set; }

        public bool? LlmPostProcessingEnabled { get; set; }

        public LlmPostProcessingProvider? LlmPostProcessingProvider { get; set; }

        public HotkeyBinding? Hotkey { get; set; }

        public HotkeyBinding? PasteLastTranscriptHotkey { get; set; }

        public HotkeyBinding? OpenHistoryHotkey { get; set; }

        public int? TranscriptHistoryLimit { get; set; }

        public bool? PushToTalk { get; set; }

        public bool? LaunchAtStartup { get; set; }

        public bool? SoundFeedbackEnabled { get; set; }

        public string? LlmPostProcessingPrompt { get; set; }

        public List<string>? FetchedCerebrasModels { get; set; }

        public List<string>? FetchedLlmGroqModels { get; set; }

        public string? CerebrasModel { get; set; }

        public string? LlmGroqModel { get; set; }

        public string? GroqModel { get; set; }

        public string? GroqLanguage { get; set; }

        public string? FireworksModel { get; set; }

        public string? FireworksLanguage { get; set; }

        public string? DeepgramModel { get; set; }

        public string? DeepgramLanguage { get; set; }

        public bool? DeepgramStreamingEnabled { get; set; }

        public string? MistralModel { get; set; }

        public bool? MistralStreamingEnabled { get; set; }

        // Legacy setting name. Keep reading it so existing settings files migrate cleanly.
        public bool? MistralRealtimeEnabled { get; set; }

        public MistralRealtimeMode? MistralRealtimeMode { get; set; }

        public string? CohereModel { get; set; }

        public string? CohereLanguage { get; set; }

        public string? ElevenLabsModel { get; set; }

        public bool? ElevenLabsStreamingEnabled { get; set; }

        public string? ElevenLabsLanguage { get; set; }

        public bool? HasCompletedInitialSetup { get; set; }
    }
}
