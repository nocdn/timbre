using NAudio.CoreAudioApi;
using whisper_windows.Interfaces;
using whisper_windows.Models;

namespace whisper_windows.Services;

public sealed class AudioDeviceService : IAudioDeviceService
{
    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultDeviceId = TryGetDefaultDeviceId(enumerator);

        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new AudioInputDevice(
                device.ID,
                device.ID == defaultDeviceId ? $"{device.FriendlyName} (Default)" : device.FriendlyName,
                device.ID == defaultDeviceId))
            .ToList();

        DiagnosticsLogger.Info(
            $"Enumerated {devices.Count} input devices. DefaultDeviceId='{defaultDeviceId}'.");

        return devices;
    }

    public MMDevice OpenPreferredCaptureDevice(string? selectedDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        DiagnosticsLogger.Info($"Opening preferred capture device. RequestedDeviceId='{selectedDeviceId}'.");

        if (!string.IsNullOrWhiteSpace(selectedDeviceId))
        {
            try
            {
                var selectedDevice = enumerator.GetDevice(selectedDeviceId);
                DiagnosticsLogger.Info($"Using explicitly selected capture device '{selectedDevice.FriendlyName}'.");
                return selectedDevice;
            }
            catch
            {
                // Fall back to the current default capture device if the saved device is gone.
                DiagnosticsLogger.Info("Requested capture device was unavailable. Falling back to the default device.");
            }
        }

        try
        {
            var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            DiagnosticsLogger.Info($"Using default capture device '{defaultDevice.FriendlyName}'.");
            return defaultDevice;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("No active microphone is available.", exception);
        }
    }

    private static string? TryGetDefaultDeviceId(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return defaultDevice.ID;
        }
        catch
        {
            return null;
        }
    }
}
