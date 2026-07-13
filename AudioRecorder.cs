using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Birdcage;

public enum RecordSource
{
    Microphone,
    SystemAudio,
    Mix
}

public class AudioRecorder : IDisposable
{
    private const long MaxFileSize = 3L * 1024 * 1024 * 1024; // 3GBで安全に分割

    private readonly object _writerLock = new();

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopCapture;
    private IAudioFileWriter? _writer;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _loopBuffer;
    private MixingSampleProvider? _mixer;
    private Task? _mixTask;
    private CancellationTokenSource? _cts;
    private string? _currentFilePath;
    private int _fileSplitCount;

    public bool IsRecording { get; private set; }

    public void StartRecording(string filePath, RecordSource source)
    {
        if (IsRecording) return;

        _currentFilePath = filePath;
        _fileSplitCount = 0;

        switch (source)
        {
            case RecordSource.Microphone:
                _micCapture = new WasapiCapture();
                StartSingleSourceRecording(_micCapture, filePath, mergeStereoToBothEars: true);
                break;
            case RecordSource.SystemAudio:
                _loopCapture = new WasapiLoopbackCapture();
                StartSingleSourceRecording(_loopCapture, filePath, mergeStereoToBothEars: false);
                break;
            case RecordSource.Mix:
                StartMixedRecording(filePath);
                break;
        }

        IsRecording = true;
    }

    private void StartSingleSourceRecording(IWaveIn capture, string filePath, bool mergeStereoToBothEars)
    {
        var format = capture.WaveFormat;
        _writer = AudioFileWriterFactory.Create(filePath, format);

        capture.DataAvailable += (_, a) =>
        {
            if (mergeStereoToBothEars && format.Channels == 2 && format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                MergeStereoToBothEars(a.Buffer.AsSpan(0, a.BytesRecorded));
            }
            WriteBytes(a.Buffer, a.BytesRecorded, format);
        };

        capture.StartRecording();
    }

    /// <summary>
    /// オーディオインターフェース等のマイクにおいて、左（L）にしか音が入らない場合への対策。
    /// LとRの音を合成して両耳から聞こえるようにする。
    /// </summary>
    private static void MergeStereoToBothEars(Span<byte> buffer)
    {
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
        for (int i = 0; i < span.Length - 1; i += 2)
        {
            float mixed = Math.Clamp(span[i] + span[i + 1], -1.0f, 1.0f);
            span[i] = mixed;
            span[i + 1] = mixed;
        }
    }

    /// <summary>キャプチャスレッドからの書き込み。停止処理と競合しないよう lock で保護する。</summary>
    private void WriteBytes(byte[] buffer, int count, WaveFormat format)
    {
        lock (_writerLock)
        {
            if (_writer is null) return; // 停止済みなら破棄後の書き込みを防ぐ
            _writer.Write(buffer, 0, count);
            SplitFileIfNeeded(format);
        }
    }

    /// <summary>ミックス経路からの書き込み。停止処理と競合しないよう lock で保護する。</summary>
    private void WriteSamples(float[] buffer, int count, WaveFormat format)
    {
        lock (_writerLock)
        {
            if (_writer is null) return;
            _writer.WriteSamples(buffer, 0, count);
            SplitFileIfNeeded(format);
        }
    }

    /// <summary>上限サイズを超えたら連番付きの新ファイルへ切り替える。_writerLock 保持中に呼ぶこと。</summary>
    private void SplitFileIfNeeded(WaveFormat format)
    {
        if (_writer is null || _writer.Length <= MaxFileSize || _currentFilePath is null) return;

        _writer.Flush();
        _writer.Dispose();

        _fileSplitCount++;
        string dir = Path.GetDirectoryName(_currentFilePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(_currentFilePath);
        string ext = Path.GetExtension(_currentFilePath);
        string newPath = Path.Combine(dir, $"{name}_{_fileSplitCount:D3}{ext}");

        _writer = AudioFileWriterFactory.Create(newPath, format);
    }

    private void StartMixedRecording(string filePath)
    {
        _micCapture = new WasapiCapture();
        _loopCapture = new WasapiLoopbackCapture();

        var targetFormat = _loopCapture.WaveFormat;
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(targetFormat.SampleRate, targetFormat.Channels));

        // Use a larger buffer to avoid overflow
        _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(5)
        };
        _loopBuffer = new BufferedWaveProvider(_loopCapture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(5)
        };

        _mixer.AddMixerInput(new ContinuousSampleProvider(BuildMicProvider(targetFormat)));
        _mixer.AddMixerInput(new ContinuousSampleProvider(_loopBuffer.ToSampleProvider()));

        _writer = AudioFileWriterFactory.Create(filePath, _mixer.WaveFormat);

        _micCapture.DataAvailable += (_, a) => _micBuffer.AddSamples(a.Buffer, 0, a.BytesRecorded);
        _loopCapture.DataAvailable += (_, a) => _loopBuffer.AddSamples(a.Buffer, 0, a.BytesRecorded);

        _cts = new CancellationTokenSource();
        _mixTask = Task.Run(() => MixLoop(_cts.Token));

        _micCapture.StartRecording();
        _loopCapture.StartRecording();
    }

    /// <summary>マイク入力をループバック側のフォーマットに合わせる変換チェーンを構築する。</summary>
    private ISampleProvider BuildMicProvider(WaveFormat targetFormat)
    {
        ISampleProvider micProvider = _micBuffer!.ToSampleProvider();

        if (_micCapture!.WaveFormat.SampleRate != targetFormat.SampleRate)
        {
            micProvider = new WdlResamplingSampleProvider(micProvider, targetFormat.SampleRate);
        }

        // マイクがステレオデバイスとして認識されている場合、片耳での入力を防ぐため一旦モノラル化
        if (micProvider.WaveFormat.Channels == 2)
        {
            // LとRを等倍でミックスして1chにする
            micProvider = new StereoToMonoSampleProvider(micProvider) { LeftVolume = 1.0f, RightVolume = 1.0f };
        }

        if (micProvider.WaveFormat.Channels == 1 && targetFormat.Channels == 2)
        {
            micProvider = new MonoToStereoSampleProvider(micProvider);
        }
        else if (_micCapture.WaveFormat.Channels != targetFormat.Channels)
        {
            micProvider = new MultiplexingSampleProvider([micProvider], targetFormat.Channels);
        }

        return micProvider;
    }

    private void MixLoop(CancellationToken token)
    {
        // わずかなバッファをためてから処理開始（スタベーション防止）
        Thread.Sleep(100);

        var format = _mixer!.WaveFormat;
        float[] buffer = new float[format.SampleRate * format.Channels]; // 1秒分程度のバッファ
        while (!token.IsCancellationRequested)
        {
            try
            {
                // マイク入力は常にデータが来るため、リアルタイムの基準クロックとして利用
                double bufferedTimeMs = _micBuffer?.BufferedDuration.TotalMilliseconds ?? 0;

                // バッファ枯渇やリサンプル遅延を防ぐため、常に 50ms 分はバッファに残して読み取る
                double timeToReadMs = bufferedTimeMs - 50.0;

                if (timeToReadMs > 0)
                {
                    int samplesToRead = (int)(timeToReadMs / 1000.0 * format.SampleRate * format.Channels);

                    // チャンネル数で割り切れるようにフレームを揃える（チャンネル毎のズレを防ぐ）
                    samplesToRead -= samplesToRead % format.Channels;

                    if (samplesToRead > buffer.Length)
                    {
                        samplesToRead = buffer.Length;
                    }

                    if (samplesToRead > 0)
                    {
                        int read = _mixer.Read(buffer, 0, samplesToRead);
                        if (read > 0)
                        {
                            WriteSamples(buffer, read, format);
                        }
                    }
                }
            }
            catch
            {
                // リソース破棄時の例外などを無視
            }

            Thread.Sleep(20);
        }
    }

    public void StopRecording()
    {
        if (!IsRecording) return;
        IsRecording = false;

        if (_cts != null)
        {
            _cts.Cancel();
            _mixTask?.Wait(1000);
            _cts.Dispose();
            _cts = null;
        }

        _micCapture?.StopRecording();
        _loopCapture?.StopRecording();

        _micCapture?.Dispose();
        _micCapture = null;

        _loopCapture?.Dispose();
        _loopCapture = null;

        lock (_writerLock)
        {
            if (_writer != null)
            {
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    public void Dispose()
    {
        StopRecording();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// MixingSampleProvider は内部プロバイダが 0 を返すと停止してしまうため、
    /// 不足分をゼロ埋めして常に要求数を返すラッパー。
    /// </summary>
    private class ContinuousSampleProvider(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            if (read < count)
            {
                Array.Clear(buffer, offset + read, count - read);
            }
            return count;
        }
    }
}
