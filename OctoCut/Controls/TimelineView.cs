using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OctoCut.Models;

namespace OctoCut.Controls;

public sealed class TimelineSeekEventArgs(TimeSpan position) : EventArgs
{
    public TimeSpan Position { get; } = position;
}

public sealed class TimelineClipEventArgs(int clipIndex) : EventArgs
{
    public int ClipIndex { get; } = clipIndex;
}

public sealed class TimelineThumbnail(TimeSpan sourceTime, BitmapSource image)
{
    public TimeSpan SourceTime { get; } = sourceTime;

    public BitmapSource Image { get; } = image;
}

public sealed class TimelineView : FrameworkElement
{
    private const double RulerHeight = 30;
    private const double ThumbnailHeight = 78;
    private const double WaveformHeight = 54;
    private const double TrackGap = 5;
    private const double MinimumClipWidth = 8;

    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255));
    private static readonly Brush RulerBrush = new SolidColorBrush(Color.FromRgb(246, 248, 250));
    private static readonly Brush ClipBrush = new SolidColorBrush(Color.FromRgb(235, 240, 246));
    private static readonly Brush SelectedClipBrush = new SolidColorBrush(Color.FromRgb(218, 235, 255));
    private static readonly Brush ClipBorderBrush = new SolidColorBrush(Color.FromRgb(111, 133, 155));
    private static readonly Brush WaveformBrush = new SolidColorBrush(Color.FromRgb(237, 243, 249));
    private static readonly Brush EmptyPreviewBrush = new SolidColorBrush(Color.FromRgb(241, 244, 247));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(36, 41, 47));
    private static readonly Brush MutedTextBrush = new SolidColorBrush(Color.FromRgb(87, 96, 106));
    private static readonly Brush PlayheadBrush = Brushes.Black;

    private readonly Typeface _labelTypeface = new("Segoe UI");

    private IReadOnlyList<TimelineThumbnail> _thumbnails = Array.Empty<TimelineThumbnail>();
    private BitmapSource? _waveformImage;
    private double _pixelsPerSecond = 48;

    public TimelineView()
    {
        Clips = new ObservableCollection<ClipSegment>();
        Focusable = true;
        Cursor = Cursors.Hand;
    }

    public event EventHandler<TimelineSeekEventArgs>? PositionRequested;

    public event EventHandler<TimelineClipEventArgs>? ClipSelected;

    public ObservableCollection<ClipSegment> Clips { get; set; }

    public TimeSpan CurrentPosition { get; private set; }

    public TimeSpan SourceDuration { get; private set; }

    public int SelectedClipIndex { get; private set; } = -1;

    public TimeSpan EditDuration
    {
        get
        {
            if (Clips.Count == 0)
            {
                return TimeSpan.Zero;
            }

            return Clips[^1].TimelineEnd;
        }
    }

    public void SetClips(ObservableCollection<ClipSegment> clips)
    {
        Clips = clips;
        InvalidateVisual();
    }

    public void SetSourceDuration(TimeSpan duration)
    {
        SourceDuration = duration;
        InvalidateVisual();
    }

    public void SetPixelsPerSecond(double pixelsPerSecond)
    {
        _pixelsPerSecond = Math.Clamp(pixelsPerSecond, 12, 120);
        InvalidateVisual();
    }

    public void SetCurrentPosition(TimeSpan position)
    {
        CurrentPosition = ClampToEditDuration(position);
        InvalidateVisual();
    }

    public void SetSelectedClipIndex(int selectedClipIndex)
    {
        SelectedClipIndex = selectedClipIndex;
        InvalidateVisual();
    }

    public void SetPreviewAssets(IReadOnlyList<TimelineThumbnail> thumbnails, BitmapSource? waveformImage)
    {
        _thumbnails = thumbnails;
        _waveformImage = waveformImage;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsNaN(Width) ? availableSize.Width : Width;
        var height = double.IsNaN(Height) ? 170 : Height;

        if (double.IsInfinity(width))
        {
            width = MinWidth;
        }

        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = Math.Max(ActualWidth, 1);
        var height = Math.Max(ActualHeight, RulerHeight + ThumbnailHeight + WaveformHeight + TrackGap);
        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        DrawRuler(drawingContext, width);
        DrawThumbnailTrack(drawingContext, width);
        DrawWaveformTrack(drawingContext, width);
        DrawPlayhead(drawingContext, height);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (Clips.Count == 0)
        {
            return;
        }

        var point = e.GetPosition(this);
        var position = PositionFromX(point.X);
        SelectedClipIndex = FindClipIndexAtTimeline(position);
        PositionRequested?.Invoke(this, new TimelineSeekEventArgs(position));

        if (SelectedClipIndex >= 0)
        {
            ClipSelected?.Invoke(this, new TimelineClipEventArgs(SelectedClipIndex));
        }

        InvalidateVisual();
        e.Handled = true;
    }

    private void DrawRuler(DrawingContext drawingContext, double width)
    {
        drawingContext.DrawRectangle(RulerBrush, null, new Rect(0, 0, width, RulerHeight));

        var duration = EditDuration;
        if (duration <= TimeSpan.Zero)
        {
            DrawText(drawingContext, "영상을 열면 편집 타임라인이 표시됩니다.", 10, 8, MutedTextBrush, 12);
            return;
        }

        var tickStep = ChooseTickStep();
        for (var seconds = 0.0; seconds <= duration.TotalSeconds + 0.001; seconds += tickStep)
        {
            var x = seconds * _pixelsPerSecond;
            var isMajor = Math.Abs(seconds % (tickStep * 2)) < 0.001;
            var tickHeight = isMajor ? 13 : 8;
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(139, 148, 158)), 1);
            drawingContext.DrawLine(pen, new Point(x + 0.5, RulerHeight - tickHeight), new Point(x + 0.5, RulerHeight));

            if (isMajor)
            {
                DrawText(drawingContext, ClipSegment.FormatTime(TimeSpan.FromSeconds(seconds)), x + 4, 5, MutedTextBrush, 10);
            }
        }

        drawingContext.DrawLine(
            new Pen(new SolidColorBrush(Color.FromRgb(208, 215, 222)), 1),
            new Point(0, RulerHeight - 0.5),
            new Point(width, RulerHeight - 0.5));
    }

    private void DrawThumbnailTrack(DrawingContext drawingContext, double width)
    {
        var top = RulerHeight;
        drawingContext.DrawRectangle(EmptyPreviewBrush, null, new Rect(0, top, width, ThumbnailHeight));

        for (var index = 0; index < Clips.Count; index++)
        {
            var clip = Clips[index];
            var clipRect = ClipRect(clip, top, ThumbnailHeight);
            if (clipRect.Width < MinimumClipWidth)
            {
                continue;
            }

            var fill = index == SelectedClipIndex ? SelectedClipBrush : ClipBrush;
            drawingContext.DrawRectangle(fill, new Pen(ClipBorderBrush, 1), clipRect);
            DrawClipThumbnails(drawingContext, clip, clipRect);
            DrawText(drawingContext, $"#{clip.Index}", clipRect.X + 6, clipRect.Y + 5, TextBrush, 11);
        }
    }

    private void DrawWaveformTrack(DrawingContext drawingContext, double width)
    {
        var top = RulerHeight + ThumbnailHeight + TrackGap;
        drawingContext.DrawRectangle(WaveformBrush, null, new Rect(0, top, width, WaveformHeight));

        for (var index = 0; index < Clips.Count; index++)
        {
            var clip = Clips[index];
            var clipRect = ClipRect(clip, top, WaveformHeight);
            if (clipRect.Width < MinimumClipWidth)
            {
                continue;
            }

            drawingContext.DrawRectangle(index == SelectedClipIndex ? SelectedClipBrush : Brushes.Transparent, new Pen(ClipBorderBrush, 1), clipRect);
            DrawWaveform(drawingContext, clip, clipRect);
        }
    }

    private void DrawClipThumbnails(DrawingContext drawingContext, ClipSegment clip, Rect clipRect)
    {
        const double tileWidth = 96;
        var tileCount = Math.Max(1, (int)Math.Ceiling(clipRect.Width / tileWidth));

        for (var tile = 0; tile < tileCount; tile++)
        {
            var tileX = clipRect.X + tile * tileWidth;
            var tileRect = new Rect(tileX, clipRect.Y + 18, Math.Min(tileWidth, clipRect.Right - tileX), clipRect.Height - 22);
            if (tileRect.Width <= 1)
            {
                continue;
            }

            var ratio = tileCount == 1 ? 0 : (double)tile / Math.Max(1, tileCount - 1);
            var sourceTime = clip.Start + TimeSpan.FromTicks((long)(clip.Duration.Ticks * ratio));
            var thumbnail = FindNearestThumbnail(sourceTime);
            if (thumbnail is not null)
            {
                drawingContext.DrawImage(thumbnail.Image, tileRect);
            }
            else
            {
                drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 229, 235)), null, tileRect);
                DrawText(drawingContext, "thumbnail", tileRect.X + 8, tileRect.Y + 18, MutedTextBrush, 10);
            }
        }
    }

    private void DrawWaveform(DrawingContext drawingContext, ClipSegment clip, Rect clipRect)
    {
        if (_waveformImage is null || SourceDuration <= TimeSpan.Zero)
        {
            DrawText(drawingContext, "audio waveform", clipRect.X + 8, clipRect.Y + 18, MutedTextBrush, 10);
            return;
        }

        var sourceX = Math.Clamp(clip.Start.TotalSeconds / SourceDuration.TotalSeconds * _waveformImage.PixelWidth, 0, _waveformImage.PixelWidth - 1);
        var sourceWidth = Math.Clamp(clip.Duration.TotalSeconds / SourceDuration.TotalSeconds * _waveformImage.PixelWidth, 1, _waveformImage.PixelWidth - sourceX);
        var crop = new CroppedBitmap(
            _waveformImage,
            new Int32Rect((int)sourceX, 0, Math.Max(1, (int)sourceWidth), _waveformImage.PixelHeight));

        drawingContext.DrawImage(crop, clipRect);
    }

    private void DrawPlayhead(DrawingContext drawingContext, double height)
    {
        if (EditDuration <= TimeSpan.Zero)
        {
            return;
        }

        var x = XFromPosition(CurrentPosition);
        drawingContext.DrawLine(new Pen(PlayheadBrush, 1), new Point(x + 0.5, 0), new Point(x + 0.5, height));
    }

    private Rect ClipRect(ClipSegment clip, double y, double height)
    {
        var x = XFromPosition(clip.TimelineStart);
        var width = Math.Max(MinimumClipWidth, clip.Duration.TotalSeconds * _pixelsPerSecond);
        return new Rect(x, y, width, height);
    }

    private TimelineThumbnail? FindNearestThumbnail(TimeSpan sourceTime)
    {
        if (_thumbnails.Count == 0)
        {
            return null;
        }

        var nearest = _thumbnails[0];
        var nearestDistance = (nearest.SourceTime - sourceTime).Duration();

        foreach (var thumbnail in _thumbnails)
        {
            var distance = (thumbnail.SourceTime - sourceTime).Duration();
            if (distance < nearestDistance)
            {
                nearest = thumbnail;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private int FindClipIndexAtTimeline(TimeSpan position)
    {
        for (var index = 0; index < Clips.Count; index++)
        {
            var clip = Clips[index];
            if (position >= clip.TimelineStart && position <= clip.TimelineEnd)
            {
                return index;
            }
        }

        return -1;
    }

    private double XFromPosition(TimeSpan position)
    {
        return Math.Clamp(position.TotalSeconds, 0, Math.Max(0, EditDuration.TotalSeconds)) * _pixelsPerSecond;
    }

    private TimeSpan PositionFromX(double x)
    {
        return ClampToEditDuration(TimeSpan.FromSeconds(Math.Max(0, x / _pixelsPerSecond)));
    }

    private TimeSpan ClampToEditDuration(TimeSpan position)
    {
        var duration = EditDuration;
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return position > duration ? duration : position;
    }

    private double ChooseTickStep()
    {
        var desiredPixels = 90;
        var seconds = desiredPixels / _pixelsPerSecond;

        if (seconds <= 0.5)
        {
            return 0.5;
        }

        if (seconds <= 1)
        {
            return 1;
        }

        if (seconds <= 2)
        {
            return 2;
        }

        if (seconds <= 5)
        {
            return 5;
        }

        if (seconds <= 10)
        {
            return 10;
        }

        return 30;
    }

    private void DrawText(DrawingContext drawingContext, string text, double x, double y, Brush brush, double size)
    {
        var formattedText = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            _labelTypeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        drawingContext.DrawText(formattedText, new Point(x, y));
    }
}
