using NAudio.Wave;
using timbre.Interfaces;

namespace timbre.Services;

public sealed class AudioFeedbackService : IAudioFeedbackService
{
    private readonly object _syncRoot = new();

    private WaveOutEvent? _outputDevice;
    private AudioFileReader? _audioFile;
    private bool _isDisposed;

    public void WarmUp()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            EnsureInitialized();
        }
    }

    public void PlayRecordingStarted()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            EnsureInitialized();

            if (_outputDevice is null || _audioFile is null)
            {
                return;
            }

            _outputDevice.Stop();
            _audioFile.Position = 0;
            _outputDevice.Play();
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _outputDevice?.Dispose();
            _audioFile?.Dispose();
            _outputDevice = null;
            _audioFile = null;
        }
    }

    private void EnsureInitialized()
    {
        if (_outputDevice is not null && _audioFile is not null)
        {
            return;
        }

        var soundPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "switch-005.mp3");
        if (!File.Exists(soundPath))
        {
            DiagnosticsLogger.Info($"Feedback sound file not found at '{soundPath}'.");
            return;
        }

        _audioFile = new AudioFileReader(soundPath);
        _outputDevice = new WaveOutEvent();
        _outputDevice.Init(_audioFile);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
