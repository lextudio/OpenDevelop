Gtk.Module.Initialize();

var smokeTest = args.Contains("--smoke-test", StringComparer.Ordinal);

var application = Gtk.Application.New(
    "com.lextudio.opendevelop.gtkdesignerfixture",
    Gio.ApplicationFlags.FlagsNone);

application.OnActivate += (sender, _) =>
{
    var uiPath = Path.Combine(AppContext.BaseDirectory, "Windows", "MainWindow.ui");
    // Not Gtk.Builder.NewFromFile: GtkBuilder aborts the process on a <signal> it cannot resolve
    // to a native symbol. The designer-generated BuildUi (Windows/MainWindow.ui.cs) builds the UI
    // and connects its signals to MainWindow's handler methods instead.
    var builder = new GtkDesignerFixture.MainWindow().BuildUi(uiPath);
    var window = (Gtk.ApplicationWindow?)builder.GetObject("mainWindow")
        ?? throw new InvalidOperationException("MainWindow.ui does not define 'mainWindow'.");
    var runButton = (Gtk.Button?)builder.GetObject("runButton")
        ?? throw new InvalidOperationException("MainWindow.ui does not define 'runButton'.");

    window.Application = (Gtk.Application)sender;
    runButton.OnClicked += (_, _) => runButton.Label = "Running";
    if (smokeTest)
    {
        Console.WriteLine("GTK fixture smoke test passed: MainWindow.ui loaded and signals wired.");
        application.Quit();
        return;
    }
    window.Show();
};

return application.RunWithSynchronizationContext(null);
