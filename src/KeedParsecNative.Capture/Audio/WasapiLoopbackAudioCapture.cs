using System.Collections.Concurrent;
using KeedParsecNative.Contracts.Streaming;
using KeedParsecNative.Core.Abstractions;
using NAudio.Wave;

namespace KeedParsecNative.Capture.Audio;

/// <summary>
/// WASAPI loopback capture of system audio via NAudio. Converts the device mix
/// format to mono float, resamples to 8kHz and emits 16-bit PCM mono
/// <see cref="AudioFrame"/> chunks of ~20ms — the payload the PCMU RTP track
/// expects before µ-law encoding.
/// </summary>
public sealed class WasapiLoopbackAudioCapture : IAudioCapture
{
    public const int TargetSampleRate = 8000;
    public const int FrameSamples = 160; // 20ms @ 8kHz

    private WasapiLoopbackCapture? _capture;
    private readonly BlockingCollection<float[]> _chunks = new();
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private bool _disposed;

    public event Func<AudioFrame, CancellationToken, Task>? FrameCaptured;

    public Task StartAsync(AudioStreamConfig config, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();

        _workerCts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerLoopAsync(_workerCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
        _workerCts?.Cancel();
        _chunks.CompleteAdding();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _capture?.WaveFormat;
        if (format is null || e.BytesRecorded == 0)
        {
            return;
        }
        var channels = Math.Max(1, format.Channels);
        float[] mono;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var interleaved = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, interleaved, 0, interleaved.Length * 4);
            mono = MixToMono(interleaved, channels);
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var interleaved = new short[e.BytesRecorded / 2];
            Buffer.BlockCopy(e.Buffer, 0, interleaved, 0, interleaved.Length * 2);
            mono = new float[interleaved.Length / channels];
            var o = 0;
            for (var i = 0; i + channels <= interleaved.Length; i += channels, o++)
            {
                int sum = 0;
                for (var c = 0; c < channels; c++)
                {
                    sum += interleaved[i + c];
                }
                mono[o] = sum / (channels * 32768f);
            }
        }
        else
        {
            return;
        }

        if (mono.Length > 0)
        {
            _chunks.Add(mono);
        }
    }

    private static float[] MixToMono(float[] interleaved, int channels)
    {
        var mono = new float[interleaved.Length / channels];
        var o = 0;
        for (var i = 0; i + channels <= interleaved.Length; i += channels, o++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                sum += interleaved[i + c];
            }
            mono[o] = sum / channels;
        }
        return mono;
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        // WASAPI mix format is virtually always 48kHz; read the real rate lazily.
        var sourceRate = _capture?.WaveFormat is { } f && f.SampleRate > 0 ? f.SampleRate : 48000;
        var ratio = (double)sourceRate / TargetSampleRate;

        var pending = new List<float>(8192);
        var outPcm = new List<short>(FrameSamples);
        var consumed = 0; // index into pending of next source sample to consume

        try
        {
            foreach (var chunk in _chunks.GetConsumingEnumerable(ct))
            {
                pending.AddRange(chunk);

                var available = pending.Count - consumed;
                var targetCount = (int)(available / ratio);
                if (targetCount <= 0)
                {
                    continue;
                }

                for (var t = 0; t < targetCount; t++)
                {
                    var srcPos = consumed + t * ratio;
                    var idx = (int)srcPos;
                    var frac = (float)(srcPos - idx);
                    var next = Math.Min(idx + 1, pending.Count - 1);
                    var sample = pending[idx] * (1 - frac) + pending[next] * frac;
                    sample = Math.Clamp(sample, -1f, 1f);
                    outPcm.Add((short)(sample * 32767f));
                    if (outPcm.Count >= FrameSamples)
                    {
                        await EmitFrameAsync(outPcm.Take(FrameSamples).ToArray(), ct);
                        outPcm.RemoveRange(0, FrameSamples);
                    }
                }
                consumed += (int)(targetCount * ratio);
                if (consumed > pending.Count / 2)
                {
                    pending.RemoveRange(0, consumed);
                    consumed = 0;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task EmitFrameAsync(short[] samples, CancellationToken ct)
    {
        if (FrameCaptured is null)
        {
            return;
        }
        var pcmBytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, pcmBytes, 0, pcmBytes.Length);
        var frame = new AudioFrame
        {
            PcmData = pcmBytes,
            SampleRate = TargetSampleRate,
            Channels = 1,
            TimestampUtcTicks = DateTime.UtcNow.Ticks
        };
        await FrameCaptured.Invoke(frame, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await StopAsync();
        _chunks.Dispose();
        _workerCts?.Dispose();
    }
}
