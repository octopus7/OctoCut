using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using OctoCut.Controls;
using OctoCut.Models;
using OctoCut.Services;

namespace OctoCut;

public partial class MainWindow : Window
{
    private static readonly TimeSpan MinimumClipDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FrameDuration = TimeSpan.FromSeconds(1d / 30d);
    private static readonly IReadOnlyList<TimelineThumbnail> EmptyThumbnails = Array.Empty<TimelineThumbnail>();
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

    private const double TimelinePixelsPerSecond = 52;

    private readonly ObservableCollection<ClipSegment> _clips = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly FfmpegRenderer _renderer = new();
    private readonly TimelinePreviewGenerator _previewGenerator = new();
    private readonly AppSettings _settings;

    private bool _isBusy;
    private bool _isPlaying;
    private CancellationTokenSource? _previewCancellation;
    private string? _ffmpegPath;
    private int _selectedClipIndex = -1;
    private TimeSpan _currentTimelinePosition = TimeSpan.Zero;
    private TimeSpan _sourceDuration = TimeSpan.Zero;
    private string? _videoPath;

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsStore.Load();
        Timeline.SetClips(_clips);
        Timeline.SetPixelsPerSecond(TimelinePixelsPerSecond);
        Timeline.PositionRequested += Timeline_PositionRequested;
        Timeline.ClipSelected += Timeline_ClipSelected;

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _positionTimer.Tick += (_, _) => RefreshPositionUi();
        _positionTimer.Start();

        Closed += (_, _) => _previewCancellation?.Cancel();

        RefreshFfmpegStatus();
        UpdateCommandState();
        UpdateTimelineExtent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!await EnsureRequiredFfmpegOnStartupAsync())
        {
            Close();
        }
    }

    private TimeSpan EditDuration => _clips.Count == 0 ? TimeSpan.Zero : _clips[^1].TimelineEnd;

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
        _previewCancellation?.Cancel();
        _isPlaying = false;
        Player.Stop();

        _videoPath = path;
        _sourceDuration = TimeSpan.Zero;
        _currentTimelinePosition = TimeSpan.Zero;
        _selectedClipIndex = -1;
        _clips.Clear();

        Timeline.SetSourceDuration(TimeSpan.Zero);
        Timeline.SetPreviewAssets(EmptyThumbnails, null);
        Timeline.SetSelectedClipIndex(-1);
        Timeline.SetCurrentPosition(TimeSpan.Zero);
        UpdateTimelineExtent();

        VideoNameText.Text = Path.GetFileName(path);
        VideoPathText.Text = path;
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

        _sourceDuration = Player.NaturalDuration.TimeSpan;
        Timeline.SetSourceDuration(_sourceDuration);
        ResetClips();
        SelectClip(0);
        SetCurrentTimelinePosition(TimeSpan.Zero, seekPlayer: true, keepVisible: true);
        StatusText.Text = "영상이 열렸습니다.";
        UpdateCommandState();
        _ = GenerateTimelinePreviewAsync();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        _isPlaying = false;
        Player.Pause();
        SetCurrentTimelinePosition(EditDuration, seekPlayer: false, keepVisible: true);
        UpdateCommandState();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayback();
    }

    private void TogglePlayback()
    {
        if (_isPlaying)
        {
            PausePlayback();
            return;
        }

        if (_videoPath is null || EditDuration <= TimeSpan.Zero)
        {
            return;
        }

        if (_currentTimelinePosition >= EditDuration)
        {
            SetCurrentTimelinePosition(TimeSpan.Zero, seekPlayer: true, keepVisible: true);
        }

        _isPlaying = true;
        SeekPlayerToCurrentTimelinePosition(refreshPausedFrame: false);
        Player.Play();
        UpdateCommandState();
    }

    private void PausePlayback()
    {
        _isPlaying = false;
        Player.Pause();
        UpdateCommandState();
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        SplitAtCurrentTimelinePosition();
    }

    private void RemoveClip_Click(object sender, RoutedEventArgs e)
    {
        RemoveSelectedClip();
    }

    private void RippleDeleteToggle_Click(object sender, RoutedEventArgs e)
    {
        RefreshClipTimeline();
        SetCurrentTimelinePosition(_currentTimelinePosition, seekPlayer: true, keepVisible: true);
        StatusText.Text = RippleDeleteToggle.IsChecked == true
            ? "리플 삭제가 켜졌습니다."
            : "리플 삭제가 꺼졌습니다.";
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (e.Key == Key.Enter && PlayPauseButton.IsEnabled)
        {
            TogglePlayback();
            e.Handled = true;
        }
        else if (e.Key == Key.S && SplitButton.IsEnabled)
        {
            SplitAtCurrentTimelinePosition();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && RemoveClipButton.IsEnabled)
        {
            RemoveSelectedClip();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && _videoPath is not null)
        {
            StepFrame(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && _videoPath is not null)
        {
            StepFrame(1);
            e.Handled = true;
        }
    }

    private void Timeline_PositionRequested(object? sender, TimelineSeekEventArgs e)
    {
        _isPlaying = false;
        Player.Pause();
        SetCurrentTimelinePosition(e.Position, seekPlayer: true, keepVisible: false);
        UpdateCommandState();
    }

    private void Timeline_ClipSelected(object? sender, TimelineClipEventArgs e)
    {
        SelectClip(e.ClipIndex);
    }

    private void TimelineScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTimelineExtent();
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

    private async void Settings_Click(object sender, RoutedEventArgs e)
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

        if (_videoPath is not null && _sourceDuration > TimeSpan.Zero)
        {
            await GenerateTimelinePreviewAsync();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SplitAtCurrentTimelinePosition()
    {
        if (_videoPath is null || EditDuration <= TimeSpan.Zero)
        {
            return;
        }

        var position = _currentTimelinePosition;
        var clipIndex = FindClipIndexAtTimeline(position);
        if (clipIndex < 0)
        {
            StatusText.Text = "클립 경계에서는 분할할 수 없습니다.";
            return;
        }

        var clip = _clips[clipIndex];
        var sourcePosition = clip.SourceFromTimeline(position);
        if (sourcePosition - clip.Start < MinimumClipDuration || clip.End - sourcePosition < MinimumClipDuration)
        {
            StatusText.Text = "클립 길이가 너무 짧아지는 위치입니다.";
            return;
        }

        _clips.RemoveAt(clipIndex);
        _clips.Insert(clipIndex, new ClipSegment(0, sourcePosition, clip.End));
        _clips.Insert(clipIndex, new ClipSegment(0, clip.Start, sourcePosition));
        RefreshClipTimeline();
        SelectClip(clipIndex);
        SetCurrentTimelinePosition(position, seekPlayer: true, keepVisible: true);
        StatusText.Text = $"{ClipSegment.FormatTime(position)} 위치에서 클립을 분할했습니다.";
        UpdateCommandState();
    }

    private void RemoveSelectedClip()
    {
        if (_selectedClipIndex < 0 || _selectedClipIndex >= _clips.Count)
        {
            return;
        }

        var removedDuration = _clips[_selectedClipIndex].Duration;
        var targetPosition = _clips[_selectedClipIndex].TimelineStart;
        _clips.RemoveAt(_selectedClipIndex);
        RefreshClipTimeline();

        if (_clips.Count == 0)
        {
            SelectClip(-1);
            SetCurrentTimelinePosition(TimeSpan.Zero, seekPlayer: true, keepVisible: true);
        }
        else
        {
            var nextIndex = Math.Min(_selectedClipIndex, _clips.Count - 1);
            SelectClip(nextIndex);
            SetCurrentTimelinePosition(ClampToEditDuration(targetPosition), seekPlayer: true, keepVisible: true);
        }

        StatusText.Text = RippleDeleteToggle.IsChecked == true
            ? $"{ClipSegment.FormatTime(removedDuration)} 구간을 리플 삭제했습니다."
            : "선택한 클립을 삭제했습니다.";
        UpdateCommandState();
    }

    private void StepFrame(int direction)
    {
        if (_videoPath is null || EditDuration <= TimeSpan.Zero)
        {
            return;
        }

        _isPlaying = false;
        Player.Pause();

        var target = _currentTimelinePosition + TimeSpan.FromTicks(FrameDuration.Ticks * direction);
        SetCurrentTimelinePosition(target, seekPlayer: true, keepVisible: true);
        StatusText.Text = $"{(direction < 0 ? "이전" : "다음")} 프레임으로 이동했습니다.";
        UpdateCommandState();
    }

    private async Task RenderAsync(RenderMode mode)
    {
        if (_videoPath is null || _clips.Count == 0)
        {
            return;
        }

        var ffmpegPath = ResolveRequiredFfmpegPath();
        if (ffmpegPath is null)
        {
            Close();
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

    private async Task<bool> EnsureRequiredFfmpegOnStartupAsync()
    {
        _ffmpegPath = FfmpegLocator.Resolve(_settings.FfmpegPath);
        if (_ffmpegPath is not null)
        {
            RefreshFfmpegStatus();
            return true;
        }

        var missingWindow = new FfmpegMissingWindow
        {
            Owner = this
        };

        if (missingWindow.ShowDialog() != true)
        {
            RefreshFfmpegStatus();
            return false;
        }

        _ffmpegPath = await HandleMissingFfmpegActionAsync(missingWindow.SelectedAction);
        RefreshFfmpegStatus();
        return _ffmpegPath is not null;
    }

    private async Task<string?> HandleMissingFfmpegActionAsync(FfmpegMissingAction action)
    {
        if (action == FfmpegMissingAction.InstallWithWinget)
        {
            await InstallFfmpegWithWingetAsync();
            return FfmpegLocator.Resolve(_settings.FfmpegPath);
        }

        if (action == FfmpegMissingAction.OpenDownload)
        {
            OpenFfmpegDownloadPage();
            return null;
        }

        if (action == FfmpegMissingAction.Browse)
        {
            return BrowseAndSaveFfmpegPath();
        }

        return null;
    }

    private string? ResolveRequiredFfmpegPath()
    {
        _ffmpegPath = FfmpegLocator.Resolve(_settings.FfmpegPath);
        RefreshFfmpegStatus();
        return _ffmpegPath;
    }

    private async Task InstallFfmpegWithWingetAsync()
    {
        if (!WingetInstaller.IsAvailable())
        {
            MessageBox.Show(
                this,
                "winget.exe를 찾을 수 없습니다. Microsoft App Installer를 설치하거나 FFmpeg 경로를 직접 지정하세요.",
                "WinGet 없음",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        StatusText.Text = "winget으로 FFmpeg 설치 중...";

        try
        {
            await WingetInstaller.InstallFfmpegAsync(CancellationToken.None);
            RefreshFfmpegStatus();

            var resolvedPath = ResolveRequiredFfmpegPath();
            if (resolvedPath is null)
            {
                StatusText.Text = "FFmpeg 설치 후에도 경로를 찾지 못했습니다.";
                MessageBox.Show(
                    this,
                    "설치가 끝났지만 현재 앱에서 ffmpeg.exe를 찾지 못했습니다. OctoCut을 종료합니다.",
                    "FFmpeg 확인 필요",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            StatusText.Text = $"FFmpeg 설치 확인: {resolvedPath}";
            if (_videoPath is not null && _sourceDuration > TimeSpan.Zero)
            {
                await GenerateTimelinePreviewAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "winget 설치 실패";
            MessageBox.Show(this, ex.Message, "winget 설치 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task GenerateTimelinePreviewAsync()
    {
        if (_videoPath is null || _sourceDuration <= TimeSpan.Zero)
        {
            return;
        }

        var ffmpegPath = ResolveRequiredFfmpegPath();
        if (ffmpegPath is null)
        {
            Close();
            return;
        }

        _previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;

        BusyProgress.Visibility = Visibility.Visible;
        StatusText.Text = "타임라인 썸네일과 오디오 파형 생성 중...";

        try
        {
            var assets = await _previewGenerator.GenerateAsync(ffmpegPath, _videoPath, _sourceDuration, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            Timeline.SetPreviewAssets(assets.Thumbnails, assets.Waveform);
            StatusText.Text = "타임라인 미리 보기를 생성했습니다.";
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            // A newer video or settings change started another preview job.
        }
        catch
        {
            Timeline.SetPreviewAssets(EmptyThumbnails, null);
            StatusText.Text = "썸네일 또는 파형 미리 보기를 생성하지 못했습니다.";
        }
        finally
        {
            if (_previewCancellation == cancellation)
            {
                BusyProgress.Visibility = _isBusy ? Visibility.Visible : Visibility.Collapsed;
            }
        }
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
        if (_sourceDuration > TimeSpan.Zero)
        {
            _clips.Add(new ClipSegment(1, TimeSpan.Zero, _sourceDuration));
        }

        RefreshClipTimeline();
    }

    private void RefreshClipTimeline()
    {
        var timelineStart = TimeSpan.Zero;
        for (var index = 0; index < _clips.Count; index++)
        {
            var clip = _clips[index];
            clip.Index = index + 1;
            clip.SetTimelineStart(timelineStart);
            timelineStart += clip.Duration;
        }

        UpdateTimelineExtent();
        Timeline.InvalidateVisual();
    }

    private void RefreshPositionUi()
    {
        if (_videoPath is null)
        {
            return;
        }

        if (_isPlaying)
        {
            SyncTimelinePositionFromPlayer();
        }

        UpdatePositionText(_currentTimelinePosition);
        Timeline.SetCurrentPosition(_currentTimelinePosition);
    }

    private void SyncTimelinePositionFromPlayer()
    {
        var clipIndex = FindClipIndexAtTimeline(_currentTimelinePosition);
        if (clipIndex < 0)
        {
            StopAtTimelineEnd();
            return;
        }

        var clip = _clips[clipIndex];
        var sourcePosition = Player.Position;
        if (sourcePosition >= clip.End - TimeSpan.FromMilliseconds(25))
        {
            if (clipIndex >= _clips.Count - 1)
            {
                StopAtTimelineEnd();
                return;
            }

            SelectClip(clipIndex + 1);
            SetCurrentTimelinePosition(_clips[clipIndex + 1].TimelineStart, seekPlayer: true, keepVisible: true);
            Player.Play();
            return;
        }

        if (sourcePosition < clip.Start)
        {
            sourcePosition = clip.Start;
        }

        var timelinePosition = clip.TimelineStart + (sourcePosition - clip.Start);
        _currentTimelinePosition = ClampToEditDuration(timelinePosition);
        Timeline.SetCurrentPosition(_currentTimelinePosition);
        EnsureTimelinePositionVisible();
    }

    private void StopAtTimelineEnd()
    {
        _isPlaying = false;
        Player.Pause();
        SetCurrentTimelinePosition(EditDuration, seekPlayer: false, keepVisible: true);
        UpdateCommandState();
    }

    private void SetCurrentTimelinePosition(TimeSpan position, bool seekPlayer, bool keepVisible)
    {
        _currentTimelinePosition = ClampToEditDuration(position);
        Timeline.SetCurrentPosition(_currentTimelinePosition);
        UpdatePositionText(_currentTimelinePosition);

        var clipIndex = FindClipIndexAtTimeline(_currentTimelinePosition);
        if (clipIndex >= 0)
        {
            SelectClip(clipIndex);
        }

        if (seekPlayer)
        {
            SeekPlayerToCurrentTimelinePosition(refreshPausedFrame: !_isPlaying);
        }

        if (keepVisible)
        {
            EnsureTimelinePositionVisible();
        }
    }

    private void SeekPlayerToCurrentTimelinePosition(bool refreshPausedFrame)
    {
        if (_videoPath is null || _clips.Count == 0)
        {
            return;
        }

        Player.Position = SourcePositionFromTimeline(_currentTimelinePosition);
        if (refreshPausedFrame)
        {
            Player.Play();
            Player.Pause();
        }
    }

    private TimeSpan SourcePositionFromTimeline(TimeSpan timelinePosition)
    {
        if (_clips.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var clipIndex = FindClipIndexAtTimeline(timelinePosition);
        if (clipIndex >= 0)
        {
            return _clips[clipIndex].SourceFromTimeline(timelinePosition);
        }

        return timelinePosition >= EditDuration ? _clips[^1].End : _clips[0].Start;
    }

    private int FindClipIndexAtTimeline(TimeSpan timelinePosition)
    {
        for (var index = 0; index < _clips.Count; index++)
        {
            var clip = _clips[index];
            var isLast = index == _clips.Count - 1;
            if (timelinePosition >= clip.TimelineStart &&
                (timelinePosition < clip.TimelineEnd || isLast && timelinePosition <= clip.TimelineEnd))
            {
                return index;
            }
        }

        return -1;
    }

    private void SelectClip(int clipIndex)
    {
        _selectedClipIndex = clipIndex >= 0 && clipIndex < _clips.Count ? clipIndex : -1;
        Timeline.SetSelectedClipIndex(_selectedClipIndex);
        UpdateCommandState();
    }

    private TimeSpan ClampToEditDuration(TimeSpan position)
    {
        if (EditDuration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return position > EditDuration ? EditDuration : position;
    }

    private void UpdatePositionText(TimeSpan position)
    {
        PositionText.Text = $"{ClipSegment.FormatTime(position)} / {ClipSegment.FormatTime(EditDuration)}";
    }

    private void UpdateTimelineExtent()
    {
        var viewportWidth = TimelineScroll.ViewportWidth;
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = Math.Max(0, ActualWidth - 48);
        }

        var timelineWidth = Math.Max(viewportWidth, Math.Max(1, EditDuration.TotalSeconds) * TimelinePixelsPerSecond);
        Timeline.Width = timelineWidth;
        Timeline.SetPixelsPerSecond(TimelinePixelsPerSecond);
        Timeline.InvalidateMeasure();
        Timeline.InvalidateVisual();
    }

    private void EnsureTimelinePositionVisible()
    {
        if (TimelineScroll.ViewportWidth <= 0)
        {
            return;
        }

        var playheadX = _currentTimelinePosition.TotalSeconds * TimelinePixelsPerSecond;
        var left = TimelineScroll.HorizontalOffset;
        var right = left + TimelineScroll.ViewportWidth;
        const double padding = 48;

        if (playheadX < left + padding)
        {
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, playheadX - padding));
        }
        else if (playheadX > right - padding)
        {
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, playheadX - TimelineScroll.ViewportWidth + padding));
        }
    }

    private void RefreshFfmpegStatus()
    {
        var resolvedPath = FfmpegLocator.Resolve(_settings.FfmpegPath);
        _ffmpegPath = resolvedPath;
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
        var hasVideo = _videoPath is not null && EditDuration > TimeSpan.Zero;
        var canRender = hasVideo && _clips.Count > 0 && !_isBusy;

        PlayPauseButton.IsEnabled = hasVideo && !_isBusy;
        PlayPauseButton.Content = _isPlaying ? "일시정지" : "재생";
        PlayPauseButton.ToolTip = _isPlaying ? "일시정지 (Enter)" : "재생 (Enter)";
        SplitButton.IsEnabled = hasVideo && !_isBusy;
        RemoveClipButton.IsEnabled = _selectedClipIndex >= 0 && _selectedClipIndex < _clips.Count && !_isBusy;
        RenderCopyMenuItem.IsEnabled = canRender && CanUseStreamCopy;
        RenderEncodeMenuItem.IsEnabled = canRender;
    }

    private static string NormalizeExtension(string? extension, string fallback)
    {
        return string.IsNullOrWhiteSpace(extension) ? fallback : extension;
    }
}
