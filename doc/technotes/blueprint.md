# Blueprint (.blp)

[Blueprint](https://gnome.pages.gitlab.gnome.org/blueprint-compiler/) is GNOME's markup language for GTK interfaces; its
compiler turns a `.blp` into the GtkBuilder `.ui` that GTK, and the GTK designer, read. The `ICSharpCode.BlueprintBinding`
addin (`src/AddIns/BackendBindings/BlueprintBinding`) gives `.blp` files editor support by driving the real
`blueprint-compiler`; it does not reimplement the language.

## What it gives

| Feature | How |
|---|---|
| Syntax highlighting | `Resources/Blueprint.xshd` (always on, with or without the compiler) |
| Diagnostics, completion, hover, formatting, document symbols (outline), go to definition, semantic tokens | `blueprint-compiler lsp`, the compiler's own language server, started through the shared LSP client and registered for ".blp" on the language-service registry |
| Tools > **Compile Blueprint** | `blueprint-compiler compile --output X.ui X.blp` for the active `.blp` (saved first); problems are listed in a message |
| Tools > **Convert .ui to Blueprint** | `blueprint-compiler decompile X.ui` written to `X.blp` for the active `.ui`; an existing `X.blp` is never overwritten |
| DevFlow `od.blueprint.status`, `od.blueprint.compile`, `od.blueprint.decompile` | the same operations for tests; compile returns its diagnostics with line, column, span and hints |

## The compiler is an external tool

`blueprint-compiler` is Python (PyGObject plus the GIR typelibs), so the IDE does not ship it. `BlueprintCompilerLocator`
looks for it in `OD_BLUEPRINT_COMPILER` (a full path), then on the `PATH`, then in the folders a GUI process may not have on
its PATH (`/opt/homebrew/bin`, `/home/linuxbrew/.linuxbrew/bin`, `/usr/local/bin`, `/usr/bin`, `~/.local/bin`). With none
found the language server is **not registered** (a log line says why) and `.blp` opens as highlighted text; the Tools
commands tell the user how to install it (`brew install blueprint-compiler`).

## Rules learned from the real compiler (0.22.2)

- It colours its messages even when piped, and `NO_COLOR` is not honoured: output is stripped of ANSI codes before it is parsed.
- A problem is `error: MESSAGE` / `warning: MESSAGE`, then `at FILE line L column C:`, the source line and a `^^^` underline (its width is the
  span), then `hint:` and `suggestion:` lines. `N errors` is a summary, not a problem.
- `compile --output` writes the file only on success. `decompile` prints Blueprint on stdout and, on a file that is not XML,
  **crashes with a Python traceback** and "COMPILER BUG": the wrapper reports one line (the exception) instead of the traceback.
- The compiler converts enum names to numbers in the `.ui` (`orientation: vertical` becomes `1`); that is its output, not the designer's.

## Relation to the GTK designer

The `.ui` a `.blp` produces is a generated file ("DO NOT EDIT"): edit the `.blp` and compile again. The GTK designer opens a `.ui`
and would write it back, so it is not wired to a generated `.ui`; designing visually and converting the result to Blueprint (the Convert
command) is the supported direction.

## Shared infrastructure fix

Wiring this exposed a bug in the shared LSP client (`LspLanguageService`): its `publishDiagnostics` handler was never invoked, so
`GetDiagnosticsAsync` returned nothing for every LSP-served language (see commit "Bind LSP push notifications with single-object
parameter deserialization"). The Blueprint journey is the regression test for it.

## Tests

`src/AddIns/BackendBindings/BlueprintBinding.Tests` (locator, diagnostic parser, and the real compiler for compile, failure,
decompile and a crashing decompile; the compiler tests skip when it is not installed) and
`GtkDesigner_BlueprintFiles_CompileDecompileAndGetTheCompilersLanguageServer` (compile, decompile, and the language server's
diagnostics and outline through the generic language-service actions; skipped without the compiler; it lives in the GTK designer's
journey class because that class owns the fixture project).
