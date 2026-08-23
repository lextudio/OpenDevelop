using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace MewUIFixture.Windows;

/// <summary>
/// A second, independently constructible and designable window.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        generalGroup.Header = new Label { Text = "General" };
    }

    private void SaveButton_Click()
    {
        Title = "Saved";
    }
}
