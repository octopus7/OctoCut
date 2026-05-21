using OctoCut.Controls;
using System.Windows.Media.Imaging;

namespace OctoCut.Services;

public sealed class TimelinePreviewAssets(
    IReadOnlyList<TimelineThumbnail> thumbnails,
    BitmapSource? waveform)
{
    public IReadOnlyList<TimelineThumbnail> Thumbnails { get; } = thumbnails;

    public BitmapSource? Waveform { get; } = waveform;
}
