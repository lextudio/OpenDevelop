using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace MewUIFixture.Windows;

/// <summary>
/// User-owned behavior. Layout lives in MainWindow.mxaml; the generated partial (from the
/// .mxaml at build time) provides the fields and InitializeComponent.
/// </summary>
public partial class MainWindow : Window
{
    readonly SettingsWindow settings = new();

    public MainWindow()
    {
        InitializeComponent();
        statusList.ItemsSource = new ItemsView<string>(new[] { "Welcome to QuickNotes", "Designer fixtures rock" });
    }

    private void NewButton_Click()
    {
        nameBox.Text = "new note";
        statusText.Text = "Created";
    }

    private void PreferencesButton_Click()
    {
        settings.Show();
    }

    private void SaveButton_Click()
    {
        statusText.Text = "Saved";
    }
}
