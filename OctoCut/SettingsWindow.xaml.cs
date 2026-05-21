using System.IO;
using System.Windows;
using Microsoft.Win32;
using OctoCut.Services;

namespace OctoCut;

public partial class SettingsWindow : Window
{
    private readonly LocalizationManager _localization;

    public SettingsWindow(AppSettings settings, LocalizationManager localization)
    {
        _localization = localization;
        InitializeComponent();

        ApplyLocalization();
        LanguageBox.ItemsSource = _localization.AvailableLanguages;
        LanguageBox.SelectedItem = _localization.AvailableLanguages.FirstOrDefault(language =>
            string.Equals(language.Code, settings.LanguageCode, StringComparison.OrdinalIgnoreCase)) ??
            _localization.AvailableLanguages.FirstOrDefault(language =>
                string.Equals(language.Code, _localization.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase));

        FfmpegPathBox.Text = settings.FfmpegPath ?? string.Empty;
    }

    public string? FfmpegPath { get; private set; }

    public string? LanguageCode { get; private set; }

    private void ApplyLocalization()
    {
        Title = _localization.Text("Settings.Title");
        LanguageLabel.Text = _localization.Text("Settings.Language");
        FfmpegLabel.Text = _localization.Text("Settings.FfmpegPath");
        BrowseButton.Content = _localization.Text("Common.Browse");
        FfmpegHintText.Text = _localization.Text("Settings.FfmpegHint");
        SaveButton.Content = _localization.Text("Common.Save");
        CancelButton.Content = _localization.Text("Common.Cancel");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _localization.Text("Dialog.FfmpegBrowse.Title"),
            Filter = _localization.Text("Dialog.Exe.Filter"),
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
            MessageBox.Show(this, _localization.Text("Settings.InvalidFfmpegPath"), "OctoCut", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FfmpegPath = path.Length == 0 ? null : path;
        LanguageCode = (LanguageBox.SelectedItem as LanguageInfo)?.Code ?? _localization.CurrentLanguageCode;
        DialogResult = true;
    }
}
