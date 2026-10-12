using System.ComponentModel;
using System.Windows.Forms;

namespace FormsDesigner.CustomControlFixture;

/// <summary>A project UserControl: the WinForms Toolbox lists it.</summary>
public class GreetingPanel : UserControl
{
}

/// <summary>Opted out of the Toolbox.</summary>
[ToolboxItem(false)]
public class HiddenPanel : Panel
{
}

/// <summary>A top-level window: a Control, but never dropped onto another form.</summary>
public class SettingsForm : Form
{
}
