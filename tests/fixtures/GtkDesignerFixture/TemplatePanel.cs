using Gtk;

namespace GtkDesignerFixture;

/// <summary>A Gir.Core composite template: the .ui is an embedded resource and the class wires
/// its children with [Gtk.Connect]. GTK's template builder connects &lt;signal&gt; entries as native
/// symbols, which aborts the process, so the click handler is an event subscription in
/// Initialize() - the pattern the GTK designer generates for template documents.</summary>
[GObject.Subclass<Gtk.Window>(qualifiedName: nameof(TemplatePanel))]
[Gtk.Template<Gtk.AssemblyResource>("GtkDesignerFixture.Windows.TemplatePanel.ui")]
public partial class TemplatePanel
{
    [Gtk.Connect("runButton")]
    private Gtk.Button _runButton;

    /// <summary>The template child, exposed so the smoke test can prove the template built.</summary>
    public Gtk.Button RunButton => _runButton;

    partial void Initialize()
    {
        _runButton.Label = "Wired";
        _runButton.OnClicked += (_, _) => _runButton.Label = "Running";
    }
}
