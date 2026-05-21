using System.Windows;
using OctoCut.Services;

namespace OctoCut;

public partial class DebugLogWindow : Window
{
    private readonly LocalizationManager _localization;

    public DebugLogWindow(LocalizationManager localization)
    {
        _localization = localization;
        InitializeComponent();
        ApplyLocalization();
    }

    public void ApplyLocalization()
    {
        Title = _localization.Text("DebugLog.Title");
    }

    public void SetLogText(string text)
    {
        LogTextBox.Text = text;
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
        LogTextBox.ScrollToEnd();
    }

    public void AppendLog(string line)
    {
        if (LogTextBox.Text.Length > 0)
        {
            LogTextBox.AppendText(Environment.NewLine);
        }

        LogTextBox.AppendText(line);
        LogTextBox.ScrollToEnd();
    }
}
