using System.Windows;

namespace OctoCut;

public enum FfmpegMissingAction
{
    None,
    OpenDownload,
    Browse
}

public partial class FfmpegMissingWindow : Window
{
    public FfmpegMissingWindow()
    {
        InitializeComponent();
    }

    public FfmpegMissingAction SelectedAction { get; private set; }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = FfmpegMissingAction.OpenDownload;
        DialogResult = true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = FfmpegMissingAction.Browse;
        DialogResult = true;
    }
}
