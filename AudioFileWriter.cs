using System;
using System.IO;
using NAudio.Lame;
using NAudio.Wave;

namespace Birdcage;

/// <summary>録音の出力形式。追加時はここと <see cref="AudioFileWriterFactory"/> を拡張する。</summary>
public enum AudioFormat
{
    MP3,
    WAV
    // 将来: WMA, AAC(M4A) は MediaFoundationEncoder ベースの実装を追加
}

/// <summary>形式ごとの書き込み処理を抽象化するインターフェース。</summary>
public interface IAudioFileWriter : IDisposable
{
    /// <summary>出力ファイルの現在のサイズ(バイト)。分割判定に使用。</summary>
    long Length { get; }

    void Write(byte[] buffer, int offset, int count);
    void WriteSamples(float[] samples, int offset, int count);
    void Flush();
}

public static class AudioFileWriterFactory
{
    public static string GetExtension(AudioFormat format) => format switch
    {
        AudioFormat.MP3 => ".mp3",
        AudioFormat.WAV => ".wav",
        _ => throw new NotSupportedException($"未対応の形式: {format}")
    };

    /// <summary>ファイル一覧表示などに使う対応拡張子。</summary>
    public static string[] SupportedExtensions { get; } = [".wav", ".mp3"];

    /// <summary>拡張子から適切なライターを生成する。</summary>
    public static IAudioFileWriter Create(string filePath, WaveFormat inputFormat)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".mp3" => new Mp3AudioFileWriter(filePath, inputFormat),
            ".wav" => new WavAudioFileWriter(filePath, inputFormat),
            var ext => throw new NotSupportedException($"未対応の拡張子: {ext}")
        };
    }
}

/// <summary>従来通りの WAV 書き込み。</summary>
public sealed class WavAudioFileWriter(string filePath, WaveFormat format) : IAudioFileWriter
{
    private readonly WaveFileWriter _writer = new(filePath, format);

    public long Length => _writer.Length;

    public void Write(byte[] buffer, int offset, int count) => _writer.Write(buffer, offset, count);
    public void WriteSamples(float[] samples, int offset, int count) => _writer.WriteSamples(samples, offset, count);
    public void Flush() => _writer.Flush();
    public void Dispose() => _writer.Dispose();
}

/// <summary>LAME による MP3 リアルタイムエンコード書き込み。</summary>
public sealed class Mp3AudioFileWriter : IAudioFileWriter
{
    private const int BitRate = 192; // kbps

    private readonly FileStream _fileStream;
    private readonly LameMP3FileWriter _writer;

    public Mp3AudioFileWriter(string filePath, WaveFormat inputFormat)
    {
        // 出力サイズ(Length)を取得するため FileStream を自前で保持する
        _fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        try
        {
            // LameMP3FileWriter は 16bit PCM / IEEE Float 入力を自動変換して受け付ける
            _writer = new LameMP3FileWriter(_fileStream, inputFormat, BitRate);
        }
        catch
        {
            _fileStream.Dispose();
            throw;
        }
    }

    public long Length => _fileStream.Length;

    public void Write(byte[] buffer, int offset, int count) => _writer.Write(buffer, offset, count);

    public void WriteSamples(float[] samples, int offset, int count)
    {
        // float サンプルをバイト列に変換して書き込む(ミックス録音経路は IEEE Float 前提)
        byte[] bytes = new byte[count * sizeof(float)];
        Buffer.BlockCopy(samples, offset * sizeof(float), bytes, 0, bytes.Length);
        _writer.Write(bytes, 0, bytes.Length);
    }

    public void Flush() => _writer.Flush();

    public void Dispose()
    {
        _writer.Dispose(); // 内部で最終フレームを書き出す
        _fileStream.Dispose();
    }
}
