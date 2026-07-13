using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.Wave;

namespace Birdcage;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioRecorder _recorder = new();
    private WaveOutEvent? _player;
    private AudioFileReader? _audioFileReader;
    private readonly DispatcherTimer _timer;
    private readonly string _saveDirectory;

    [ObservableProperty]
    private ObservableCollection<FileInfo> _audioFiles = [];

    [ObservableProperty]
    private FileInfo? _selectedAudioFile;

    [ObservableProperty]
    private RecordSource _selectedRecordSource = RecordSource.SystemAudio;

    [ObservableProperty]
    private AudioFormat _selectedAudioFormat = AudioFormat.MP3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingButtonText))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaybackButtonText))]
    private bool _isPlaying;

    [ObservableProperty]
    private TimeSpan _recordingTime;

    public RecordSource[] RecordSources { get; } = Enum.GetValues<RecordSource>();
    public AudioFormat[] AudioFormats { get; } = Enum.GetValues<AudioFormat>();

    public string RecordingButtonText => IsRecording ? "録音停止" : "録音開始";
    public string PlaybackButtonText => IsPlaying ? "停止" : "再生";

    public MainViewModel()
    {
        _saveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Birdcage");
        Directory.CreateDirectory(_saveDirectory);
        RefreshAudioFiles();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (IsRecording)
            {
                RecordingTime = RecordingTime.Add(TimeSpan.FromSeconds(1));
            }
        };
    }

    [RelayCommand]
    private void ToggleRecording()
    {
        if (IsRecording)
        {
            _recorder.StopRecording();
            IsRecording = false;
            _timer.Stop();
            RefreshAudioFiles();
            return;
        }

        string fileName = $"Record_{DateTime.Now:yyyyMMdd_HHmmss}{AudioFileWriterFactory.GetExtension(SelectedAudioFormat)}";
        string filePath = Path.Combine(_saveDirectory, fileName);
        
        try
        {
            _recorder.StartRecording(filePath, SelectedRecordSource);
            IsRecording = true;
            RecordingTime = TimeSpan.Zero;
            _timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"録音の開始に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void SetRecordSource(RecordSource source)
    {
        if (!IsRecording)
        {
            SelectedRecordSource = source;
            ToggleRecording(); // ソースを選んだらそのまま録音開始
        }
    }

    [RelayCommand]
    private void TogglePlayback()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }
        
        if (SelectedAudioFile is null) return;

        try
        {
            _audioFileReader = new AudioFileReader(SelectedAudioFile.FullName);
            _player = new WaveOutEvent();
            _player.Init(_audioFileReader);
            _player.PlaybackStopped += (_, _) => App.Current.Dispatcher.Invoke(StopPlayback);
            _player.Play();
            IsPlaying = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"再生に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            StopPlayback();
        }
    }

    private void StopPlayback()
    {
        _player?.Stop();
        _player?.Dispose();
        _player = null;
        
        _audioFileReader?.Dispose();
        _audioFileReader = null;
        
        IsPlaying = false;
    }

    [RelayCommand]
    private void DeleteFile()
    {
        if (SelectedAudioFile is null) return;

        var result = MessageBox.Show(
            $"{SelectedAudioFile.Name} を削除しますか？", 
            "確認", 
            MessageBoxButton.YesNo, 
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (IsPlaying && _audioFileReader?.FileName == SelectedAudioFile.FullName)
            {
                StopPlayback();
            }

            File.Delete(SelectedAudioFile.FullName);
            RefreshAudioFiles();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"削除に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void RefreshAudioFiles()
    {
        var files = new DirectoryInfo(_saveDirectory)
            .GetFiles()
            .Where(f => AudioFileWriterFactory.SupportedExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(f => f.CreationTime);
        
        AudioFiles.Clear();
        foreach (var file in files)
        {
            AudioFiles.Add(file);
        }
    }

    public void Dispose()
    {
        _recorder.Dispose();
        StopPlayback();
        _timer.Stop();
        GC.SuppressFinalize(this);
    }
}
