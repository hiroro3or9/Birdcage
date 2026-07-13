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
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopCapture;
    private IAudioFileWriter? _writer;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _loopBuffer;
    private MixingSampleProvider? _mixer;
    private Task? _mixTask;
    private CancellationTokenSource? _cts;
    private string? _currentFilePath;

    public bool IsRecording { get; private set; }

    public void StartRecording(string filePath, RecordSource source)
    {
        if (IsRecording) return;

        switch (source)
        {
            case RecordSource.Microphone:
                _micCapture = new WasapiCapture();
                var micFormat = _micCapture.WaveFormat;
                _writer = AudioFileWriterFactory.Create(filePath, micFormat);
                _micCapture.DataAvailable += (_, a) => 
                {
                    // オーディオインターフェース等のマイクにおいて、左（L）にしか音が入らない場合への対策
                    // LとRの音を合成して両耳から聞こえるようにする
                    if (micFormat.Channels == 2 && micFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                    {
                        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(a.Buffer.AsSpan(0, a.BytesRecorded));
                        for (int i = 0; i < span.Length - 1; i += 2)
                        {
                            float mixed = span[i] + span[i + 1];
                            if (mixed > 1.0f) mixed = 1.0f;
                            else if (mixed < -1.0f) mixed = -1.0f;
                            span[i] = mixed;
                            span[i + 1] = mixed;
                        }
                    }
                    _writer.Write(a.Buffer, 0, a.BytesRecorded);
                    CheckFileSizeAndSplit(ref _writer, _currentFilePath ?? filePath, micFormat);
                };
                _micCapture.StartRecording();
                break;
            case RecordSource.SystemAudio:
                _loopCapture = new WasapiLoopbackCapture();
                _writer = AudioFileWriterFactory.Create(filePath, _loopCapture.WaveFormat);
                _loopCapture.DataAvailable += (_, a) => 
                {
                    _writer.Write(a.Buffer, 0, a.BytesRecorded);
                    CheckFileSizeAndSplit(ref _writer, filePath, _loopCapture.WaveFormat);
                };
                _loopCapture.StartRecording();
                break;
            case RecordSource.Mix:
                StartMixedRecording(filePath);
                break;
        }

        _currentFilePath = filePath;

        IsRecording = true;
    }

    private int _fileSplitCount = 0;
    private const long MaxFileSize = 3L * 1024 * 1024 * 1024; // 3GBで安全に分割

    private void CheckFileSizeAndSplit(ref IAudioFileWriter? writer, string originalFilePath, WaveFormat format)
    {
        if (writer != null && writer.Length > MaxFileSize)
        {
            writer.Flush();
            writer.Dispose();
            
            _fileSplitCount++;
            string dir = Path.GetDirectoryName(originalFilePath) ?? "";
            string name = Path.GetFileNameWithoutExtension(originalFilePath);
            string ext = Path.GetExtension(originalFilePath);
            string newPath = Path.Combine(dir, $"{name}_{_fileSplitCount:D3}{ext}");

            writer = AudioFileWriterFactory.Create(newPath, format);
        }
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

        ISampleProvider micProvider = _micBuffer.ToSampleProvider();
        if (_micCapture.WaveFormat.SampleRate != targetFormat.SampleRate)
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

        ISampleProvider loopProvider = _loopBuffer.ToSampleProvider();

        // MixingSampleProvider automatically stops if any inner provider returns 0.
        // We ensure data flow continues or padds with zeroes.
        _mixer.AddMixerInput(new ContinuousSampleProvider(micProvider));
        _mixer.AddMixerInput(new ContinuousSampleProvider(loopProvider));

        _writer = AudioFileWriterFactory.Create(filePath, _mixer.WaveFormat);

        _micCapture.DataAvailable += (_, a) => _micBuffer.AddSamples(a.Buffer, 0, a.BytesRecorded);
        _loopCapture.DataAvailable += (_, a) => _loopBuffer.AddSamples(a.Buffer, 0, a.BytesRecorded);

        _cts = new CancellationTokenSource();
        _mixTask = Task.Run(() => MixLoop(_cts.Token));

        _micCapture.StartRecording();
        _loopCapture.StartRecording();
    }

    private void MixLoop(CancellationToken token)
    {
        // わずかなバッファをためてから処理開始（スタベーション防止）
        Thread.Sleep(100);

        float[] buffer = new float[_mixer!.WaveFormat.SampleRate * _mixer.WaveFormat.Channels]; // 1秒分程度のバッファ
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
                    int samplesToRead = (int)(timeToReadMs / 1000.0 * _mixer.WaveFormat.SampleRate * _mixer.WaveFormat.Channels);
                    
                    // チャンネル数で割り切れるようにフレームを揃える（チャンネル毎のズレを防ぐ）
                    samplesToRead -= samplesToRead % _mixer.WaveFormat.Channels;

                    if (samplesToRead > buffer.Length)
                    {
                        samplesToRead = buffer.Length;
                    }

                    if (samplesToRead > 0)
                    {
                        int read = _mixer.Read(buffer, 0, samplesToRead);
                        if (read > 0)
                        {
                            _writer!.WriteSamples(buffer, 0, read);
                            if (_currentFilePath != null)
                            {
                                CheckFileSizeAndSplit(ref _writer, _currentFilePath, _mixer.WaveFormat);
                            }
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

        if (_writer != null)
        {
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
        }
    }

    public void Dispose()
    {
        StopRecording();
        GC.SuppressFinalize(this);
    }

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
