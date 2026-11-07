# GTK 4 designer fixture

`Windows/MainWindow.ui` is a standard GTK 4 GtkBuilder document and the source authority used by
the OpenDevelop GTK designer integration tests. It is also a real GirCore GTK 4 application:

`ui/mainWindow.ui` is the nested-selection document used by `GtkDesignerIntegrationTests` (two
top-level objects plus an id-referencing property); it lives in the same project so the GTK tests
share one fixture instead of one project per journey.

```bash
dotnet run --project tests/fixtures/GtkDesignerFixture/GtkDesignerFixture.csproj
```

For a non-interactive runtime check (including loading the GtkBuilder document and resolving its
named objects), run:

```bash
dotnet run --project tests/fixtures/GtkDesignerFixture/GtkDesignerFixture.csproj -- --smoke-test
```

GirCore supplies the managed bindings, but GTK 4 itself must be installed on the machine. On macOS
use `brew install gtk4`; on Linux install the distribution's GTK 4 runtime/development package.
The launch profile includes the standard Apple Silicon and Intel Homebrew library directories so
plain `dotnet run` can resolve GirCore's native GTK dependency on macOS.
Clicking **Run** changes its label to **Running**, which provides a minimal runtime smoke test that
the GtkBuilder object lookup and signal wiring both work.

`Windows/TemplatePanel.ui` is a Gir.Core composite template (an embedded resource) and
`TemplatePanel.cs` is its `[Template]` class. It uses `[Gtk.Connect]` plus an event subscription in
`Initialize()` rather than a `<signal>` entry, because GTK's template builder resolves handler names
as native symbols and aborts the process. The smoke test instantiates it and requires the connected
button to have been initialized; the designer's template-signal journey wires that button and then
builds this fixture to prove the generated code compiles.
