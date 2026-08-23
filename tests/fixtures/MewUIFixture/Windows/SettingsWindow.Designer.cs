using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace MewUIFixture.Windows;

/// <summary>
/// Designer-owned layout for <see cref="SettingsWindow"/>.
/// </summary>
public partial class SettingsWindow
{
    private StackPanel prefsRoot = null!;
    private GroupBox generalGroup = null!;
    private StackPanel generalStack = null!;
    private Label nameLabel = null!;
    private TextBox nameBox = null!;
    private CheckBox notifyCheck = null!;
    private StackPanel themeRow = null!;
    private Label themeLabel = null!;
    private ComboBox themeCombo = null!;
    private Button saveButton = null!;

    private void InitializeComponent()
    {
        prefsRoot = new StackPanel();
        generalGroup = new GroupBox();
        generalStack = new StackPanel();
        nameLabel = new Label();
        nameBox = new TextBox();
        notifyCheck = new CheckBox();
        themeRow = new StackPanel();
        themeLabel = new Label();
        themeCombo = new ComboBox();
        saveButton = new Button();

        Title = "Settings";
        WindowSize = WindowSize.Resizable(520, 460);
        prefsRoot.Spacing = 10;
        nameLabel.Text = "User name";
        nameBox.Text = "designer";
        notifyCheck.Text = "Show notifications";
        themeLabel.Text = "Theme";
        saveButton.Content = "Save";
        saveButton.Click += SaveButton_Click;

        generalStack.Children(nameLabel, nameBox);
        generalGroup.Children(generalStack);
        themeRow.Children(themeLabel, themeCombo);
        prefsRoot.Children(generalGroup, notifyCheck, themeRow, saveButton);
        Content = prefsRoot;
    }
}
