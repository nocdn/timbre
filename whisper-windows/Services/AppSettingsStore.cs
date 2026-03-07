using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using whisper_windows.Models;

namespace whisper_windows.Services;

public sealed class AppSettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("whisper-windows-settings");
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public AppSettingsStore()
    {
        var settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperWindows");

        Directory.CreateDirectory(settingsDirectory);
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
    }

    public Task<AppSettings> LoadAsync()
    {
        DiagnosticsLogger.Info($"Loading settings from '{_settingsPath}'.");
        if (!File.Exists(_settingsPath))
        {
            DiagnosticsLogger.Info("Settings file does not exist yet.");
            return Task.FromResult(new AppSettings());
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var storedSettings = JsonSerializer.Deserialize<StoredSettings>(json, _serializerOptions) ?? new StoredSettings();

            var settings = new AppSettings
            {
                SelectedInputDeviceId = storedSettings.SelectedInputDeviceId,
                GroqApiKey = Decrypt(storedSettings.EncryptedGroqApiKey),
                Hotkey = storedSettings.Hotkey ?? HotkeyBinding.Default,
                PasteLastTranscriptHotkey = storedSettings.PasteLastTranscriptHotkey ?? HotkeyBinding.PasteLastTranscriptDefault,
                TranscriptHistoryLimit = storedSettings.TranscriptHistoryLimit is null or < 0 ? 20 : storedSettings.TranscriptHistoryLimit.Value,
                PushToTalk = storedSettings.PushToTalk ?? true,
                GroqModel = string.IsNullOrWhiteSpace(storedSettings.GroqModel)
                    ? "whisper-large-v3-turbo"
                    : storedSettings.GroqModel,
            };

            DiagnosticsLogger.Info(
                $"Settings loaded. SelectedInputDeviceId='{settings.SelectedInputDeviceId}', HasGroqApiKey={!string.IsNullOrWhiteSpace(settings.GroqApiKey)}, Hotkey='{settings.Hotkey.ToDisplayString()}', PasteLastTranscriptHotkey='{settings.PasteLastTranscriptHotkey.ToDisplayString()}', TranscriptHistoryLimit={settings.TranscriptHistoryLimit}, PushToTalk={settings.PushToTalk}, GroqModel='{settings.GroqModel}'.");

            return Task.FromResult(settings);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The saved settings file is invalid. Open Settings and save it again.", exception);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("The saved Groq API key could not be read. Open Settings and save it again.", exception);
        }
    }

    public Task SaveAsync(AppSettings settings)
    {
        DiagnosticsLogger.Info(
            $"Saving settings. SelectedInputDeviceId='{settings.SelectedInputDeviceId}', HasGroqApiKey={!string.IsNullOrWhiteSpace(settings.GroqApiKey)}, Hotkey='{settings.Hotkey.ToDisplayString()}', PasteLastTranscriptHotkey='{settings.PasteLastTranscriptHotkey.ToDisplayString()}', TranscriptHistoryLimit={settings.TranscriptHistoryLimit}, PushToTalk={settings.PushToTalk}, GroqModel='{settings.GroqModel}'.");
        var storedSettings = new StoredSettings
        {
            SelectedInputDeviceId = settings.SelectedInputDeviceId,
            EncryptedGroqApiKey = Encrypt(settings.GroqApiKey),
            Hotkey = settings.Hotkey,
            PasteLastTranscriptHotkey = settings.PasteLastTranscriptHotkey,
            TranscriptHistoryLimit = settings.TranscriptHistoryLimit,
            PushToTalk = settings.PushToTalk,
            GroqModel = settings.GroqModel,
        };

        var json = JsonSerializer.Serialize(storedSettings, _serializerOptions);
        File.WriteAllText(_settingsPath, json);
        DiagnosticsLogger.Info($"Settings saved to '{_settingsPath}'.");

        return Task.CompletedTask;
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
        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private sealed class StoredSettings
    {
        public string? SelectedInputDeviceId { get; set; }

        public string? EncryptedGroqApiKey { get; set; }

        public HotkeyBinding? Hotkey { get; set; }

        public HotkeyBinding? PasteLastTranscriptHotkey { get; set; }

        public int? TranscriptHistoryLimit { get; set; }

        public bool? PushToTalk { get; set; }

        public string? GroqModel { get; set; }
    }
}
