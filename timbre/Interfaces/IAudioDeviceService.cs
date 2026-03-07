using NAudio.CoreAudioApi;
using timbre.Models;

namespace timbre.Interfaces;

public interface IAudioDeviceService
{
    IReadOnlyList<AudioInputDevice> GetInputDevices();

    MMDevice OpenPreferredCaptureDevice(string? selectedDeviceId);
}
