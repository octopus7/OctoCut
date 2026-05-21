using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using OctoCut.Models;
using OctoCut.Services;

namespace OctoCut;

public partial class MainWindow : Window
{
    private static readonly TimeSpan MinimumClipDuration = TimeSpan.FromMilliseconds(250);
    private static readonly HashSet<string> StreamCopyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi",
        ".m2ts",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp4",
        ".mts",
        ".ts",
        ".webm"
    };

    private readonly ObservableCollection<ClipSegment> _clips = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly FfmpegRenderer _renderer = new();
    private readonly AppSettings _settings;

    private bool _isBusy;
    private bool _isSliderDragging;
    private bool _isUpdatingSlider;
    private TimeSpan _duration = TimeSpan.Zero;
    private string? _videoPath;

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsStore.Load();
        ClipList.ItemsSource = _clips;

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _positionTimer.Tick += (_, _) => RefreshPositionUi();
        _positionTimer.Start();

        RefreshFfmpegStatus();
        UpdateCommandState();
    }

    private bool HasRenderableClips => _videoPath is not null && _clips.Count > 0;

    private bool CanUseStreamCopy
    {
        get
        {
            var extension = _videoPath is null ? string.Empty : Path.GetExtension(_videoPath);
            return HasRenderableClips &&
                   StreamCopyExtensions.Contains(extension) &&
                   _clips.All(clip => clip.Duration >= MinimumClipDuration);
        }
    }

    private void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "영상 열기",
            Filter = "영상 파일|*.mp4;*.mov;*.m4v;*.mkv;*.webm;*.avi;*.wmv;*.ts;*.mts;*.m2ts|모든 파일|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LoadVideo(dialog.FileName);
    }

    private void LoadVideo(string path)
    {
        Player.Stop();
        _videoPath = path;
        _duration = TimeSpan.Zero;
        _clips.Clear();

        VideoNameText.Text = Path.GetFileName(path);
        VideoPathText.Text = path;
        PositionSlider.Value = 0;
        PositionSlider.Maximum = 0;
        PositionText.Text = "00:00.000 / 00:00.000";
        StatusText.Text = "영상 정보를 읽는 중...";

        Player.Source = new Uri(path);
        Player.Play();
        Player.Pause();
        UpdateCommandState();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!Player.NaturalDuration.HasTimeSpan)
        {
            StatusText.Text = "영상 길이를 읽을 수 없습니다.";
            UpdateCommandState();
            return;
        }

        _duration = Player.NaturalDuration.TimeSpan;
        PositionSlider.Maximum = Math.Max(0, _duration.TotalSeconds);
        ResetClips();
        RefreshPositionUi();
        StatusText.Text = "영상이 열렸습니다.";
        UpdateCommandState();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        Player.Position = _duration;
        RefreshPositionUi();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPath is null)
        {
            return;
        }

        if (_duration > TimeSpan.Zero && Player.Position >= _duration)
        {
            Player.Position = TimeSpan.Zero;
        }

        Player.Play();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        Player.Pause();
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_videoPath is null || _duration <= TimeSpan.Zero)
        {
            return;
        }

        var position = CurrentEditPosition();
        var clipIndex = FindClipIndex(position);
        if (clipIndex < 0)
        {
            StatusText.Text = "클립 경계에서는 분할할 수 없습니다.";
            return;
        }

        var clip = _clips[clipIndex];
        if (position - clip.Start < MinimumClipDuration || clip.End - position < MinimumClipDuration)
        {
            StatusText.Text = "클립 길이가 너무 짧아지는 위치입니다.";
            return;
        }

        _clips.RemoveAt(clipIndex);
        _clips.Insert(clipIndex, new ClipSegment(0, position, clip.End));
        _clips.Insert(clipIndex, new ClipSegment(0, clip.Start, position));
        RenumberClips();
        ClipList.SelectedIndex = clipIndex;
        StatusText.Text = $"{ClipSegment.FormatTime(position)} 위치에서 분할했습니다.";
        UpdateCommandState();
    }

    private void RemoveClip_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is not ClipSegment clip)
        {
            return;
        }

        var previousIndex = ClipList.SelectedIndex;
        _clips.Remove(clip);
        RenumberClips();

        if (_clips.Count > 0)
        {
            ClipList.SelectedIndex = Math.Min(previousIndex, _clips.Count - 1);
        }

        StatusText.Text = "선택한 클립을 렌더 목록에서 제거했습니다.";
        UpdateCommandState();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.None || !SplitButton.IsEnabled)
        {
            return;
        }

        Split_Click(sender, e);
        e.Handled = true;
    }

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipSegment clip && _videoPath is not null)
        {
            Player.Position = clip.Start;
            RefreshPositionUi();
        }

        UpdateCommandState();
    }

    private void PositionSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isSliderDragging = true;
    }

    private void PositionSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_duration > TimeSpan.Zero)
        {
            Player.Position = TimeSpan.FromSeconds(PositionSlider.Value);
        }

        _isSliderDragging = false;
        RefreshPositionUi();
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingSlider || !_isSliderDragging)
        {
            return;
        }

        UpdatePositionText(TimeSpan.FromSeconds(e.NewValue));
    }

    private void RenderMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        UpdateCommandState();
    }

    private async void RenderCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseStreamCopy)
        {
            MessageBox.Show(
                this,
                "현재 영상 형식이나 클립 상태에서는 무인코딩 렌더를 사용할 수 없습니다. 인코딩 렌더를 사용하세요.",
                "OctoCut",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await RenderAsync(RenderMode.StreamCopy);
    }

    private async void RenderEncode_Click(object sender, RoutedEventArgs e)
    {
        await RenderAsync(RenderMode.Encode);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        _settings.FfmpegPath = window.FfmpegPath;
        SettingsStore.Save(_settings);
        RefreshFfmpegStatus();
        UpdateCommandState();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task RenderAsync(RenderMode mode)
    {
        if (_videoPath is null || _clips.Count == 0)
        {
            return;
        }

        var ffmpegPath = EnsureFfmpegPath();
        if (ffmpegPath is null)
        {
            return;
        }

        var outputPath = SelectOutputPath(mode);
        if (outputPath is null)
        {
            return;
        }

        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(_videoPath), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "출력 파일은 원본 영상과 다른 경로여야 합니다.",
                "OctoCut",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            await _renderer.RenderAsync(
                ffmpegPath,
                _videoPath,
                _clips.ToList(),
                outputPath,
                mode,
                progress,
                CancellationToken.None);

            StatusText.Text = $"렌더 완료: {outputPath}";
            MessageBox.Show(this, "렌더가 완료되었습니다.", "OctoCut", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "렌더 실패";
            MessageBox.Show(this, ex.Message, "렌더 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string? EnsureFfmpegPath()
    {
        var resolvedPath = FfmpegLocator.Resolve(_settings.FfmpegPath);
        if (resolvedPath is not null)
        {
            RefreshFfmpegStatus();
            return resolvedPath;
        }

        var missingWindow = new FfmpegMissingWindow
        {
            Owner = this
        };

        if (missingWindow.ShowDialog() != true)
        {
            return null;
        }

        if (missingWindow.SelectedAction == FfmpegMissingAction.OpenDownload)
        {
            OpenFfmpegDownloadPage();
            return null;
        }

        if (missingWindow.SelectedAction == FfmpegMissingAction.Browse)
        {
            return BrowseAndSaveFfmpegPath();
        }

        return null;
    }

    private string? BrowseAndSaveFfmpegPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = "ffmpeg.exe 선택",
            Filter = "ffmpeg.exe|ffmpeg.exe|실행 파일|*.exe|모든 파일|*.*",
            FileName = "ffmpeg.exe"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        if (!FfmpegLocator.IsUsableExecutable(dialog.FileName))
        {
            MessageBox.Show(this, "선택한 파일을 사용할 수 없습니다.", "OctoCut", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        _settings.FfmpegPath = dialog.FileName;
        SettingsStore.Save(_settings);
        RefreshFfmpegStatus();
        return dialog.FileName;
    }

    private static void OpenFfmpegDownloadPage()
    {
        Process.Start(new ProcessStartInfo("https://ffmpeg.org/download.html")
        {
            UseShellExecute = true
        });
    }

    private string? SelectOutputPath(RenderMode mode)
    {
        if (_videoPath is null)
        {
            return null;
        }

        var sourceExtension = NormalizeExtension(Path.GetExtension(_videoPath), ".mp4");
        var defaultExtension = mode == RenderMode.StreamCopy ? sourceExtension : ".mp4";
        var sourceName = Path.GetFileNameWithoutExtension(_videoPath);

        var dialog = new SaveFileDialog
        {
            Title = mode == RenderMode.StreamCopy ? "무인코딩 렌더 저장" : "인코딩 렌더 저장",
            InitialDirectory = Path.GetDirectoryName(_videoPath),
            FileName = $"{sourceName}_octocut{defaultExtension}",
            DefaultExt = defaultExtension,
            AddExtension = true,
            OverwritePrompt = true,
            Filter = mode == RenderMode.StreamCopy
                ? $"{sourceExtension.TrimStart('.').ToUpperInvariant()} 파일|*{sourceExtension}|MP4 파일|*.mp4|MKV 파일|*.mkv|모든 파일|*.*"
                : "MP4 파일|*.mp4|MKV 파일|*.mkv"
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void ResetClips()
    {
        _clips.Clear();
        if (_duration > TimeSpan.Zero)
        {
            _clips.Add(new ClipSegment(1, TimeSpan.Zero, _duration));
            ClipList.SelectedIndex = 0;
        }
    }

    private int FindClipIndex(TimeSpan position)
    {
        for (var index = 0; index < _clips.Count; index++)
        {
            if (_clips[index].Contains(position))
            {
                return index;
            }
        }

        return -1;
    }

    private void RenumberClips()
    {
        for (var index = 0; index < _clips.Count; index++)
        {
            _clips[index].Index = index + 1;
        }
    }

    private TimeSpan CurrentEditPosition()
    {
        if (_isSliderDragging)
        {
            return TimeSpan.FromSeconds(PositionSlider.Value);
        }

        return Player.Position;
    }

    private void RefreshPositionUi()
    {
        if (_videoPath is null)
        {
            return;
        }

        var position = CurrentEditPosition();
        if (!_isSliderDragging)
        {
            _isUpdatingSlider = true;
            PositionSlider.Value = Math.Clamp(Player.Position.TotalSeconds, 0, PositionSlider.Maximum);
            _isUpdatingSlider = false;
            position = Player.Position;
        }

        UpdatePositionText(position);
    }

    private void UpdatePositionText(TimeSpan position)
    {
        PositionText.Text = $"{ClipSegment.FormatTime(position)} / {ClipSegment.FormatTime(_duration)}";
    }

    private void RefreshFfmpegStatus()
    {
        var resolvedPath = FfmpegLocator.Resolve(_settings.FfmpegPath);
        FfmpegStatusText.Text = resolvedPath is null
            ? "FFmpeg: 찾을 수 없음"
            : $"FFmpeg: {resolvedPath}";
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        BusyProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        var hasVideo = _videoPath is not null && _duration > TimeSpan.Zero;
        var canRender = hasVideo && _clips.Count > 0 && !_isBusy;

        PlayButton.IsEnabled = hasVideo && !_isBusy;
        PauseButton.IsEnabled = hasVideo && !_isBusy;
        SplitButton.IsEnabled = hasVideo && !_isBusy;
        RemoveClipButton.IsEnabled = ClipList.SelectedItem is ClipSegment && !_isBusy;
        RenderCopyMenuItem.IsEnabled = canRender && CanUseStreamCopy;
        RenderEncodeMenuItem.IsEnabled = canRender;
    }

    private static string NormalizeExtension(string? extension, string fallback)
    {
        return string.IsNullOrWhiteSpace(extension) ? fallback : extension;
    }
}
