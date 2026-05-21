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
    public FfmpegMissingWindow()
    {
        InitializeComponent();

        if (!WingetInstaller.IsAvailable())
        {
            WingetInstallButton.IsEnabled = false;
            WingetInstallButton.ToolTip = "이 PC에서 winget.exe를 찾을 수 없습니다. Microsoft App Installer가 필요합니다.";
        }
    }

    public FfmpegMissingAction SelectedAction { get; private set; }

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
