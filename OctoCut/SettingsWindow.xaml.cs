using System.IO;
using System.Windows;
using Microsoft.Win32;
using OctoCut.Services;

namespace OctoCut;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        FfmpegPathBox.Text = settings.FfmpegPath ?? string.Empty;
    }

    public string? FfmpegPath { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "ffmpeg.exe 선택",
            Filter = "ffmpeg.exe|ffmpeg.exe|실행 파일|*.exe|모든 파일|*.*",
            FileName = "ffmpeg.exe"
        };

        if (dialog.ShowDialog(this) == true)
        {
            FfmpegPathBox.Text = dialog.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = FfmpegPathBox.Text.Trim();
        if (path.Length > 0 && !File.Exists(path))
        {
            MessageBox.Show(this, "FFmpeg 실행 파일 경로를 확인하세요.", "OctoCut", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FfmpegPath = path.Length == 0 ? null : path;
        DialogResult = true;
    }
}
