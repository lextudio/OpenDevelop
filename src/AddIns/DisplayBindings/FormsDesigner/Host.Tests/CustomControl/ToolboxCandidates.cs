using System;
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

/// <summary>A non-visual project component: it belongs in the Toolbox and then the component tray.</summary>
public class ProjectComponent : Component
{
}

/// <summary>Visible only as part of its owner, never as a standalone tray component.</summary>
[DesignTimeVisible(false)]
public class HiddenProjectComponent : Component
{
}

/// <summary>A valid component need not inherit Component; IComponent is the designer contract.</summary>
public class DirectProjectComponent : IComponent
{
	public ISite Site { get; set; }
	public event EventHandler Disposed;

	public void Dispose() => Disposed?.Invoke(this, EventArgs.Empty);
}

/// <summary>A project component with a converter-backed structured scalar property.</summary>
public class DateTimeProjectComponent : Component
{
	public DateTime OccurredAt { get; set; }
}
