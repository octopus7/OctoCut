using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private static readonly TimeSpan TransitionPreviewEdgeTolerance = TimeSpan.FromTicks(FrameDuration.Ticks / 2);
    private static readonly TimeSpan StreamCopyTimeTolerance = TimeSpan.FromMilliseconds(1);
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

    private const double DefaultTimelinePixelsPerSecond = 52;
    private const double MinimumTimelinePixelsPerSecond = 12;
    private const double MaximumTimelinePixelsPerSecond = 240;
    private const double TimelineScaleStep = 1.15;
    private const int PreviewFrameMaxWidth = 960;
    private const int PreviewFrameMaxHeight = 540;

    private readonly ObservableCollection<ClipSegment> _clips = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly FfmpegRenderer _renderer = new();
    private readonly FramePreviewRenderer _framePreviewRenderer = new();
    private readonly TimelinePreviewGenerator _previewGenerator = new();
    private readonly LocalizationManager _localization = new();
    private readonly AppSettings _settings;
    private readonly List<string> _debugMessages = new();
    private readonly List<CancellationTokenSource> _previewCancellations = new();

    private bool _isBusy;
    private bool _isClosing;
    private bool _isPlaying;
    private CancellationTokenSource? _framePreviewCancellation;
    private DebugLogWindow? _debugLogWindow;
    private readonly Dictionary<string, TimeSpan> _sourceDurations = new(StringComparer.OrdinalIgnoreCase);
    private int _framePreviewRequestId;
    private string? _ffmpegPath;
    private string? _activeSourcePath;
    private string? _pendingAppendPath;
    private TimeSpan? _pendingPlayerPosition;
    private int _selectedClipIndex = -1;
    private TimeSpan? _spacePlaybackStartPosition;
    private TimeSpan _currentTimelinePosition = TimeSpan.Zero;
    private double _timelinePixelsPerSecond = DefaultTimelinePixelsPerSecond;

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsStore.Load();
        InitializeLanguage();
        Timeline.SetClips(_clips);
        Timeline.SetPixelsPerSecond(_timelinePixelsPerSecond);
        Timeline.PositionRequested += Timeline_PositionRequested;
        Timeline.ClipSelected += Timeline_ClipSelected;
        Timeline.ClipDragRequested += Timeline_ClipDragRequested;
        Timeline.ScaleRequested += Timeline_ScaleRequested;
        Timeline.ScrollRequested += Timeline_ScrollRequested;

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _positionTimer.Tick += (_, _) => RefreshPositionUi();
        _positionTimer.Start();

        Closing += (_, _) => _isClosing = true;
        Closed += (_, _) =>
        {
            foreach (var previewCancellation in _previewCancellations.ToList())
            {
                previewCancellation.Cancel();
            }

            _framePreviewCancellation?.Cancel();
            _debugLogWindow?.Close();
        };

        RefreshFfmpegStatus();
        ApplyLocalization();
        UpdateCommandState();
        UpdateTimelineExtent();
    }

    private void InitializeLanguage()
    {
        _localization.Reload();

        if (string.IsNullOrWhiteSpace(_settings.LanguageCode))
        {
            _settings.LanguageCode = _localization.DetectPreferredLanguageCode();
            SettingsStore.Save(_settings);
        }

        _localization.SetLanguage(_settings.LanguageCode);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!await EnsureRequiredFfmpegOnStartupAsync())
        {
            Close();
        }
    }

    private TimeSpan EditDuration => _clips.Count == 0 ? TimeSpan.Zero : _clips[^1].TimelineEnd;

    private bool HasRenderableClips => _clips.Count > 0;

    private bool CanCaptureCurrentFrame => _clips.Count > 0 && EditDuration > TimeSpan.Zero && !_isBusy;

    private bool CanUseStreamCopy
    {
        get
        {
            var extension = _clips.Count == 0 ? string.Empty : Path.GetExtension(_clips[0].SourcePath);
            return HasRenderableClips &&
                   StreamCopyExtensions.Contains(extension) &&
                   _clips.All(clip => clip.Duration >= MinimumClipDuration) &&
                   IsStreamCopyTimelineSafe();
        }
    }

    private bool IsStreamCopyTimelineSafe()
    {
        var expectedSourceStart = TimeSpan.Zero;
        var sourcePath = _clips[0].SourcePath;
        foreach (var clip in _clips)
        {
            if (!string.Equals(clip.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                clip.TransitionInDuration > StreamCopyTimeTolerance)
            {
                return false;
            }

            if (!NearlyEqual(clip.Start, expectedSourceStart))
            {
                return false;
            }

            expectedSourceStart = clip.End;
        }

        return true;
    }

    private static bool NearlyEqual(TimeSpan left, TimeSpan right)
    {
        return (left - right).Duration() <= StreamCopyTimeTolerance;
    }

    private void ApplyLocalization()
    {
        FileMenu.Header = _localization.Text("Main.Menu.File");
        OpenVideoMenuItem.Header = _localization.Text("Main.Menu.OpenVideo");
        SaveFrameMenuItem.Header = _localization.Text("Main.Menu.SaveFrame");
        SettingsMenuItem.Header = _localization.Text("Main.Menu.Settings");
        ExitMenuItem.Header = _localization.Text("Main.Menu.Exit");
        RenderMenu.Header = _localization.Text("Main.Menu.Render");
        RenderCopyMenuItem.Header = _localization.Text("Main.Menu.RenderCopy");
        RenderCopyMenuItem.ToolTip = _localization.Text("Main.Menu.RenderCopy.ToolTip");
        RenderEncodeMenuItem.Header = _localization.Text("Main.Menu.RenderEncode");
        RenderEncodeMenuItem.ToolTip = _localization.Text("Main.Menu.RenderEncode.ToolTip");
        ViewMenu.Header = _localization.Text("Main.Menu.View");
        DebugLogMenuItem.Header = _localization.Text("Main.Menu.DebugLog");
        HelpMenu.Header = _localization.Text("Main.Menu.Help");
        ShortcutsMenuItem.Header = _localization.Text("Main.Menu.Shortcuts");

        UpdateWindowTitle();
        OpenButton.Content = _localization.Text("Main.Button.OpenVideo");
        TransitionPreviewCheckBox.Content = _localization.Text("Main.CheckBox.TransitionPreview");
        TransitionPreviewCheckBox.ToolTip = _localization.Text("Main.CheckBox.TransitionPreview.ToolTip");
        SplitButton.Content = _localization.Text("Main.Button.Split");
        SplitButton.ToolTip = _localization.Text("Main.Button.Split.ToolTip");
        RemoveClipButton.Content = _localization.Text("Main.Button.Delete");
        MoveClipEarlierButton.Content = _localization.Text("Main.Button.MoveEarlier");
        MoveClipEarlierButton.ToolTip = _localization.Text("Main.Button.MoveEarlier.ToolTip");
        MoveClipLaterButton.Content = _localization.Text("Main.Button.MoveLater");
        MoveClipLaterButton.ToolTip = _localization.Text("Main.Button.MoveLater.ToolTip");
        ClearClipTransitionButton.Content = _localization.Text("Main.Button.ClearTransition");
        ClearClipTransitionButton.ToolTip = _localization.Text("Main.Button.ClearTransition.ToolTip");
        RippleDeleteToggle.ToolTip = _localization.Text("Main.Button.RippleDelete.ToolTip");
        Timeline.EmptyTimelineText = _localization.Text("Timeline.Empty");
        Timeline.MissingThumbnailText = _localization.Text("Timeline.Thumbnail");
        Timeline.MissingWaveformText = _localization.Text("Timeline.Waveform");
        _debugLogWindow?.ApplyLocalization();
        UpdatePositionText(_currentTimelinePosition);
        Timeline.InvalidateVisual();
        UpdateCommandState();
    }

    private void UpdateWindowTitle()
    {
        Title = _clips.Count switch
        {
            0 => "OctoCut",
            1 => $"{Path.GetFileName(_clips[0].SourcePath)} - OctoCut",
            _ => $"{_clips.Count} clips - OctoCut"
        };
    }

    private void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _localization.Text("Dialog.OpenVideo.Title"),
            Filter = _localization.Text("Dialog.Video.Filter")
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        AppendVideo(dialog.FileName);
    }

    private void AppendVideo(string path)
    {
        CancelViewportFrameRefresh();
        _isPlaying = false;
        _spacePlaybackStartPosition = null;
        Player.Stop();
        FramePreviewImage.Source = null;
        FramePreviewImage.Visibility = Visibility.Collapsed;

        _pendingAppendPath = path;
        _pendingPlayerPosition = null;
        _activeSourcePath = path;
        LogDebug(_localization.Text("Log.Video.Reading"));

        Player.Source = new Uri(path);
        Player.Play();
        Player.Pause();
        UpdateCommandState();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_pendingAppendPath is not null && IsCurrentPlayerSource(_pendingAppendPath))
        {
            AppendPendingVideoFromPlayer();
            return;
        }

        if (_pendingPlayerPosition.HasValue)
        {
            Player.Position = _pendingPlayerPosition.Value;
            _pendingPlayerPosition = null;
            if (_isPlaying)
            {
                Player.Play();
            }
            else
            {
                Player.Pause();
            }
        }
    }

    private void AppendPendingVideoFromPlayer()
    {
        var path = _pendingAppendPath;
        _pendingAppendPath = null;
        if (path is null)
        {
            return;
        }

        if (!Player.NaturalDuration.HasTimeSpan)
        {
            LogDebug(_localization.Text("Log.Video.DurationFailed"));
            UpdateCommandState();
            return;
        }

        var sourceDuration = Player.NaturalDuration.TimeSpan;
        _sourceDurations[path] = sourceDuration;

        var clip = new ClipSegment(0, path, sourceDuration, TimeSpan.Zero, sourceDuration);
        _clips.Add(clip);
        RefreshClipTimeline();
        var appendedIndex = _clips.Count - 1;
        SelectClip(appendedIndex);
        SetCurrentTimelinePosition(clip.TimelineStart, seekPlayer: true, keepVisible: true);
        UpdateWindowTitle();
        LogDebug(_localization.Text("Log.Video.Opened"));
        UpdateCommandState();
        _ = GenerateTimelinePreviewAsync(path, sourceDuration);
    }

    private bool IsCurrentPlayerSource(string path)
    {
        return Player.Source is not null &&
               string.Equals(Player.Source.LocalPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        _isPlaying = false;
        _spacePlaybackStartPosition = null;
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

        StartPlayback(rememberSpaceStart: false);
    }

    private void ToggleSpacePlayback()
    {
        if (_isPlaying)
        {
            var returnPosition = _spacePlaybackStartPosition;
            PausePlayback();

            if (returnPosition.HasValue)
            {
                SetCurrentTimelinePosition(returnPosition.Value, seekPlayer: true, keepVisible: true);
            }

            return;
        }

        StartPlayback(rememberSpaceStart: true);
    }

    private void StartPlayback(bool rememberSpaceStart)
    {
        if (_clips.Count == 0 || EditDuration <= TimeSpan.Zero)
        {
            return;
        }

        if (_currentTimelinePosition >= EditDuration)
        {
            SetCurrentTimelinePosition(TimeSpan.Zero, seekPlayer: true, keepVisible: true);
        }

        _spacePlaybackStartPosition = rememberSpaceStart ? _currentTimelinePosition : null;
        _isPlaying = true;
        CancelViewportFrameRefresh();
        FramePreviewImage.Visibility = Visibility.Collapsed;
        SeekPlayerToCurrentTimelinePosition();
        Player.Play();
        UpdateCommandState();
    }

    private void PausePlayback()
    {
        _isPlaying = false;
        _spacePlaybackStartPosition = null;
        Player.Pause();
        RequestViewportFrameRefresh();
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

    private void MoveClipEarlier_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedClipBy(-1);
    }

    private void MoveClipLater_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedClipBy(1);
    }

    private void ClearClipTransition_Click(object sender, RoutedEventArgs e)
    {
        ClearSelectedClipTransitions();
    }

    private void TransitionPreviewCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_isPlaying)
        {
            RequestViewportFrameRefresh();
        }
    }

    private void RippleDeleteToggle_Click(object sender, RoutedEventArgs e)
    {
        RefreshClipTimeline();
        SetCurrentTimelinePosition(_currentTimelinePosition, seekPlayer: true, keepVisible: true);
        LogDebug(RippleDeleteToggle.IsChecked == true
            ? _localization.Text("Log.Ripple.On")
            : _localization.Text("Log.Ripple.Off"));
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || IsTextEditingInput(e.OriginalSource))
        {
            return;
        }

        if (e.Key == Key.Enter && PlayPauseButton.IsEnabled)
        {
            TogglePlayback();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && PlayPauseButton.IsEnabled)
        {
            ToggleSpacePlayback();
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
        else if (e.Key == Key.Left && _clips.Count > 0)
        {
            StepFrame(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && _clips.Count > 0)
        {
            StepFrame(1);
            e.Handled = true;
        }
        else if (e.Key == Key.F12 && CanCaptureCurrentFrame)
        {
            e.Handled = true;
            await CopyCurrentFrameToClipboardAsync();
        }
    }

    private static bool IsTextEditingInput(object? source)
    {
        return source is TextBoxBase or PasswordBox;
    }

    private void Timeline_PositionRequested(object? sender, TimelineSeekEventArgs e)
    {
        _isPlaying = false;
        _spacePlaybackStartPosition = null;
        Player.Pause();
        SetCurrentTimelinePosition(e.Position, seekPlayer: true, keepVisible: false);
        UpdateCommandState();
    }

    private void Timeline_ClipSelected(object? sender, TimelineClipEventArgs e)
    {
        SelectClip(e.ClipIndex);
    }

    private void Timeline_ClipDragRequested(object? sender, TimelineClipDragEventArgs e)
    {
        MoveClipToRequestedStart(e.ClipIndex, e.RequestedStart);
    }

    private void Timeline_ScaleRequested(object? sender, TimelineScaleEventArgs e)
    {
        AdjustTimelineScale(e.WheelDelta, e.AnchorPosition);
    }

    private void Timeline_ScrollRequested(object? sender, TimelineScrollEventArgs e)
    {
        ScrollTimelineView(e.WheelDelta);
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
                _localization.Text("Message.RenderCopyUnavailable"),
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
        _localization.Reload();
        _localization.SetLanguage(_settings.LanguageCode);

        var window = new SettingsWindow(_settings, _localization)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        _settings.FfmpegPath = window.FfmpegPath;
        _settings.LanguageCode = window.LanguageCode ?? _localization.CurrentLanguageCode;
        SettingsStore.Save(_settings);
        _localization.Reload();
        _localization.SetLanguage(_settings.LanguageCode);
        ApplyLocalization();
        RefreshFfmpegStatus();
        UpdateCommandState();

        foreach (var clip in _clips)
        {
            await GenerateTimelinePreviewAsync(clip.SourcePath, clip.SourceDuration);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void SaveCurrentFrame_Click(object sender, RoutedEventArgs e)
    {
        var captureRequest = GetCurrentFrameCaptureRequest();
        if (captureRequest is null)
        {
            return;
        }

        var outputPath = SelectFrameOutputPath(captureRequest.Value.SourcePath, captureRequest.Value.FrameNumber);
        if (outputPath is null)
        {
            return;
        }

        await SaveCurrentFrameAsync(captureRequest.Value.SourcePath, captureRequest.Value.SourcePosition, outputPath);
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        var window = new ShortcutsWindow(_localization)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void DebugLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DebugLogMenuItem.IsChecked)
        {
            ShowDebugLogWindow();
            return;
        }

        _debugLogWindow?.Hide();
    }

    private void ShowDebugLogWindow()
    {
        if (_debugLogWindow is null)
        {
            _debugLogWindow = new DebugLogWindow(_localization)
            {
                Owner = this
            };
            _debugLogWindow.Closing += (_, args) =>
            {
                if (_isClosing)
                {
                    return;
                }

                args.Cancel = true;
                _debugLogWindow.Hide();
                DebugLogMenuItem.IsChecked = false;
            };
        }

        _debugLogWindow.SetLogText(string.Join(Environment.NewLine, _debugMessages));
        _debugLogWindow.Show();
        _debugLogWindow.Activate();
    }

    private void LogDebug(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _debugMessages.Add(line);
        _debugLogWindow?.AppendLog(line);
    }

    private async Task CopyCurrentFrameToClipboardAsync()
    {
        var captureRequest = GetCurrentFrameCaptureRequest();
        if (captureRequest is null)
        {
            return;
        }

        var ffmpegPath = ResolveRequiredFfmpegPath();
        if (ffmpegPath is null)
        {
            Close();
            return;
        }

        SetBusy(true);
        try
        {
            var bitmap = await _framePreviewRenderer.CaptureFrameAsync(
                ffmpegPath,
                captureRequest.Value.SourcePath,
                captureRequest.Value.SourcePosition,
                CancellationToken.None);

            Clipboard.SetImage(bitmap);
            LogDebug(_localization.Format("Log.Frame.CopiedToClipboard", captureRequest.Value.FrameNumber));
        }
        catch (Exception ex)
        {
            LogDebug(_localization.Text("Log.Frame.ClipboardFailed"));
            MessageBox.Show(this, ex.Message, _localization.Text("Message.FrameCapture.FailTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveCurrentFrameAsync(string sourcePath, TimeSpan sourcePosition, string outputPath)
    {
        var ffmpegPath = ResolveRequiredFfmpegPath();
        if (ffmpegPath is null)
        {
            Close();
            return;
        }

        SetBusy(true);
        try
        {
            await _framePreviewRenderer.SaveFrameAsync(
                ffmpegPath,
                sourcePath,
                sourcePosition,
                outputPath,
                CancellationToken.None);

            LogDebug(_localization.Format("Log.Frame.SavedToFile", outputPath));
            MessageBox.Show(this, _localization.Text("Message.FrameSave.Complete"), "OctoCut", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogDebug(_localization.Text("Log.Frame.SaveFailed"));
            MessageBox.Show(this, ex.Message, _localization.Text("Message.FrameSave.FailTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private (string SourcePath, TimeSpan SourcePosition, long FrameNumber)? GetCurrentFrameCaptureRequest()
    {
        if (!CanCaptureCurrentFrame)
        {
            return null;
        }

        if (_isPlaying)
        {
            SyncTimelinePositionFromPlayer();
        }

        var sourceLocation = SourceLocationFromTimeline(_currentTimelinePosition);
        return (
            sourceLocation.SourcePath,
            ClampSourcePreviewTime(sourceLocation.SourcePath, sourceLocation.SourcePosition),
            FrameNumberFromTime(_currentTimelinePosition));
    }

    private string? SelectFrameOutputPath(string sourcePath, long frameNumber)
    {
        var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        var dialog = new SaveFileDialog
        {
            Title = _localization.Text("Dialog.FrameSave.Title"),
            InitialDirectory = Path.GetDirectoryName(sourcePath),
            FileName = $"{sourceName}_frame_{frameNumber:000000}.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            Filter = _localization.Text("Dialog.Png.Filter")
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void SplitAtCurrentTimelinePosition()
    {
        if (_clips.Count == 0 || EditDuration <= TimeSpan.Zero)
        {
            return;
        }

        var position = _currentTimelinePosition;
        var clipIndex = FindClipIndexAtTimeline(position);
        if (clipIndex < 0)
        {
            LogDebug(_localization.Text("Log.Split.Boundary"));
            return;
        }

        var clip = _clips[clipIndex];
        var sourcePosition = clip.SourceFromTimeline(position);
        if (sourcePosition - clip.Start < MinimumClipDuration || clip.End - sourcePosition < MinimumClipDuration)
        {
            LogDebug(_localization.Text("Log.Split.TooShort"));
            return;
        }

        _clips.RemoveAt(clipIndex);
        var transitionInDuration = clip.TransitionInDuration;
        _clips.Insert(clipIndex, new ClipSegment(0, clip.SourcePath, clip.SourceDuration, sourcePosition, clip.End));
        _clips.Insert(clipIndex, new ClipSegment(0, clip.SourcePath, clip.SourceDuration, clip.Start, sourcePosition)
        {
            TransitionInDuration = transitionInDuration
        });
        RefreshClipTimeline();
        SelectClip(clipIndex);
        SetCurrentTimelinePosition(position, seekPlayer: true, keepVisible: true);
        LogDebug(_localization.Format("Log.Split.Done", ClipSegment.FormatTime(position)));
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

        LogDebug(RippleDeleteToggle.IsChecked == true
            ? _localization.Format("Log.Delete.Ripple", ClipSegment.FormatTime(removedDuration))
            : _localization.Text("Log.Delete.Selected"));
        UpdateCommandState();
    }

    private void StepFrame(int direction)
    {
        if (_clips.Count == 0 || EditDuration <= TimeSpan.Zero)
        {
            return;
        }

        _isPlaying = false;
        _spacePlaybackStartPosition = null;
        Player.Pause();

        var target = _currentTimelinePosition + TimeSpan.FromTicks(FrameDuration.Ticks * direction);
        SetCurrentTimelinePosition(target, seekPlayer: true, keepVisible: true);
        LogDebug(_localization.Text(direction < 0 ? "Log.Step.Previous" : "Log.Step.Next"));
        UpdateCommandState();
    }

    private void MoveClipToRequestedStart(int clipIndex, TimeSpan requestedStart)
    {
        if (clipIndex < 0 || clipIndex >= _clips.Count)
        {
            return;
        }

        var movingClip = _clips[clipIndex];
        if (clipIndex == 0)
        {
            movingClip.TransitionInDuration = TimeSpan.Zero;
        }
        else
        {
            var previousClip = _clips[clipIndex - 1];
            var requestedOverlap = previousClip.TimelineEnd - requestedStart;
            movingClip.TransitionInDuration = ClampTransitionDuration(previousClip, movingClip, requestedOverlap);
        }

        RefreshClipTimeline();
        SelectClip(clipIndex);
        SetCurrentTimelinePosition(movingClip.TimelineStart, seekPlayer: true, keepVisible: true);
    }

    private void ClearSelectedClipTransitions()
    {
        if (!SelectedClipHasTransitions())
        {
            return;
        }

        _isPlaying = false;
        _spacePlaybackStartPosition = null;
        Player.Pause();

        var selectedClip = _clips[_selectedClipIndex];
        selectedClip.TransitionInDuration = TimeSpan.Zero;

        if (_selectedClipIndex + 1 < _clips.Count)
        {
            _clips[_selectedClipIndex + 1].TransitionInDuration = TimeSpan.Zero;
        }

        RefreshClipTimeline();
        SelectClip(_selectedClipIndex);
        SetCurrentTimelinePosition(selectedClip.TimelineStart, seekPlayer: true, keepVisible: true);
        UpdateCommandState();
    }

    private bool SelectedClipHasTransitions()
    {
        if (_selectedClipIndex < 0 || _selectedClipIndex >= _clips.Count)
        {
            return false;
        }

        if (_clips[_selectedClipIndex].TransitionInDuration > TimeSpan.Zero)
        {
            return true;
        }

        return _selectedClipIndex + 1 < _clips.Count &&
               _clips[_selectedClipIndex + 1].TransitionInDuration > TimeSpan.Zero;
    }

    private void MoveSelectedClipBy(int direction)
    {
        if (_selectedClipIndex < 0 || _selectedClipIndex >= _clips.Count)
        {
            return;
        }

        var targetIndex = _selectedClipIndex + direction;
        if (targetIndex < 0 || targetIndex >= _clips.Count)
        {
            return;
        }

        _isPlaying = false;
        _spacePlaybackStartPosition = null;
        Player.Pause();

        var movingClip = _clips[_selectedClipIndex];
        _clips.RemoveAt(_selectedClipIndex);
        _clips.Insert(targetIndex, movingClip);
        ResetAllTransitions();
        RefreshClipTimeline();
        SelectClip(targetIndex);
        SetCurrentTimelinePosition(movingClip.TimelineStart, seekPlayer: true, keepVisible: true);
        UpdateCommandState();
    }

    private void ResetAllTransitions()
    {
        foreach (var clip in _clips)
        {
            clip.TransitionInDuration = TimeSpan.Zero;
        }
    }

    private async Task RenderAsync(RenderMode mode)
    {
        if (_clips.Count == 0)
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

        if (_clips.Any(clip => string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(clip.SourcePath), StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                this,
                _localization.Text("Message.OutputSameAsInput"),
                "OctoCut",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(LogDebug);
            await _renderer.RenderAsync(
                ffmpegPath,
                _clips.ToList(),
                outputPath,
                mode,
                new RenderProgressText
                {
                    NoClips = _localization.Text("Render.Progress.NoClips"),
                    CreatingSegment = _localization.Text("Render.Progress.CreatingSegment"),
                    MergingSegments = _localization.Text("Render.Progress.MergingSegments"),
                    Complete = _localization.Text("Render.Progress.Complete")
                },
                progress,
                CancellationToken.None);

            LogDebug(_localization.Format("Log.Render.Done", outputPath));
            MessageBox.Show(this, _localization.Text("Message.Render.Complete"), "OctoCut", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogDebug(_localization.Text("Log.Render.Failed"));
            MessageBox.Show(this, ex.Message, _localization.Text("Message.Render.FailTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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

        var missingWindow = new FfmpegMissingWindow(_localization)
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
                _localization.Text("Message.WingetMissing"),
                _localization.Text("Message.WingetMissing.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        LogDebug(_localization.Text("Log.Winget.Installing"));

        try
        {
            await WingetInstaller.InstallFfmpegAsync(CancellationToken.None);
            RefreshFfmpegStatus();

            var resolvedPath = ResolveRequiredFfmpegPath();
            if (resolvedPath is null)
            {
                LogDebug(_localization.Text("Log.Ffmpeg.NotFoundAfterInstall"));
                MessageBox.Show(
                    this,
                    _localization.Text("Message.FfmpegNotFoundAfterInstall"),
                    _localization.Text("Message.FfmpegCheckRequired.Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            LogDebug(_localization.Format("Log.Ffmpeg.Resolved", resolvedPath));
            foreach (var clip in _clips)
            {
                await GenerateTimelinePreviewAsync(clip.SourcePath, clip.SourceDuration);
            }
        }
        catch (Exception ex)
        {
            LogDebug(_localization.Text("Log.Winget.Failed"));
            MessageBox.Show(this, ex.Message, _localization.Text("Message.WingetFailed.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task GenerateTimelinePreviewAsync(string sourcePath, TimeSpan sourceDuration)
    {
        if (sourceDuration <= TimeSpan.Zero)
        {
            return;
        }

        var ffmpegPath = ResolveRequiredFfmpegPath();
        if (ffmpegPath is null)
        {
            Close();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _previewCancellations.Add(cancellation);

        BusyProgress.Visibility = Visibility.Visible;
        LogDebug(_localization.Text("Log.Preview.Generating"));

        try
        {
            var assets = await _previewGenerator.GenerateAsync(ffmpegPath, sourcePath, sourceDuration, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            Timeline.SetPreviewAssets(sourcePath, assets.Thumbnails, assets.Waveform);
            LogDebug(_localization.Text("Log.Preview.Done"));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            // A newer video or settings change started another preview job.
        }
        catch
        {
            Timeline.SetPreviewAssets(sourcePath, EmptyThumbnails, null);
            LogDebug(_localization.Text("Log.Preview.Failed"));
        }
        finally
        {
            _previewCancellations.Remove(cancellation);
            cancellation.Dispose();
            if (!_isBusy && _previewCancellations.Count == 0)
            {
                BusyProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private string? BrowseAndSaveFfmpegPath()
    {
        var dialog = new OpenFileDialog
        {
            Title = _localization.Text("Dialog.FfmpegBrowse.Title"),
            Filter = _localization.Text("Dialog.Exe.Filter"),
            FileName = "ffmpeg.exe"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        if (!FfmpegLocator.IsUsableExecutable(dialog.FileName))
        {
            MessageBox.Show(this, _localization.Text("Message.InvalidExecutable"), "OctoCut", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        if (_clips.Count == 0)
        {
            return null;
        }

        var firstSourcePath = _clips[0].SourcePath;
        var sourceExtension = NormalizeExtension(Path.GetExtension(firstSourcePath), ".mp4");
        var defaultExtension = mode == RenderMode.StreamCopy ? sourceExtension : ".mp4";
        var sourceName = Path.GetFileNameWithoutExtension(firstSourcePath);

        var dialog = new SaveFileDialog
        {
            Title = _localization.Text(mode == RenderMode.StreamCopy ? "Dialog.RenderSave.CopyTitle" : "Dialog.RenderSave.EncodeTitle"),
            InitialDirectory = Path.GetDirectoryName(firstSourcePath),
            FileName = $"{sourceName}_octocut{defaultExtension}",
            DefaultExt = defaultExtension,
            AddExtension = true,
            OverwritePrompt = true,
            Filter = mode == RenderMode.StreamCopy
                ? _localization.Format("Dialog.RenderSave.CopyFilter", sourceExtension.TrimStart('.').ToUpperInvariant(), sourceExtension)
                : _localization.Text("Dialog.RenderSave.EncodeFilter")
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void RefreshClipTimeline()
    {
        var timelineStart = TimeSpan.Zero;
        for (var index = 0; index < _clips.Count; index++)
        {
            var clip = _clips[index];
            clip.Index = index + 1;
            if (index == 0)
            {
                clip.TransitionInDuration = TimeSpan.Zero;
            }
            else
            {
                var previousClip = _clips[index - 1];
                clip.TransitionInDuration = ClampTransitionDuration(previousClip, clip, clip.TransitionInDuration);
                timelineStart -= clip.TransitionInDuration;
            }

            clip.SetTimelineStart(timelineStart);
            timelineStart += clip.Duration;
        }

        UpdateWindowTitle();
        UpdateTimelineExtent();
        Timeline.InvalidateVisual();
    }

    private static TimeSpan ClampTransitionDuration(ClipSegment previousClip, ClipSegment clip, TimeSpan requestedDuration)
    {
        if (requestedDuration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var maxTransition = TimeSpan.FromTicks(Math.Max(
            0,
            Math.Min(previousClip.Duration.Ticks, clip.Duration.Ticks) - MinimumClipDuration.Ticks));

        return requestedDuration > maxTransition ? maxTransition : requestedDuration;
    }

    private void RefreshPositionUi()
    {
        if (_clips.Count == 0)
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
        _spacePlaybackStartPosition = null;
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
            SeekPlayerToCurrentTimelinePosition();
        }

        if (keepVisible)
        {
            EnsureTimelinePositionVisible();
        }

        if (!_isPlaying)
        {
            RequestViewportFrameRefresh();
        }
    }

    private void SeekPlayerToCurrentTimelinePosition()
    {
        if (_clips.Count == 0)
        {
            return;
        }

        var sourceLocation = SourceLocationFromTimeline(_currentTimelinePosition);
        if (!string.Equals(_activeSourcePath, sourceLocation.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            _activeSourcePath = sourceLocation.SourcePath;
            _pendingPlayerPosition = sourceLocation.SourcePosition;
            Player.Source = new Uri(sourceLocation.SourcePath);
            return;
        }

        Player.Position = sourceLocation.SourcePosition;
    }

    private void RequestViewportFrameRefresh()
    {
        if (_isPlaying || _clips.Count == 0)
        {
            return;
        }

        var ffmpegPath = _ffmpegPath ?? ResolveRequiredFfmpegPath();
        if (ffmpegPath is null)
        {
            Close();
            return;
        }

        _framePreviewCancellation?.Cancel();

        var cancellation = new CancellationTokenSource();
        _framePreviewCancellation = cancellation;
        var requestId = ++_framePreviewRequestId;
        var previewSize = GetViewportPreviewSize();

        var transitionPreview = TransitionPreviewCheckBox.IsChecked == true
            ? TransitionPreviewFromTimeline(_currentTimelinePosition)
            : null;
        if (transitionPreview is not null)
        {
            var preview = transitionPreview.Value;
            _ = RenderViewportTransitionFrameAsync(
                requestId,
                ffmpegPath,
                preview.FirstSourcePath,
                preview.FirstSourcePosition,
                preview.SecondSourcePath,
                preview.SecondSourcePosition,
                preview.Amount,
                previewSize.Width,
                previewSize.Height,
                cancellation.Token);
            return;
        }

        var sourceLocation = SourceLocationFromTimeline(_currentTimelinePosition);
        var sourcePosition = ClampSourcePreviewTime(sourceLocation.SourcePath, sourceLocation.SourcePosition);
        _ = RenderViewportFrameAsync(
            requestId,
            ffmpegPath,
            sourceLocation.SourcePath,
            sourcePosition,
            previewSize.Width,
            previewSize.Height,
            cancellation.Token);
    }

    private async Task RenderViewportFrameAsync(
        int requestId,
        string ffmpegPath,
        string videoPath,
        TimeSpan sourcePosition,
        int maxPixelWidth,
        int maxPixelHeight,
        CancellationToken cancellationToken)
    {
        try
        {
            var frame = await _framePreviewRenderer.RenderFrameAsync(
                ffmpegPath,
                videoPath,
                sourcePosition,
                maxPixelWidth,
                maxPixelHeight,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested || requestId != _framePreviewRequestId)
            {
                return;
            }

            if (TransitionPreviewCheckBox.IsChecked == true &&
                TransitionPreviewFromTimeline(_currentTimelinePosition) is not null)
            {
                return;
            }

            FramePreviewImage.Source = frame;
            FramePreviewImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            // A newer seek request superseded this frame.
        }
        catch
        {
            if (requestId == _framePreviewRequestId)
            {
                if (FramePreviewImage.Source is null)
                {
                    FramePreviewImage.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    private async Task RenderViewportTransitionFrameAsync(
        int requestId,
        string ffmpegPath,
        string firstVideoPath,
        TimeSpan firstSourcePosition,
        string secondVideoPath,
        TimeSpan secondSourcePosition,
        double amount,
        int maxPixelWidth,
        int maxPixelHeight,
        CancellationToken cancellationToken)
    {
        try
        {
            var frame = await _framePreviewRenderer.RenderTransitionFrameAsync(
                ffmpegPath,
                firstVideoPath,
                firstSourcePosition,
                secondVideoPath,
                secondSourcePosition,
                amount,
                maxPixelWidth,
                maxPixelHeight,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested || requestId != _framePreviewRequestId)
            {
                return;
            }

            if (TransitionPreviewCheckBox.IsChecked != true ||
                TransitionPreviewFromTimeline(_currentTimelinePosition) is null)
            {
                return;
            }

            FramePreviewImage.Source = frame;
            FramePreviewImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            // A newer seek request superseded this frame.
        }
        catch
        {
            if (requestId == _framePreviewRequestId)
            {
                if (FramePreviewImage.Source is null)
                {
                    FramePreviewImage.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    private void CancelViewportFrameRefresh()
    {
        _framePreviewCancellation?.Cancel();
        _framePreviewRequestId++;
    }

    private (int Width, int Height) GetViewportPreviewSize()
    {
        var width = Player.ActualWidth;
        var height = Player.ActualHeight;

        if (double.IsNaN(width) || width <= 0)
        {
            width = PreviewFrameMaxWidth;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = PreviewFrameMaxHeight;
        }

        var scale = Math.Min(PreviewFrameMaxWidth / width, PreviewFrameMaxHeight / height);
        if (scale < 1)
        {
            width *= scale;
            height *= scale;
        }

        var pixelWidth = Math.Max(2, (int)Math.Round(width));
        var pixelHeight = Math.Max(2, (int)Math.Round(height));
        return (pixelWidth / 2 * 2, pixelHeight / 2 * 2);
    }

    private TimeSpan ClampSourcePreviewTime(string sourcePath, TimeSpan sourcePosition)
    {
        if (sourcePosition < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (!_sourceDurations.TryGetValue(sourcePath, out var sourceDuration) || sourceDuration <= TimeSpan.Zero)
        {
            return sourcePosition;
        }

        var maxPosition = sourceDuration > TimeSpan.FromMilliseconds(1)
            ? sourceDuration - TimeSpan.FromMilliseconds(1)
            : sourceDuration;

        return sourcePosition > maxPosition ? maxPosition : sourcePosition;
    }

    private (string FirstSourcePath, TimeSpan FirstSourcePosition, string SecondSourcePath, TimeSpan SecondSourcePosition, double Amount)?
        TransitionPreviewFromTimeline(TimeSpan timelinePosition)
    {
        for (var index = 1; index < _clips.Count; index++)
        {
            var clip = _clips[index];
            if (clip.TransitionInDuration <= TimeSpan.Zero)
            {
                continue;
            }

            var transitionStart = clip.TimelineStart;
            var transitionEnd = transitionStart + clip.TransitionInDuration;
            if (timelinePosition < transitionStart - TransitionPreviewEdgeTolerance ||
                timelinePosition > transitionEnd + TransitionPreviewEdgeTolerance)
            {
                continue;
            }

            var previousClip = _clips[index - 1];
            var previewPosition = timelinePosition;
            if (previewPosition < transitionStart)
            {
                previewPosition = transitionStart;
            }
            else if (previewPosition > transitionEnd)
            {
                previewPosition = transitionEnd;
            }

            var elapsed = previewPosition - transitionStart;
            var amount = elapsed.TotalSeconds / clip.TransitionInDuration.TotalSeconds;
            return (
                previousClip.SourcePath,
                ClampSourcePreviewTime(previousClip.SourcePath, previousClip.SourceFromTimeline(previewPosition)),
                clip.SourcePath,
                ClampSourcePreviewTime(clip.SourcePath, clip.SourceFromTimeline(previewPosition)),
                Math.Clamp(amount, 0, 1));
        }

        return null;
    }

    private (string SourcePath, TimeSpan SourcePosition) SourceLocationFromTimeline(TimeSpan timelinePosition)
    {
        if (_clips.Count == 0)
        {
            return (string.Empty, TimeSpan.Zero);
        }

        var clipIndex = FindClipIndexAtTimeline(timelinePosition);
        if (clipIndex >= 0)
        {
            var clip = _clips[clipIndex];
            return (clip.SourcePath, clip.SourceFromTimeline(timelinePosition));
        }

        var edgeClip = timelinePosition >= EditDuration ? _clips[^1] : _clips[0];
        return (edgeClip.SourcePath, timelinePosition >= EditDuration ? edgeClip.End : edgeClip.Start);
    }

    private int FindClipIndexAtTimeline(TimeSpan timelinePosition)
    {
        for (var index = _clips.Count - 1; index >= 0; index--)
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
        var totalFrames = FrameCountFromDuration(EditDuration);
        var currentFrame = Math.Min(FrameNumberFromTime(position), totalFrames);
        PositionText.Text = $"{ClipSegment.FormatTime(position)} / {ClipSegment.FormatTime(EditDuration)}   {_localization.Text("Main.Position.FrameLabel")} {currentFrame} / {totalFrames}";
    }

    private static long FrameNumberFromTime(TimeSpan position)
    {
        if (position <= TimeSpan.Zero)
        {
            return 0;
        }

        return (long)Math.Floor(position.TotalSeconds / FrameDuration.TotalSeconds);
    }

    private static long FrameCountFromDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        return (long)Math.Ceiling(duration.TotalSeconds / FrameDuration.TotalSeconds);
    }

    private void UpdateTimelineExtent()
    {
        var viewportWidth = TimelineScroll.ViewportWidth;
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = Math.Max(0, ActualWidth - 48);
        }

        var timelineWidth = Math.Max(viewportWidth, Math.Max(1, EditDuration.TotalSeconds) * _timelinePixelsPerSecond);
        Timeline.Width = timelineWidth;
        Timeline.SetPixelsPerSecond(_timelinePixelsPerSecond);
        Timeline.InvalidateMeasure();
        Timeline.InvalidateVisual();
    }

    private void AdjustTimelineScale(int wheelDelta, TimeSpan anchorPosition)
    {
        if (wheelDelta == 0 || EditDuration <= TimeSpan.Zero)
        {
            return;
        }

        var oldPixelsPerSecond = _timelinePixelsPerSecond;
        var factor = wheelDelta > 0 ? TimelineScaleStep : 1 / TimelineScaleStep;
        var nextPixelsPerSecond = Math.Clamp(
            oldPixelsPerSecond * factor,
            MinimumTimelinePixelsPerSecond,
            MaximumTimelinePixelsPerSecond);

        if (Math.Abs(nextPixelsPerSecond - oldPixelsPerSecond) < 0.001)
        {
            return;
        }

        var anchorSeconds = Math.Clamp(anchorPosition.TotalSeconds, 0, Math.Max(0, EditDuration.TotalSeconds));
        var anchorViewportX = anchorSeconds * oldPixelsPerSecond - TimelineScroll.HorizontalOffset;

        _timelinePixelsPerSecond = nextPixelsPerSecond;
        UpdateTimelineExtent();
        TimelineScroll.UpdateLayout();

        var nextOffset = anchorSeconds * nextPixelsPerSecond - anchorViewportX;
        TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, nextOffset));
    }

    private void ScrollTimelineView(int wheelDelta)
    {
        if (TimelineScroll.ScrollableWidth <= 0)
        {
            return;
        }

        const double wheelScrollPixels = 96;
        var direction = wheelDelta > 0 ? -1 : 1;
        var nextOffset = TimelineScroll.HorizontalOffset + direction * wheelScrollPixels;
        TimelineScroll.ScrollToHorizontalOffset(Math.Clamp(nextOffset, 0, TimelineScroll.ScrollableWidth));
    }

    private void EnsureTimelinePositionVisible()
    {
        if (TimelineScroll.ViewportWidth <= 0)
        {
            return;
        }

        var playheadX = _currentTimelinePosition.TotalSeconds * _timelinePixelsPerSecond;
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
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        BusyProgress.Visibility = isBusy || _previewCancellations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommandState();
    }

    private void UpdateCommandState()
    {
        var hasVideo = _clips.Count > 0 && EditDuration > TimeSpan.Zero;
        var canRender = hasVideo && _clips.Count > 0 && !_isBusy;

        PlayPauseButton.IsEnabled = hasVideo && !_isBusy;
        PlayPauseButton.Content = _localization.Text(_isPlaying ? "Main.Button.Pause" : "Main.Button.Play");
        PlayPauseButton.ToolTip = _localization.Text(_isPlaying ? "Main.Button.Pause.ToolTip" : "Main.Button.Play.ToolTip");
        TransitionPreviewCheckBox.IsEnabled = hasVideo && !_isBusy;
        SplitButton.IsEnabled = hasVideo && !_isBusy;
        RemoveClipButton.IsEnabled = _selectedClipIndex >= 0 && _selectedClipIndex < _clips.Count && !_isBusy;
        MoveClipEarlierButton.IsEnabled = _selectedClipIndex > 0 && !_isBusy;
        MoveClipLaterButton.IsEnabled = _selectedClipIndex >= 0 && _selectedClipIndex < _clips.Count - 1 && !_isBusy;
        ClearClipTransitionButton.Visibility = SelectedClipHasTransitions() ? Visibility.Visible : Visibility.Collapsed;
        ClearClipTransitionButton.IsEnabled = !_isBusy;
        SaveFrameMenuItem.IsEnabled = hasVideo && !_isBusy;
        RenderCopyMenuItem.IsEnabled = canRender && CanUseStreamCopy;
        RenderEncodeMenuItem.IsEnabled = canRender;
    }

    private static string NormalizeExtension(string? extension, string fallback)
    {
        return string.IsNullOrWhiteSpace(extension) ? fallback : extension;
    }
}
