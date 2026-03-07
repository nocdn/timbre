using NAudio.CoreAudioApi;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace timbre.Services;

public sealed class AudioRecorder : IDisposable
{
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private MemoryStream? _recordedStream;
    private WaveFileWriter? _waveWriter;
    private TaskCompletionSource<byte[]>? _stopCompletionSource;

    public bool IsRecording { get; private set; }

    public string? DeviceName { get; private set; }

    public string? WaveFormatDescription { get; private set; }

    public long BytesCaptured { get; private set; }

    public string? LastCompletedDeviceName { get; private set; }

    public string? LastCompletedWaveFormatDescription { get; private set; }

    public long LastCompletedBytesCaptured { get; private set; }

    public void Start(MMDevice device)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Recording is already in progress.");
        }

        _device = device;
        _capture = new WasapiCapture(device);
        DeviceName = device.FriendlyName;
        WaveFormatDescription = _capture.WaveFormat.ToString();
        BytesCaptured = 0;
        LastCompletedDeviceName = null;
        LastCompletedWaveFormatDescription = null;
        LastCompletedBytesCaptured = 0;
        _recordedStream = new MemoryStream();
        _waveWriter = new WaveFileWriter(new IgnoreDisposeStream(_recordedStream), _capture.WaveFormat);
        _stopCompletionSource = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();

        IsRecording = true;
    }

    public Task<byte[]> StopAsync()
    {
        if (!IsRecording || _capture is null || _stopCompletionSource is null)
        {
            throw new InvalidOperationException("No active recording is available.");
        }

        _capture.StopRecording();
        return _stopCompletionSource.Task;
    }

    public void Dispose()
    {
        Cleanup();
        _stopCompletionSource?.TrySetCanceled();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        BytesCaptured += eventArgs.BytesRecorded;
        _waveWriter?.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        _waveWriter?.Flush();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        var completionSource = _stopCompletionSource;

        try
        {
            _waveWriter?.Flush();
            _waveWriter?.Dispose();

            LastCompletedDeviceName = DeviceName;
            LastCompletedWaveFormatDescription = WaveFormatDescription;
            LastCompletedBytesCaptured = BytesCaptured;
            var audioBytes = DownsampleTo16KhzMono(_recordedStream?.ToArray() ?? []);
            Cleanup();

            if (eventArgs.Exception is not null)
            {
                completionSource?.TrySetException(eventArgs.Exception);
                return;
            }

            completionSource?.TrySetResult(audioBytes);
        }
        catch (Exception exception)
        {
            Cleanup();
            completionSource?.TrySetException(exception);
        }
    }

    private void Cleanup()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _waveWriter?.Dispose();
        _waveWriter = null;

        _recordedStream?.Dispose();
        _recordedStream = null;

        _device?.Dispose();
        _device = null;

        DeviceName = null;
        WaveFormatDescription = null;
        BytesCaptured = 0;
        IsRecording = false;
    }

    private static byte[] DownsampleTo16KhzMono(byte[] inputBytes)
    {
        if (inputBytes.Length == 0)
        {
            return inputBytes;
        }

        using var inputStream = new MemoryStream(inputBytes);
        using var reader = new WaveFileReader(inputStream);
        var sampleProvider = reader.ToSampleProvider();
        ISampleProvider resampledProvider = sampleProvider;

        if (sampleProvider.WaveFormat.SampleRate != 16000)
        {
            resampledProvider = new WdlResamplingSampleProvider(resampledProvider, 16000);
        }

        if (resampledProvider.WaveFormat.Channels > 1)
        {
            var monoProvider = new StereoToMonoSampleProvider(resampledProvider);
            monoProvider.LeftVolume = 0.5f;
            monoProvider.RightVolume = 0.5f;
            resampledProvider = monoProvider;
        }

        using var outputStream = new MemoryStream();
        using (var writer = new WaveFileWriter(outputStream, new WaveFormat(16000, 16, 1)))
        {
            var buffer = new float[16000];
            var convertedBuffer = new byte[buffer.Length * 2];

            while (true)
            {
                var samplesRead = resampledProvider.Read(buffer, 0, buffer.Length);
                if (samplesRead == 0)
                {
                    break;
                }

                for (var i = 0; i < samplesRead; i++)
                {
                    var sample = (short)Math.Clamp(buffer[i] * short.MaxValue, short.MinValue, short.MaxValue);
                    convertedBuffer[(i * 2)] = (byte)(sample & 0xFF);
                    convertedBuffer[(i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
                }

                writer.Write(convertedBuffer, 0, samplesRead * 2);
            }

            writer.Flush();
        }

        return outputStream.ToArray();
    }
}
