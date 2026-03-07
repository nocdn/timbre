using NAudio.CoreAudioApi;
using whisper_windows.Models;

namespace whisper_windows.Interfaces;

public interface IAudioDeviceService
{
    IReadOnlyList<AudioInputDevice> GetInputDevices();

    MMDevice OpenPreferredCaptureDevice(string? selectedDeviceId);
}
