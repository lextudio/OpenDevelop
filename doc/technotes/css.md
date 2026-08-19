# CSS / SCSS / LESS language support

Status: **implemented and working**, following the same LSP pattern as `typescript.md`
(TypeScript/JavaScript) and the F#/Python/XAML registrations already in
`LspServerRegistry.CreateDefault()`.

Date: 2026-08-18.

## What existed before this

Nothing. The only prior trace of CSS in the codebase was a project-browser file icon
(`.css` → `CssFileIcon` in `ICSharpCode.SharpDevelop.addin`) - no syntax highlighting was
wired up, and no language service. `.scss`/`.less` had no trace at all.

## Server: vscode-css-language-server

`vscode-langservers-extracted` (`npm install -g vscode-langservers-extracted`) ships
`vscode-css-language-server`, the same CSS/SCSS/LESS language server VS Code itself uses. Unlike
TypeScript 7's native Go binary, there is no native alternative here - the installed command is
a `#!/usr/bin/env node` bin shim, so a Node runtime is required at launch (the same dependency
the *old*, pre-TS7 `typescript-language-server` had). `Process.Start` runs the shim directly on
Unix (the OS honors the shebang); npm gives it a `.cmd` proxy on Windows that `Process.Start` can
run the same way.

**One binary serves all three dialects**, but the server picks its parser from the LSP
`languageId` sent in `textDocument/didOpen`, not the file extension - `"css"`, `"scss"`, and
`"less"` are three different, non-interchangeable language ids. So `.css`/`.scss`/`.less` each
get their own `LspServerLaunchSpec` (same command, different `LanguageId`), which means three
separate `LspLanguageService` instances/child processes per workspace root - the same tradeoff
`LspServerRegistry.CreateDefault()` already makes for `.ts` vs `.js` (also one binary, two
specs), so this isn't a new inconsistency.

## Implementation

### 1. Its own addin, owning its own LSP wiring end to end

`src/AddIns/BackendBindings/CssBinding/` - a self-contained addin (`CssBinding.csproj`/
`.addin`), mirroring `FSharpBinding`'s and `TypeScriptBinding`'s shape, not bundled into
AvalonEdit.AddIn (the core text editor) - so a user who doesn't do web development can disable
or remove CSS support independently, the same rule every other language addin already follows.
Registered in `OpenDevelop.Mvp.slnx`/`SharpDevelop.sln`/`SharpDevelop.Tests.sln` alongside the
other BackendBindings addins.

**Architecture note (2026-08-18, same-day refactor)**: binary resolution and
`LspServerLaunchSpec` registration originally lived in `LspServerRegistry.CreateDefault()`
(Base) - the first pass copied the `.xaml`/F#/Python pattern too literally. That was wrong:
Base is the shared "IDE semantic service layer" (`language-services.md`'s own layering rules)
and has no business hardcoding that CSS needs `vscode-css-language-server` specifically -
that's exactly the kind of per-language knowledge each language's own addin should own, so that
disabling `CssBinding` means Base never even tries to resolve the binary. Moved entirely into
`RegisterCssLanguageServiceCommand.cs` (namespace `CssBinding`):

```csharp
public override void Run()
{
    var cssLsp = NpmLanguageServerLocator.TryFindBinShim(
        "OD_CSS_LSP_BIN", "vscode-langservers-extracted", "vscode-css-language-server");
    if (cssLsp != null)
    {
        LspServiceManager.RegisterExtension(".css", new LspServerLaunchSpec("css", cssLsp, null, "--stdio"));
        LspServiceManager.RegisterExtension(".scss", new LspServerLaunchSpec("scss", cssLsp, null, "--stdio"));
        LspServiceManager.RegisterExtension(".less", new LspServerLaunchSpec("less", cssLsp, null, "--stdio"));
    }
    var registry = SD.GetRequiredService<LanguageServiceRegistry>();
    registry.RegisterExtension(".css", LspServiceManager.GetService);
    // ...same for .scss/.less
}
```

`LspServiceManager.RegisterExtension(extension, spec)` is a pre-existing API ("Allows addins to
register additional LSP server mappings at startup") that was simply unused until this refactor
- no new Base surface was needed, just actually using what was already there instead of listing
every language inline in `CreateDefault()`.

The npm-binary-resolution *mechanics* (walking npm global roots, finding a plain bin shim on
PATH or under a package's `bin/`) are genuinely shared, generic infrastructure - promoted to a
new public `NpmLanguageServerLocator` (`src/Main/Base/Project/Src/LanguageServices/Lsp/
NpmLanguageServerLocator.cs`, `NpmGlobalRoots()` + `TryFindBinShim()`). The *decision* of which
package/binary/extensions to use stays entirely in `CssBinding`.

Wired in `CssBinding.addin`:

- `/SharpDevelop/Autostart` → `RegisterCssLanguageServiceCommand`.
- `/SharpDevelop/ViewContent/TextEditor/CodeCompletion` → `Css-LSP` `CodeCompletionBinding`
  for the same three extensions.
- `/SharpDevelop/ViewContent/TextEditor/Extensions` → `TextEditorExtension` for the shared,
  backend-agnostic `LanguageServiceOutlineExtension` (see "Document Outline" below - the exact
  same class `TypeScriptBinding.addin`/`HtmlBinding.addin` register, not a CSS-specific copy).

**One binary serves all three dialects**, but the server picks its parser from the LSP
`languageId` sent in `textDocument/didOpen`, not the file extension - `"css"`, `"scss"`, and
`"less"` are three different, non-interchangeable language ids. So `.css`/`.scss`/`.less` each
get their own `LspServerLaunchSpec` (same command, different `LanguageId`), which means three
separate `LspLanguageService` instances/child processes per workspace root - the same tradeoff
already made for `.ts` vs `.js` (also one binary, two specs), so this isn't a new
inconsistency.

### 4. Syntax highlighting

`CssBinding/Resources/Css.xshd`, registered in `CssBinding.addin`'s
`/SharpDevelop/ViewContent/AvalonEdit/SyntaxModes` with `extensions=".css;.scss;.less"` (must
match the xshd's own `<SyntaxDefinition extensions=...>` exactly - same
`AddInTreeSyntaxMode.LoadXshd()` `SequenceEqual` gotcha `typescript.md` already documents for
`TypeScript.xshd`).

Based directly on AvalonEdit's own bundled `CSS-Mode.xshd`
(`src/Libraries/AvalonEdit/ICSharpCode.AvalonEdit/Highlighting/Resources/CSS-Mode.xshd`, a
working, proven CSS lexer already vendored into the repo) rather than written from scratch,
extended with:
- `//` line comments (SCSS/LESS have them; plain CSS doesn't, but a stray `//` in a `.css` file
  isn't otherwise meaningful, so treating it as a comment there too is harmless).
- A `Variable` color/rule for `$name`/`@name` (SCSS `$variables`, LESS `@variables`).

As with `TypeScript.xshd`, the lexer only colors comments/strings/selectors/properties/values/
variables/braces - it has no idea what a valid CSS property or a Sass function actually is;
that richer semantic understanding (if the server ever surfaces it as semantic tokens) is a
separate, not-yet-wired concern, same as TypeScript's member-name semantic tokens.

### Document Outline

The Outline pad populates for `.css`/`.scss`/`.less` files too, via the same generic
`LanguageServiceOutlineExtension`/`LanguageServiceOutlineContentHost` TypeScript uses (see
`typescript.md`'s own "Document Outline" section for the full design and the multi-top-level-
symbol bug that had to be fixed along the way). Verified live via `od.outline-pad.content`: a
sample with a `body { ... }` rule and a `.button:hover { ... }` rule shows both selectors as
top-level entries under a synthetic file-name root, not just the first one.

## Verification (live, `./launch.sh`)

- `RegisterCssLanguageServiceCommand` logs "Registered CSS/SCSS/LESS extensions" on startup.
- Opening a `.css` file: `od.active-view` reports `syntaxHighlighting: "Css"`; a real
  `node .../vscode-css-language-server --stdio` child process starts (confirmed via `ps aux`)
  and stays alive (no `Disconnected` event, no restart).
  Opening a `.scss` file with `$brand`/`&:hover` content: same highlighting mode, and a
  **second**, independent `vscode-css-language-server` process starts (confirmed two distinct
  PIDs alive simultaneously) - proving the per-extension/per-languageId process split works as
  designed, not sharing state across dialects.
- No crashes, no `ErrorCodeInvalidParams`-class issues - this server was never suspected of the
  TypeScript 7 nil-`initialized`-params bug (`LspLanguageService`'s handshake fix in
  `language-services.md`/`typescript.md` already applies uniformly to every LSP backend,
  including this one, since it's shared code).

## Known limits / remaining work

- **No fixture + integration test yet.** Add a `tests/fixtures/`-style CSS/SCSS/LESS sample
  (the F#/AspNetCore/TypeScript tests in `AddInTests.cs` are the templates) asserting
  `syntaxHighlighting == "Css"` and that the language server process starts and stays alive.
- **Decide the vscode-langservers-extracted version to pin** in whatever the repo/dev machine's
  npm global install is expected to have - the resolver accepts whatever's found, unpinned,
  same posture `typescript.md` documents for the TS7 binary choice.
- **Node dependency**: unlike TypeScript 7, this server needs Node on the machine. If that
  becomes a real constraint, the only native alternative would be a from-scratch CSS LSP
  implementation - not evaluated, likely not worth it for CSS's much smaller semantic surface
  compared to TypeScript.
- **No semantic tokens wired.** `vscode-css-language-server` supports basic completion/hover/
  diagnostics/document-symbols over LSP out of the box (all already available through the shared
  `LspLanguageService`/`ILanguageService` surface, same as F#/Python), but this pass only
  verified the connection comes up and highlighting renders - it did not exercise completion/
  hover end-to-end in the running editor.
