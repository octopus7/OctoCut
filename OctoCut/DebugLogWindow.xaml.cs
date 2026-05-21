using System.Windows;

namespace OctoCut;

public partial class DebugLogWindow : Window
{
    public DebugLogWindow()
    {
        InitializeComponent();
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
