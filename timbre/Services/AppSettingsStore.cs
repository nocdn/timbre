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
        : this(DiagnosticsLogger.GetAppDataDirectory(), migrateLegacySettings: true)
    {
    }

    internal AppSettingsStore(string settingsDirectory)
        : this(settingsDirectory, migrateLegacySettings: false)
    {
    }

    private AppSettingsStore(string settingsDirectory, bool migrateLegacySettings)
    {
        if (migrateLegacySettings)
        {
            MigrateLegacySettingsFile(settingsDirectory);
        }

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
                _currentSettings = NormalizeSettings(new AppSettings());
                _hasLoadedSettings = true;
                return _currentSettings;
            }

            var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken);
            var storedSettings = JsonSerializer.Deserialize<StoredSettings>(json, _serializerOptions) ?? new StoredSettings();

            _currentSettings = FromStoredSettings(storedSettings);
            LogSettings("Settings loaded", _currentSettings);

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
            var normalizedSettings = NormalizeSettings(settings);
            LogSettings("Saving settings", normalizedSettings);

            var storedSettings = ToStoredSettings(normalizedSettings);
            var json = JsonSerializer.Serialize(storedSettings, _serializerOptions);
            await File.WriteAllTextAsync(_settingsPath, json, cancellationToken);

            _currentSettings = normalizedSettings;
            _hasLoadedSettings = true;
            DiagnosticsLogger.Info($"Settings saved to '{_settingsPath}'.");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    internal static AppSettings NormalizeSettings(AppSettings settings)
    {
        var groq = settings.GetTranscriptionProviderSettings(TranscriptionProvider.Groq);
        var fireworks = settings.GetTranscriptionProviderSettings(TranscriptionProvider.Fireworks);
        var deepgram = settings.GetTranscriptionProviderSettings(TranscriptionProvider.Deepgram);
        var mistral = settings.GetTranscriptionProviderSettings(TranscriptionProvider.Mistral);
        var cohere = settings.GetTranscriptionProviderSettings(TranscriptionProvider.Cohere);
        var elevenLabs = settings.GetTranscriptionProviderSettings(TranscriptionProvider.ElevenLabs);

        return new AppSettings
        {
            SelectedInputDeviceId = NormalizeOptionalText(settings.SelectedInputDeviceId),
            Provider = NormalizeTranscriptionProvider(settings.Provider),
            LlmPostProcessingEnabled = settings.LlmPostProcessingEnabled,
            LlmPostProcessingProvider = NormalizeLlmPostProcessingProvider(settings.LlmPostProcessingProvider),
            GroqApiKey = NormalizeOptionalSecret(groq.ApiKey),
            CerebrasApiKey = NormalizeOptionalSecret(settings.CerebrasApiKey),
            LlmGroqApiKey = NormalizeOptionalSecret(settings.LlmGroqApiKey),
            FireworksApiKey = NormalizeOptionalSecret(fireworks.ApiKey),
            DeepgramApiKey = NormalizeOptionalSecret(deepgram.ApiKey),
            MistralApiKey = NormalizeOptionalSecret(mistral.ApiKey),
            CohereApiKey = NormalizeOptionalSecret(cohere.ApiKey),
            ElevenLabsApiKey = NormalizeOptionalSecret(elevenLabs.ApiKey),
            Hotkey = settings.Hotkey ?? HotkeyBinding.Default,
            PasteLastTranscriptHotkey = settings.PasteLastTranscriptHotkey ?? HotkeyBinding.PasteLastTranscriptDefault,
            OpenHistoryHotkey = settings.OpenHistoryHotkey ?? HotkeyBinding.OpenHistoryDefault,
            TranscriptHistoryLimit = settings.TranscriptHistoryLimit < 0 ? 200 : settings.TranscriptHistoryLimit,
            PushToTalk = settings.PushToTalk,
            LaunchAtStartup = settings.LaunchAtStartup,
            SoundFeedbackEnabled = settings.SoundFeedbackEnabled,
            LlmPostProcessingPrompt = NormalizeLlmPostProcessingPrompt(settings.LlmPostProcessingPrompt),
            FetchedCerebrasModels = NormalizeFetchedModelList(settings.FetchedCerebrasModels),
            FetchedLlmGroqModels = NormalizeFetchedModelList(settings.FetchedLlmGroqModels),
            CerebrasModel = NormalizeModelName(settings.CerebrasModel, LlmPostProcessingCatalog.DefaultCerebrasModel),
            LlmGroqModel = NormalizeModelName(settings.LlmGroqModel, LlmPostProcessingCatalog.DefaultGroqModel),
            GroqModel = groq.Model,
            GroqLanguage = groq.Language,
            FireworksModel = fireworks.Model,
            FireworksLanguage = fireworks.Language,
            DeepgramModel = deepgram.Model,
            DeepgramLanguage = deepgram.Language,
            DeepgramStreamingEnabled = deepgram.StreamingEnabled,
            DeepgramVadSilenceThresholdSeconds = deepgram.VadSilenceThresholdSeconds,
            MistralModel = mistral.Model,
            MistralStreamingEnabled = mistral.StreamingEnabled,
            MistralRealtimeMode = NormalizeMistralRealtimeMode(settings.MistralRealtimeMode),
            CohereModel = cohere.Model,
            CohereLanguage = cohere.Language,
            ElevenLabsModel = elevenLabs.Model,
            ElevenLabsStreamingEnabled = elevenLabs.StreamingEnabled,
            ElevenLabsLanguage = elevenLabs.Language,
            ElevenLabsVadSilenceThresholdSeconds = elevenLabs.VadSilenceThresholdSeconds,
            HasCompletedInitialSetup = settings.HasCompletedInitialSetup,
        };
    }

    private static AppSettings FromStoredSettings(StoredSettings storedSettings)
    {
        var deepgramStreamingEnabled = storedSettings.DeepgramStreamingEnabled
            ?? TranscriptionProviderCatalog.InferStreamingEnabled(TranscriptionProvider.Deepgram, storedSettings.DeepgramModel);
        var mistralStreamingEnabled = storedSettings.MistralStreamingEnabled
            ?? storedSettings.MistralRealtimeEnabled
            ?? TranscriptionProviderCatalog.InferStreamingEnabled(TranscriptionProvider.Mistral, storedSettings.MistralModel);
        var elevenLabsStreamingEnabled = storedSettings.ElevenLabsStreamingEnabled
            ?? TranscriptionProviderCatalog.InferStreamingEnabled(TranscriptionProvider.ElevenLabs, storedSettings.ElevenLabsModel);

        return NormalizeSettings(new AppSettings
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
            TranscriptHistoryLimit = storedSettings.TranscriptHistoryLimit ?? 200,
            PushToTalk = storedSettings.PushToTalk ?? true,
            LaunchAtStartup = storedSettings.LaunchAtStartup ?? false,
            SoundFeedbackEnabled = storedSettings.SoundFeedbackEnabled ?? true,
            LlmPostProcessingPrompt = storedSettings.LlmPostProcessingPrompt ?? string.Empty,
            FetchedCerebrasModels = storedSettings.FetchedCerebrasModels,
            FetchedLlmGroqModels = storedSettings.FetchedLlmGroqModels,
            CerebrasModel = storedSettings.CerebrasModel ?? string.Empty,
            LlmGroqModel = storedSettings.LlmGroqModel ?? string.Empty,
            GroqModel = storedSettings.GroqModel ?? string.Empty,
            GroqLanguage = storedSettings.GroqLanguage ?? string.Empty,
            FireworksModel = storedSettings.FireworksModel ?? string.Empty,
            FireworksLanguage = storedSettings.FireworksLanguage ?? string.Empty,
            DeepgramModel = storedSettings.DeepgramModel ?? string.Empty,
            DeepgramLanguage = storedSettings.DeepgramLanguage ?? string.Empty,
            DeepgramStreamingEnabled = deepgramStreamingEnabled,
            DeepgramVadSilenceThresholdSeconds = storedSettings.DeepgramVadSilenceThresholdSeconds
                ?? TranscriptionProviderCatalog.DefaultDeepgramVadSilenceThresholdSeconds,
            MistralModel = storedSettings.MistralModel ?? string.Empty,
            MistralStreamingEnabled = mistralStreamingEnabled,
            MistralRealtimeMode = NormalizeMistralRealtimeMode(storedSettings.MistralRealtimeMode),
            CohereModel = storedSettings.CohereModel ?? string.Empty,
            CohereLanguage = storedSettings.CohereLanguage ?? string.Empty,
            ElevenLabsModel = storedSettings.ElevenLabsModel ?? string.Empty,
            ElevenLabsStreamingEnabled = elevenLabsStreamingEnabled,
            ElevenLabsLanguage = storedSettings.ElevenLabsLanguage ?? string.Empty,
            ElevenLabsVadSilenceThresholdSeconds = storedSettings.ElevenLabsVadSilenceThresholdSeconds
                ?? TranscriptionProviderCatalog.DefaultElevenLabsVadSilenceThresholdSeconds,
            HasCompletedInitialSetup = storedSettings.HasCompletedInitialSetup ?? false,
        });
    }

    private static StoredSettings ToStoredSettings(AppSettings settings)
    {
        return new StoredSettings
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
            LlmPostProcessingPrompt = settings.LlmPostProcessingPrompt,
            FetchedCerebrasModels = settings.FetchedCerebrasModels?.ToList(),
            FetchedLlmGroqModels = settings.FetchedLlmGroqModels?.ToList(),
            CerebrasModel = settings.CerebrasModel,
            LlmGroqModel = settings.LlmGroqModel,
            GroqModel = settings.GroqModel,
            GroqLanguage = settings.GroqLanguage,
            FireworksModel = settings.FireworksModel,
            FireworksLanguage = settings.FireworksLanguage,
            DeepgramModel = settings.DeepgramModel,
            DeepgramLanguage = settings.DeepgramLanguage,
            DeepgramStreamingEnabled = settings.DeepgramStreamingEnabled,
            DeepgramVadSilenceThresholdSeconds = settings.DeepgramVadSilenceThresholdSeconds,
            MistralModel = settings.MistralModel,
            MistralStreamingEnabled = settings.MistralStreamingEnabled,
            MistralRealtimeEnabled = settings.MistralStreamingEnabled,
            MistralRealtimeMode = settings.MistralRealtimeMode,
            CohereModel = settings.CohereModel,
            CohereLanguage = settings.CohereLanguage,
            ElevenLabsModel = settings.ElevenLabsModel,
            ElevenLabsStreamingEnabled = settings.ElevenLabsStreamingEnabled,
            ElevenLabsLanguage = settings.ElevenLabsLanguage,
            ElevenLabsVadSilenceThresholdSeconds = settings.ElevenLabsVadSilenceThresholdSeconds,
            HasCompletedInitialSetup = settings.HasCompletedInitialSetup,
        };
    }

    private static void LogSettings(string action, AppSettings settings)
    {
        var providerSummaries = settings.GetAllTranscriptionProviderSettings()
            .Select(providerSettings =>
                $"{providerSettings.Provider}:HasApiKey={!string.IsNullOrWhiteSpace(providerSettings.ApiKey)},Model='{providerSettings.Model}',Language='{providerSettings.Language}',StreamingEnabled={providerSettings.StreamingEnabled},VadSilenceThresholdSeconds={providerSettings.VadSilenceThresholdSeconds}");

        DiagnosticsLogger.Info(
            $"{action}. SelectedInputDeviceId='{settings.SelectedInputDeviceId}', Provider='{settings.Provider}', LlmPostProcessingEnabled={settings.LlmPostProcessingEnabled}, LlmPostProcessingProvider='{settings.LlmPostProcessingProvider}', Hotkey='{settings.Hotkey.ToDisplayString()}', PasteLastTranscriptHotkey='{settings.PasteLastTranscriptHotkey.ToDisplayString()}', OpenHistoryHotkey='{settings.OpenHistoryHotkey.ToDisplayString()}', TranscriptHistoryLimit={settings.TranscriptHistoryLimit}, PushToTalk={settings.PushToTalk}, LaunchAtStartup={settings.LaunchAtStartup}, SoundFeedbackEnabled={settings.SoundFeedbackEnabled}, LlmPromptLength={settings.LlmPostProcessingPrompt.Length}, HasCerebrasApiKey={!string.IsNullOrWhiteSpace(settings.CerebrasApiKey)}, HasLlmGroqApiKey={!string.IsNullOrWhiteSpace(settings.LlmGroqApiKey)}, FetchedCerebrasModelCount={settings.FetchedCerebrasModels?.Count ?? 0}, FetchedLlmGroqModelCount={settings.FetchedLlmGroqModels?.Count ?? 0}, CerebrasModel='{settings.CerebrasModel}', LlmGroqModel='{settings.LlmGroqModel}', TranscriptionProviders=[{string.Join("; ", providerSummaries)}], HasCompletedInitialSetup={settings.HasCompletedInitialSetup}.");
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

    private static string NormalizeModelName(string? value, string defaultValue)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static string? NormalizeOptionalSecret(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static TranscriptionProvider NormalizeTranscriptionProvider(TranscriptionProvider provider)
    {
        return Enum.IsDefined(provider) ? provider : TranscriptionProvider.Groq;
    }

    private static LlmPostProcessingProvider NormalizeLlmPostProcessingProvider(LlmPostProcessingProvider provider)
    {
        return Enum.IsDefined(provider) ? provider : LlmPostProcessingCatalog.DefaultProvider;
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

        public double? DeepgramVadSilenceThresholdSeconds { get; set; }

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

        public double? ElevenLabsVadSilenceThresholdSeconds { get; set; }

        public bool? HasCompletedInitialSetup { get; set; }
    }
}
