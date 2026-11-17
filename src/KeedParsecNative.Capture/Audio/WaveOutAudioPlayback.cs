using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Core.Abstractions;
using NAudio.Wave;

namespace KeedParsecNative.Capture.Audio;

/// <summary>
/// Client-side audio playback: buffers 8kHz mono 16-bit PCM (decoded from the
/// PCMU RTP track) and plays it through the default render device via
/// WaveOutEvent with a BufferedWaveProvider.
/// </summary>
public sealed class WaveOutAudioPlayback : IAudioPlayback
{
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _provider;
    private bool _disposed;

    public Task InitializeAsync(AudioStreamConfig config, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _provider = new BufferedWaveProvider(new WaveFormat(
            WasapiLoopbackAudioCapture.TargetSampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true
        };
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_provider);
        _waveOut.Play();
        return Task.CompletedTask;
    }

    public Task PlayAsync(AudioFrame frame, CancellationToken cancellationToken = default)
    {
        if (_provider is null || frame.PcmData.Length == 0)
        {
            return Task.CompletedTask;
        }
        _provider.AddSamples(frame.PcmData.ToArray(), 0, frame.PcmData.Length);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _provider = null;
        await Task.CompletedTask;
    }
}
