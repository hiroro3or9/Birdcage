using System;
using NAudio.Wave;

namespace Birdcage;

/// <summary>録音済みファイルの再生を担当する。</summary>
public class AudioPlayer : IDisposable
{
    private WaveOutEvent? _player;
    private AudioFileReader? _reader;

    public bool IsPlaying { get; private set; }

    /// <summary>再生中のファイルパス。未再生なら null。</summary>
    public string? CurrentFilePath => _reader?.FileName;

    /// <summary>再生が終了(完了または停止)したときに発火。UIスレッド以外から呼ばれる可能性がある。</summary>
    public event EventHandler? PlaybackStopped;

    public void Play(string filePath)
    {
        Stop();

        _reader = new AudioFileReader(filePath);
        _player = new WaveOutEvent();
        _player.Init(_reader);
        _player.PlaybackStopped += (_, _) => PlaybackStopped?.Invoke(this, EventArgs.Empty);
        _player.Play();
        IsPlaying = true;
    }

    public void Stop()
    {
        _player?.Dispose();
        _player = null;

        _reader?.Dispose();
        _reader = null;

        IsPlaying = false;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
