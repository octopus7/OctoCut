using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OctoCut.Models;

public sealed class ClipSegment : INotifyPropertyChanged
{
    private int _index;

    public ClipSegment(int index, TimeSpan start, TimeSpan end)
    {
        _index = index;
        Start = start;
        End = end;
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

    public string DisplayStart => FormatTime(Start);

    public string DisplayEnd => FormatTime(End);

    public string DisplayDuration => FormatTime(Duration);

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
