using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OctoCut.Models;

public sealed class ClipSegment : INotifyPropertyChanged
{
    private int _index;
    private TimeSpan _timelineStart;
    private TimeSpan _timelineEnd;

    public ClipSegment(int index, TimeSpan start, TimeSpan end)
    {
        _index = index;
        Start = start;
        End = end;
        _timelineStart = start;
        _timelineEnd = end;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index
    {
        get => _index;
        set
        {
            if (_index == value)
            {
                return;
            }

            _index = value;
            OnPropertyChanged();
        }
    }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public TimeSpan Duration => End - Start;

    public TimeSpan TimelineStart => _timelineStart;

    public TimeSpan TimelineEnd => _timelineEnd;

    public string DisplayStart => FormatTime(TimelineStart);

    public string DisplayEnd => FormatTime(TimelineEnd);

    public string DisplayDuration => FormatTime(Duration);

    public void SetTimelineStart(TimeSpan timelineStart)
    {
        var timelineEnd = timelineStart + Duration;
        if (_timelineStart == timelineStart && _timelineEnd == timelineEnd)
        {
            return;
        }

        _timelineStart = timelineStart;
        _timelineEnd = timelineEnd;
        OnPropertyChanged(nameof(TimelineStart));
        OnPropertyChanged(nameof(TimelineEnd));
        OnPropertyChanged(nameof(DisplayStart));
        OnPropertyChanged(nameof(DisplayEnd));
    }

    public void ResetTimelineToSource()
    {
        if (_timelineStart == Start && _timelineEnd == End)
        {
            return;
        }

        _timelineStart = Start;
        _timelineEnd = End;
        OnPropertyChanged(nameof(TimelineStart));
        OnPropertyChanged(nameof(TimelineEnd));
        OnPropertyChanged(nameof(DisplayStart));
        OnPropertyChanged(nameof(DisplayEnd));
    }

    public bool Contains(TimeSpan position)
    {
        return position > Start && position < End;
    }

    public static string FormatTime(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss\.fff")
            : value.ToString(@"mm\:ss\.fff");
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
