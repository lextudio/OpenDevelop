namespace GtkDesignerFixture;

/// <summary>An application-defined Gir.Core widget. The designer's custom-widget execution test declares
/// this project's output in .gtkdesigner.json and expects the design host to build it as itself.</summary>
[GObject.Subclass<Gtk.Label>(qualifiedName: "FixtureStars")]
public partial class FixtureStars
{
    partial void Initialize()
    {
        SetLabel("***");
    }
}
