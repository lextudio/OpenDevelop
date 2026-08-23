# GTK 4 designer fixture

`Windows/MainWindow.ui` is a standard GTK 4 GtkBuilder document and the source authority used by
the OpenDevelop GTK designer integration tests. It is also a real GirCore GTK 4 application:

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
