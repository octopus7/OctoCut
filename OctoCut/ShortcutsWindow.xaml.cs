using System.Windows;
using OctoCut.Services;

namespace OctoCut;

public partial class ShortcutsWindow : Window
{
    private readonly LocalizationManager _localization;

    public ShortcutsWindow(LocalizationManager localization)
    {
        _localization = localization;
        InitializeComponent();
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        Title = _localization.Text("Shortcuts.Title");
        EnterActionText.Text = _localization.Text("Shortcuts.Enter");
        SpaceActionText.Text = _localization.Text("Shortcuts.Space");
        SplitActionText.Text = _localization.Text("Shortcuts.Split");
        DeleteActionText.Text = _localization.Text("Shortcuts.Delete");
        FrameStepActionText.Text = _localization.Text("Shortcuts.FrameStep");
        CaptureActionText.Text = _localization.Text("Shortcuts.Capture");
        CloseButton.Content = _localization.Text("Common.Close");
    }
}
