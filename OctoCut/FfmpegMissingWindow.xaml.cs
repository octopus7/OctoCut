using System.Windows;
using OctoCut.Services;

namespace OctoCut;

public enum FfmpegMissingAction
{
    None,
    InstallWithWinget,
    OpenDownload,
    Browse
}

public partial class FfmpegMissingWindow : Window
{
    private readonly LocalizationManager _localization;

    public FfmpegMissingWindow(LocalizationManager localization)
    {
        _localization = localization;
        InitializeComponent();
        ApplyLocalization();

        if (!WingetInstaller.IsAvailable())
        {
            WingetInstallButton.IsEnabled = false;
            WingetInstallButton.ToolTip = _localization.Text("FfmpegMissing.WingetUnavailable.ToolTip");
        }
    }

    public FfmpegMissingAction SelectedAction { get; private set; }

    private void ApplyLocalization()
    {
        Title = _localization.Text("FfmpegMissing.Title");
        DescriptionText.Text = _localization.Text("FfmpegMissing.Description");
        WingetInstallButton.Content = _localization.Text("FfmpegMissing.WingetInstall");
        DownloadButton.Content = _localization.Text("FfmpegMissing.Download");
        BrowseButton.Content = _localization.Text("FfmpegMissing.Browse");
        CancelButton.Content = _localization.Text("Common.Cancel");
    }

    private void WingetInstall_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = FfmpegMissingAction.InstallWithWinget;
        DialogResult = true;
    }

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
