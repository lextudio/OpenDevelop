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
        // Hand-written, but in exactly the shape the designer generates, so the designer treats it
        // as a wired handler once the generated one is cleared (see the template integration test).
        _runButton.OnClicked += (_, args) => Observe(_runButton, args);
    }

    // Not Initialize(): the designer neither shows nor removes a subscription made elsewhere.
    private void SubscribeElsewhere()
    {
        _runButton.OnClicked += (_, args) => Observe(_runButton, args);
    }

    private static void Observe(Gtk.Button sender, System.EventArgs args)
    {
    }
}
