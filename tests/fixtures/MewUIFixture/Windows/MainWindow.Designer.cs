using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace MewUIFixture.Windows;

/// <summary>
/// Designer-owned layout for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow
{
    private StackPanel rootPanel = null!;
    private Label heading = null!;
    private StackPanel toolRow = null!;
    private Button newButton = null!;
    private Button preferencesButton = null!;
    private Button saveButton = null!;
    private TextBox nameBox = null!;
    private CheckBox notificationsCheck = null!;
    private ListBox statusList = null!;
    private StackPanel statusBar = null!;
    private Label statusText = null!;

    private void InitializeComponent()
    {
        rootPanel = new StackPanel();
        heading = new Label();
        toolRow = new StackPanel();
        newButton = new Button();
        preferencesButton = new Button();
        saveButton = new Button();
        nameBox = new TextBox();
        notificationsCheck = new CheckBox();
        statusList = new ListBox();
        statusBar = new StackPanel();
        statusText = new Label();

        Title = "QuickNotes";
        WindowSize = WindowSize.Resizable(900, 700);
        heading.Text = "QuickNotes";
        rootPanel.Spacing = 8;
        toolRow.Spacing = 6;
        toolRow.Orientation = Orientation.Horizontal;
        newButton.Content = "New";
        preferencesButton.Content = "Settings";
        saveButton.Content = "Save";
        notificationsCheck.Text = "Enable notifications";
        nameBox.Text = "untitled";
        statusText.Text = "Ready";

        rootPanel.Children(heading, toolRow, nameBox, notificationsCheck, statusList, statusBar);
        toolRow.Children(newButton, preferencesButton, saveButton);
        statusBar.Children(statusText);
        Content = rootPanel;
    }
}
