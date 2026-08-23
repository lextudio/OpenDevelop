using Gtk;

namespace GtkFixture;

// A small but genuine GTK 4 application: loads the GtkBuilder document that the designer
// edits and wires the actions a real app would have.
public static class Program
{
    public static int Main(string[] args)
    {
        var app = Application.New("com.example.gtkfixture", Gio.ApplicationFlags.NonUnique);
        app.OnActivate += (_, _) => OnActivate(app);
        return app.Run(args.Length, args);
    }

    static void OnActivate(Application app)
    {
        var builder = Builder.NewFromFile(Path.Combine(AppContext.BaseDirectory, "ui", "mainWindow.ui"));
        var window = (Gtk.ApplicationWindow)builder.GetObject("mainWindow")!;
        app.AddWindow(window);
        window.Present();

        var apply = (Gtk.Button)builder.GetObject("applyButton")!;
        apply.OnClicked += (_, _) => window.SetTitle("Applied");
    }
}
