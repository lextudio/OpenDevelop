# HTML language support

Status: **implemented and working**, following the exact same pattern as `css.md`
(CSS/SCSS/LESS) - same npm package, same addin shape, same generic Outline pad bridge.

Date: 2026-08-18.

## What existed before this

Nothing genuinely reusable. `src/AddIns/BackendBindings/AspNet.Mvc` (legacy, dead - not
referenced in `OpenDevelop.Mvp.slnx`, same orphaned state the old `TypeScript` addin was in
before `typescript.md`'s work) contains hand-rolled HTML folding-region parsers
(`HtmlReader.cs`, `HtmlFoldParser.cs`, `HtmlElementFold.cs`, `RazorHtmlReader.cs`, ...) - no
completion, no diagnostics, tied to obsolete `System.Web.Razor`. Not worth porting: strictly
inferior to a real language server. The only other trace was AvalonEdit's own bundled,
never-wired `HTML-Mode.xshd` (the same "proven library lexer nobody registered" situation
`CSS-Mode.xshd` was in).

## Server: vscode-html-language-server

The exact sibling of CSS's `vscode-css-language-server` - same npm package
(`vscode-langservers-extracted`, `npm install -g vscode-langservers-extracted`), same
`#!/usr/bin/env node` bin-shim shape, same Node-runtime dependency. See `css.md` for the full
rationale on why a Node dependency is an acceptable tradeoff here (no native alternative
exists, same as CSS).

## Implementation

### 1. Its own addin

`src/AddIns/BackendBindings/HtmlBinding/` - a self-contained addin (`HtmlBinding.csproj`/
`.addin`), mirroring `CssBinding`'s and `TypeScriptBinding`'s shape exactly, not bundled into
AvalonEdit.AddIn. Registered in `OpenDevelop.Mvp.slnx`/`SharpDevelop.sln`/
`SharpDevelop.Tests.sln` alongside the other BackendBindings addins.

### 2. LSP wiring lives entirely in the addin, not in LspServerRegistry (Base)

`HtmlBinding` resolves its own server binary and registers its own `LspServerLaunchSpec` entirely
from its own addin's `Autostart` command, via `LspServiceManager.RegisterExtension` (an API that
already existed for exactly this, just unused until this same-day refactor - `TypeScriptBinding`
and `CssBinding` were updated to the same pattern; see their own technotes and
`language-services.md` for the full before/after). `LspServerRegistry.CreateDefault()` in Base
only knows about `.xaml`/`.fs`/`.fsi`/`.py` now - it has zero knowledge that TypeScript, CSS, or
HTML exist. This is a deliberate architecture rule, not incidental: Base is the "IDE semantic
service layer" (`language-services.md`'s own layering rules) and should not hardcode per-language
binary discovery for languages that live in their own toggleable addins - if `CssBinding` is
disabled, Base should never even try to resolve `vscode-css-language-server`, not just skip
registering the extension mapping.

The binary-resolution helpers (`NpmGlobalRoots`, and a shared `TryFindBinShim` for plain npm bin
shims like CSS/HTML's) were promoted to a new public `NpmLanguageServerLocator`
(`src/Main/Base/Project/Src/LanguageServices/Lsp/NpmLanguageServerLocator.cs`) - genuinely
shared, generic infrastructure any language addin can reuse, as opposed to the per-language
*decision* of which package/binary/extensions to register, which stays in each addin.

`RegisterHtmlLanguageServiceCommand.cs` (namespace `HtmlBinding`):

```csharp
var htmlLsp = NpmLanguageServerLocator.TryFindBinShim(
    "OD_HTML_LSP_BIN", "vscode-langservers-extracted", "vscode-html-language-server");
if (htmlLsp != null) {
    var html = new LspServerLaunchSpec("html", htmlLsp, null, "--stdio");
    LspServiceManager.RegisterExtension(".html", html);
    LspServiceManager.RegisterExtension(".htm", html);
}
```

wired in `HtmlBinding.addin`:

- `/SharpDevelop/Autostart` → `RegisterHtmlLanguageServiceCommand`.
- `/SharpDevelop/ViewContent/TextEditor/CodeCompletion` → `Html-LSP` `CodeCompletionBinding`
  for `.html`/`.htm`.
- `/SharpDevelop/ViewContent/TextEditor/Extensions` → `TextEditorExtension` for the shared,
  backend-agnostic `LanguageServiceOutlineExtension` (see `typescript.md`'s "Document Outline"
  section for the full design) - the exact same class every other LSP-backed language addin
  registers, not an HTML-specific copy.

### 3. Syntax highlighting

`HtmlBinding/Resources/Html.xshd`, registered in `HtmlBinding.addin`'s
`/SharpDevelop/ViewContent/AvalonEdit/SyntaxModes` with `extensions=".htm;.html"`. Copied
verbatim from AvalonEdit's own bundled `HTML-Mode.xshd`
(`src/Libraries/AvalonEdit/ICSharpCode.AvalonEdit/Highlighting/Resources/HTML-Mode.xshd`) rather
than written from scratch - a working, proven lexer (tags/attributes/entities/embedded
`<script>` blocks, including an `<Import ruleSet="JavaScript/" />` reference into AvalonEdit's
own built-in JavaScript mode) that was simply never registered by any addin before.

## Verification (live, `./launch.sh`)

- `RegisterHtmlLanguageServiceCommand` logs "Registered HTML extensions" on startup.
- Opening a `.html` file: `od.active-view` reports `syntaxHighlighting: "HTML"`; a real
  `node .../vscode-html-language-server --stdio` child process starts (confirmed via `ps aux`)
  and stays alive (no `Disconnected` event, no restart).
- Outline pad: for a sample with `<html><head><title>`/`<body><h1 id="heading">`/
  `<p class="text">`, `od.outline-pad.content` showed the full element tree (`html` → `head`/
  `body` → `title`, `h1#heading`, `p.text`) under a synthetic file-name root - the same
  multi-top-level-node fix `typescript.md` documents (though HTML's outline is naturally a
  single-root tree like XAML's, the shared code path still applies uniformly).
- Verified all three addins (TypeScript/CSS/HTML) together in the same running instance after
  the `LspServerRegistry` decentralization refactor: all three register, all three launch their
  own real LSP child process, no crashes.

## Known limits / remaining work

Same as `css.md`'s own list: no fixture + integration test yet, no version pin decided for
`vscode-langservers-extracted`, Node dependency is a real (if accepted) constraint, and
completion/hover were not exercised end-to-end in the running editor beyond confirming the
connection comes up and the Outline pad populates.
