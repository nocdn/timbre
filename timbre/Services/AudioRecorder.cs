using NAudio.CoreAudioApi;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using timbre.Models;

namespace timbre.Services;

public sealed class AudioRecorder : IDisposable
{
    private const int StreamingChunkSizeBytes = 2560;

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private MemoryStream? _convertedStream;
    private WaveFileWriter? _convertedWriter;
    private BufferedWaveProvider? _bufferedWaveProvider;
    private ISampleProvider? _resampledProvider;
    private MemoryStream? _streamingChunkBuffer;
    private TaskCompletionSource<byte[]>? _stopCompletionSource;

    public event EventHandler<AudioChunkAvailableEventArgs>? ChunkAvailable;

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
        _convertedStream = new MemoryStream();
        _convertedWriter = new WaveFileWriter(new IgnoreDisposeStream(_convertedStream), new WaveFormat(16000, 16, 1));
        _bufferedWaveProvider = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = false,
            ReadFully = false,
        };
        _resampledProvider = CreateResampledProvider(_bufferedWaveProvider);
        _streamingChunkBuffer = new MemoryStream();
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

        if (_bufferedWaveProvider is null || _resampledProvider is null || _convertedWriter is null)
        {
            return;
        }

        _bufferedWaveProvider.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        PublishConvertedAudioChunks(_resampledProvider, _convertedWriter);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        var completionSource = _stopCompletionSource;

        try
        {
            if (_resampledProvider is not null && _convertedWriter is not null)
            {
                PublishConvertedAudioChunks(_resampledProvider, _convertedWriter);
                FlushStreamingChunkBuffer();
                _convertedWriter.Flush();
                _convertedWriter.Dispose();
            }

            LastCompletedDeviceName = DeviceName;
            LastCompletedWaveFormatDescription = WaveFormatDescription;
            LastCompletedBytesCaptured = BytesCaptured;
            var audioBytes = _convertedStream?.ToArray() ?? [];
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

        _convertedWriter?.Dispose();
        _convertedWriter = null;

        _convertedStream?.Dispose();
        _convertedStream = null;

        _bufferedWaveProvider = null;
        _resampledProvider = null;

        _streamingChunkBuffer?.Dispose();
        _streamingChunkBuffer = null;

        _device?.Dispose();
        _device = null;

        DeviceName = null;
        WaveFormatDescription = null;
        BytesCaptured = 0;
        IsRecording = false;
    }

    private void PublishConvertedAudioChunks(ISampleProvider sampleProvider, WaveFileWriter waveWriter)
    {
        var sampleBuffer = new float[4096];
        var convertedBuffer = new byte[sampleBuffer.Length * 2];

        while (true)
        {
            var samplesRead = sampleProvider.Read(sampleBuffer, 0, sampleBuffer.Length);
            if (samplesRead == 0)
            {
                break;
            }

            for (var i = 0; i < samplesRead; i++)
            {
                var sample = (short)Math.Clamp(sampleBuffer[i] * short.MaxValue, short.MinValue, short.MaxValue);
                convertedBuffer[i * 2] = (byte)(sample & 0xFF);
                convertedBuffer[(i * 2) + 1] = (byte)((sample >> 8) & 0xFF);
            }

            var bytesProduced = samplesRead * 2;
            waveWriter.Write(convertedBuffer, 0, bytesProduced);
            waveWriter.Flush();
            AppendStreamingChunkBytes(convertedBuffer, bytesProduced);
        }
    }

    private void AppendStreamingChunkBytes(byte[] bytes, int count)
    {
        if (_streamingChunkBuffer is null || count <= 0)
        {
            return;
        }

        _streamingChunkBuffer.Write(bytes, 0, count);

        while (_streamingChunkBuffer.Length >= StreamingChunkSizeBytes)
        {
            EmitStreamingChunk(StreamingChunkSizeBytes);
        }
    }

    private void FlushStreamingChunkBuffer()
    {
        if (_streamingChunkBuffer is null || _streamingChunkBuffer.Length == 0)
        {
            return;
        }

        EmitStreamingChunk((int)_streamingChunkBuffer.Length);
    }

    private void EmitStreamingChunk(int bytesToEmit)
    {
        if (_streamingChunkBuffer is null || bytesToEmit <= 0)
        {
            return;
        }

        var buffer = _streamingChunkBuffer.GetBuffer();
        var chunkBytes = new byte[bytesToEmit];
        Buffer.BlockCopy(buffer, 0, chunkBytes, 0, bytesToEmit);
        ChunkAvailable?.Invoke(this, new AudioChunkAvailableEventArgs(chunkBytes));

        var remainingBytes = (int)_streamingChunkBuffer.Length - bytesToEmit;
        if (remainingBytes > 0)
        {
            Buffer.BlockCopy(buffer, bytesToEmit, buffer, 0, remainingBytes);
        }

        _streamingChunkBuffer.SetLength(remainingBytes);
        _streamingChunkBuffer.Position = remainingBytes;
    }

    private static ISampleProvider CreateResampledProvider(IWaveProvider waveProvider)
    {
        var sampleProvider = waveProvider.ToSampleProvider();
        ISampleProvider resampledProvider = sampleProvider;

        if (sampleProvider.WaveFormat.SampleRate != 16000)
        {
            resampledProvider = new WdlResamplingSampleProvider(resampledProvider, 16000);
        }

        if (resampledProvider.WaveFormat.Channels > 1)
        {
            var monoProvider = new StereoToMonoSampleProvider(resampledProvider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f,
            };
            resampledProvider = monoProvider;
        }

        return resampledProvider;
    }
}
