# TypeScript / JavaScript language support (TypeScript 7 Go LSP)

Status: **implemented and working, including semantic tokens.** The server crash documented
below (problem #1) was root-caused to a client protocol bug - not a server-side race, not
something requiring an upstream fix or a version pin - and is fixed. There is still a real,
separate, minor upstream robustness gap (also below) worth reporting, but it no longer blocks
normal use.

Date: 2026-08-18 (root cause found and fixed same day, after the initial investigation below).

## Decision: rebuild, do not migrate

The legacy addins are dead and are **not** reference material:

- `src/AddIns/BackendBindings/TypeScript/` (`TypeScriptBinding`, SharpDevelop 5.x) hosts
  the 2014-era TypeScript compiler in an embedded **Noesis.Javascript** V8 bridge (x86
  native DLL); `Libraries/` no longer contains that DLL, so it cannot build. It is a
  v4.5/x86/old-style csproj and must be dropped from `SharpDevelop.sln`.
- `src/AddIns/BackendBindings/Scripting/` (`ICSharpCode.Scripting`) is the IronPython-era
  base and shares the same fate.
- The MonoDevelop port (`mrward/typescript-addin`) is the same codebase on **V8.NET**,
  alpha-grade, stuck on TS 1.4/1.5 (2015). Nothing to copy.

**Chosen direction: use the TypeScript 7 native (Go) language server over LSP.** TypeScript
7.0 GA'd 2026-07-08 (8–12x faster, `typescript` npm package) and its language server is LSP
natively (`tsc --lsp --stdio`); the `@typescript/native-preview` npm package ships current
nightlies with the same surface. The binary is a **native executable** (no Node runtime,
no embedded JS engine) — exactly the out-of-process child model OpenDevelop already uses for
the OOP designers. Per-team decision: preview build preferred, GA acceptable.

## What already existed (no new LSP infrastructure)

OpenDevelop has a full LSP client stack in the Base project:

- `src/Main/Base/Project/Src/LanguageServices/` — `LanguageServiceRegistry`,
  `LspServiceManager`, `LspLanguageService`, `LspCodeCompletionBinding`,
  `LanguageServiceSemanticColorizer`.
- `LspServerRegistry.CreateDefault()` already registered `.ts/.tsx/.js/.jsx` →
  `typescript-language-server --stdio` (Node-based bridge); F# uses fsautocomplete, XAML
  uses wpf-xaml-ls, Python uses pylsp.
- The F# addin is the reference addin-side pattern: a 5-line startup command
  (`RegisterFSharpLanguageServiceCommand`) that calls
  `registry.RegisterExtension(".fs", LspServiceManager.GetService)`.

So the task was: swap the TS/JS launch spec to the TypeScript 7 binary, add the
extension registration, and give the files a syntax definition so they open at all.

## Implementation

### 1. Launch spec → TypeScript 7 Go binary, resolved from the addin itself

**Architecture note (2026-08-18, same-day refactor)**: this used to live in
`LspServerRegistry.CreateDefault()` (Base) - the first pass. That was wrong for the same reason
`css.md`'s own architecture note explains: Base is the shared "IDE semantic service layer" and
shouldn't hardcode that TypeScript needs a Go binary specifically. `TryFindTypeScriptGoBinary()`
and its `IsNodeShim()` helper now live in `RegisterTypeScriptLanguageServiceCommand.cs`
(`src/AddIns/BackendBindings/TypeScriptBinding/`), called from that command's own `Run()`, which
registers the resulting `LspServerLaunchSpec`s directly via
`LspServiceManager.RegisterExtension` - a pre-existing API for exactly this that was simply
unused until this refactor. `LspServerRegistry.CreateDefault()` now only knows about `.xaml`/
`.fs`/`.fsi`/`.py`.

- `.ts/.tsx/.js/.jsx` use a `LspServerLaunchSpec` whose command is the native
  TypeScript 7 executable and arguments are `--lsp --stdio`. **Gotcha:** the spec
  constructor is `(languageId, command, workingDirectory, params arguments)` — passing
  `("typescript", bin, "--lsp", "--stdio")` puts `--lsp` in the working-directory slot
  (observed as `ProcessStartInfo` failing with working dir `--lsp`). Pass `null` for the
  working directory.
- `TryFindTypeScriptGoBinary()` resolves the executable:
  1. `OD_TSGO_BIN` environment variable wins.
  2. Otherwise it walks every npm global root (see `NpmLanguageServerLocator.NpmGlobalRoots()`,
     `src/Main/Base/Project/Src/LanguageServices/Lsp/NpmLanguageServerLocator.cs` - the one
     piece of this that IS genuinely shared, generic npm-root-walking infrastructure, reused by
     `CssBinding`/`HtmlBinding` too) under `@typescript/` and finds a file named `tsgo` or
     `tsc`, **skipping the npm bin shims** (`<pkg>/bin/tsgo`, a `#!/usr/bin/env node` JS wrapper
     that would need Node). The native Go executable lives in the **platform package's `lib/`**
     directory. Two real layouts occur:
     - flat: `@typescript/native-preview-darwin-arm64/lib/tsgo`
     - nested (npm global installs): `@typescript/native-preview/node_modules/@typescript/
       native-preview-darwin-arm64/lib/tsgo`
     A recursive `Directory.EnumerateFiles(..., AllDirectories)` with a `IsNodeShim`
     filter handles both. GA package ships the binary as `typescript-<platform>/lib/tsc`.
- If no binary is found the TS/JS extensions are **left unregistered** and
  `LspServiceManager.GetService` falls back to lexical-only highlighting (same behavior as
  `.xaml` when wpf-xaml-ls was never built).

### 2. Its own addin (2026-08-18: moved out of AvalonEdit.AddIn)

`src/AddIns/BackendBindings/TypeScriptBinding/` - a self-contained addin
(`TypeScriptBinding.csproj`/`.addin`), mirroring `FSharpBinding`'s shape, not bundled into
AvalonEdit.AddIn (the core text editor). This was a deliberate fix, not the original design:
TypeScript/JS support initially lived inside AvalonEdit.AddIn (a "preinstalled", effectively
mandatory addin), which meant a user who doesn't do web development had no way to disable or
remove TypeScript support without disabling the text editor itself. Every other language
(F#, VB, XAML, C++, ...) already lives in its own toggleable addin under
`src/AddIns/BackendBindings/`; TypeScript (and CSS, see `css.md`) now follow the same rule.
Registered in `OpenDevelop.Mvp.slnx`/`SharpDevelop.sln`/`SharpDevelop.Tests.sln` alongside the
other BackendBindings addins - this also freed up the `TypeScriptBinding` project name, since
the dead legacy `TypeScriptBinding.csproj` (`src/AddIns/BackendBindings/TypeScript/`, see
`language-services.md`'s "Decision: rebuild, do not migrate") was deleted and its own solution
entries removed as part of this move.

`RegisterTypeScriptLanguageServiceCommand.cs` (now in that addin, namespace `TypeScriptBinding`)
is an `AbstractCommand` that resolves the TS7 binary itself and registers its own
`LspServerLaunchSpec`s (see "Launch spec" above), then binds `.ts`, `.tsx`, `.js`, `.jsx` to
`LspServiceManager.GetService` on `LanguageServiceRegistry`, wired in `TypeScriptBinding.addin`:

- `/SharpDevelop/Autostart` → `RegisterTypeScriptLanguageServiceCommand`.
- `/SharpDevelop/ViewContent/TextEditor/CodeCompletion` → `TypeScript-LSP`
  `CodeCompletionBinding` for the same four extensions.
- `/SharpDevelop/ViewContent/TextEditor/Extensions` → `TextEditorExtension` for the shared,
  backend-agnostic `LanguageServiceOutlineExtension` (see "Document Outline" below).

### 3. Syntax highlighting so the files open at all

A file with no highlighting definition used to crash on open
(`ThemeAwareHighlightingColorizer` dereferences `Definition.Properties` in its ctor when
`GetDefinitionByExtension` returns null):

- `CodeEditor.UpdateSyntaxHighlighting` now only inserts the colorizer when
  `highlighting != null` (unknown extensions render as plain text instead of throwing).
- The 2010 SharpDevelop `TypeScript.xshd` was moved from the dead TypeScriptBinding into
  `TypeScriptBinding/Resources/TypeScript.xshd` (the new addin above) and embedded via csproj
  (`<EmbeddedResource Include="Resources\TypeScript.xshd" />`), registered in
  `TypeScriptBinding.addin` as `/SharpDevelop/ViewContent/AvalonEdit/SyntaxModes` with
  `extensions=".ts;.tsx;.js;.jsx"`.

### Document Outline (2026-08-18)

The Outline pad now populates for `.ts`/`.tsx`/`.js`/`.jsx` files, via
`LanguageServiceOutlineExtension`/`LanguageServiceOutlineContentHost`
(`src/Main/Base/Project/Src/LanguageServices/`) - a generic `ITextEditorExtension`/
`IOutlineContentHost` pair that calls `ILanguageService.GetDocumentOutlineAsync` through
`LanguageServiceRegistry`, not anything TypeScript-specific. It's a direct promotion of
XamlBinding's own `XamlOutlineContentHost`/`XamlOutlineLspProvider`, which turned out to already
be fully backend-agnostic internally (nothing XAML-specific in the actual implementation, only
its name and location) - so instead of writing a second, near-identical copy for TypeScript
(and a third for CSS), the shared implementation moved to Base, where every LSP-registered
language's own addin can wire it up with one `<TextEditorExtension>` addin node.
XamlBinding keeps its own original copy for now (a working, tested feature not worth risking on
this pass); a future cleanup could point it at the shared one too.

One real difference from the XAML case, found live: `DocumentOutlineControl` originally accepted
a single root node (`SetRoot`), which is fine for XAML (always exactly one root element) but wrong
for a plain source file, whose `textDocument/documentSymbol` response is a FLAT LIST of top-level
symbols (multiple top-level functions/classes in one `.ts` file, multiple selectors in one `.css`
file). Taking `nodes.FirstOrDefault()` verbatim silently dropped every top-level symbol but the
first - confirmed live via `od.outline-pad.content`. The control now exposes `SetRoots(...)`
(multi-root, selection-preserving), which is the preferred way to feed it a forest; this file's
synthetic-unnamed-root wrapper predates that API and still works, but a future cleanup can pass
the node list directly.
- **Gotcha:** `AddInTreeSyntaxMode.LoadXshd()` does `SequenceEqual` between the addin
  node's `extensions` and the `<SyntaxDefinition extensions=...>` inside the xshd itself.
  The xshd originally said `extensions=".ts"`, so loading failed with a wrapped
  `HighlightingDefinitionInvalidException` ("Error delay-loading highlighting definition").
  Both sides must list the same semicolon-separated set.

The lexer only colors keywords/comments/strings/numbers. **Member/method names (`console
.log`) are not in the xshd** — they come from **semantic tokens** (the LSP server), which
is why they stay uncolored until the Go server actually serves `semanticTokens/full`.

### 4. LSP client changes for the TypeScript 7 server

`src/Main/Base/Project/Src/LanguageServices/Lsp/LspLanguageService.cs`:

- **`client/registerCapability` must be answered with a literal `null` result.**
  The TS7 server asks the client to register its configuration-change watcher right after
  `initialized`; StreamJsonRpc would otherwise reply "method not found", and a non-null
  reply (e.g. `[]`) is rejected by its strict `lsproto.Null` unmarshalling (observed as
  `json: cannot unmarshal into Go lsproto.Null: expected null, got []`). Added
  `rpc.AddLocalRpcMethod("client/registerCapability", new Func<JsonElement, object?>(_ => null))`.
  Harmless for servers that never ask (wpf-xaml-ls, fsautocomplete, pylsp).
- **`window/logMessage` is now captured** (`OnServerLogMessage` → Debug log, prefixed
  `LSP server log`) so server-side initialization errors are visible in the app log.
- **`UpsertDocumentAsync` is serialized** with a `_documentGate` `SemaphoreSlim`:
  semantic-colorizer refreshes fire concurrently and were corrupting the plain
  `_openDocuments` dictionary (`Operations that change non-concurrent collections must
  have exclusive access`).
- **300 ms startup pause** for the TS/JS language ids after `initialized`: the Go server
  builds its session asynchronously and serves every request on its own goroutine;
  requests arriving before the session exists crash it (see Known problems). This is a
  mitigation, not a fix.

### 5. DevFlow agent port is now environment-controllable

To test without clobbering a developer's running instance (and per request — no hard-coded
port edits), `src/Main/SharpDevelop/Startup/App.xaml.cs` now reads **`DEVFLOW_AGENT_PORT`**
and passes `new AgentOptions { Port = ... }` to `AddWpfDevFlowAgent`, falling back to
`DevFlowAgentPortResolver.GetPortFromAssemblyMetadata()` (the pinned `DevFlowPort.cs`,
9299) and then `AgentOptions.DefaultPort`. `DevFlowPort.cs` itself is unchanged. The
pattern mirrors `wpf-labs/src/DevFlow/LibreWpfDevFlowTestApp/App.xaml.cs`. Note: `AgentOptions`
lives in `Microsoft.Maui.DevFlow.Agent.Core`, `DevFlowAgentPortResolver` in
`LeXtudio.DevFlow.Agent.Core`.

## Verification notes

- The native binary was verified standalone with a hand-rolled stdio LSP client
  (initialize → initialized → didOpen → completion → semanticTokens → documentSymbol):
  initialize handshake, `client/registerCapability` handling, and completion all work.
  Forcing the `[]` registerCapability reply reproduces the server's unmarshal error.
- End-to-end in the app (OD_TEST_MODE=1, `DEVFLOW_AGENT_PORT=9298`): `.ts` opens in
  AvalonEdit with lexical highlighting; `RegisterTypeScriptLanguageServiceCommand` logs
  "Registered TS/JS extensions"; the TS7 binary is launched with `--lsp --stdio`; the
  launch spec and binary resolution were verified through the added `LspServiceManager`
  debug log.

## Known problems

1. **RESOLVED (2026-08-18) - TypeScript 7 server crashed on every document request; root
   cause was our own client sending a malformed `initialized` notification, not a server
   race.** Originally documented as a suspected server-side startup race (see the investigation
   trail below, kept for the record since the debugging technique is reusable). The actual
   cause, confirmed by cloning `microsoft/typescript-go` at the exact installed commit
   (`9977d6d38fcc78de8ae71770f3aa08256e6cc861`, matching npm's `gitHead`) and instrumenting its
   own dispatch loop with temporary stderr prints:

   `LspLanguageService.EnsureStartedAsync` sent the handshake's `initialized` notification via
   `rpc.NotifyAsync("initialized")` - StreamJsonRpc's no-parameters overload, which omits the
   `params` member from the JSON-RPC message entirely. `internal/lsp/lsproto/lsp.go`'s
   `UnmarshalParams` explicitly rejects that for any method not declared `NoParams`:
   ```go
   // The base protocol defines params as `array | object`; reject anything else
   // (absent, null, or a scalar).
   if k := raw.Kind(); k != '{' && k != '[' {
       return params, fmt.Errorf("%w: params must be an object or array", ErrorCodeInvalidParams)
   }
   ```
   `InitializedParams` is not `NoParams`, so `registerNotificationHandler`'s wrapper
   (`internal/lsp/server.go`) returned `ErrorCodeInvalidParams` **before ever calling
   `handleInitialized`** - meaning `s.session = project.NewSession(...)` never ran, for the
   entire lifetime of that server process. Every later request that touches the session
   (`textDocument/hover`, `semanticTokens/full`, `documentSymbol`, and ~20 others registered via
   `registerLanguageServiceDocumentRequestHandler` - see below) then dereferenced a nil
   `*Session` and crashed the process. **This was not a race - it was 100% deterministic**: a
   diagnostic build of `tsgo` confirmed `handleInitialized`'s own first line ("about to assign
   session") never printed even once, on any run, while every subsequent dispatched message
   showed `session-nil=true` right up to the crash.

   **Fix**: send a literal empty object instead -
   `rpc.NotifyWithParameterObjectAsync("initialized", new { })`
   (`LspLanguageService.cs`). Verified live: with the real installed `tsgo` binary (no
   diagnostic patch), `.ts` files now open, `didOpen`/`semanticTokens/full` succeed, and no
   crash or disconnect occurs. **This is a shared-code fix - see `language-services.md`'s
   Backend Implementation Layer section for why it applies to every LSP-backed language, not
   just TypeScript.**

   A second, independent, genuine (if now largely moot for us) upstream gap was found along
   the way and is worth a GitHub issue for whoever has time: `internal/lsp/server.go`'s
   `registerLanguageServiceDocumentRequestHandler` (used for hover, semantic tokens,
   definitions, code actions, formatting, and ~20 other request types) never checks
   `s.session == nil` before calling `s.session.GetLanguageService(...)` - unlike its sibling
   `registerRequestHandler`/`registerNotificationHandler`, which both guard with
   `if s.session == nil { return ErrorCodeServerNotInitialized }`. A malformed/early client
   request of any of those ~24 types (from a genuinely buggy or just non-conformant client,
   not necessarily our exact mistake) would still crash the server instead of getting a clean
   protocol error. No existing `microsoft/typescript-go` issue matches this; the closest
   precedent, [#1744](https://github.com/microsoft/typescript-go/issues/1744) (same
   nil-`*Session` crash class, different cause: client never sent `initialized` at all), was
   fixed upstream for the `didOpen` notification path in
   [#1747](https://github.com/microsoft/typescript-go/pull/1747) - the equivalent guard for
   `registerLanguageServiceDocumentRequestHandler`'s request path appears to have been missed.

   **Also fixed along the way, independently valid regardless of the above**: `EnsureStartedAsync`
   used to assign `_rpc` immediately after `rpc.StartListening()`, well before the
   `initialize`/`initialized` handshake actually completed - exposing a half-initialized
   connection to `EnsureStartedAsync`'s fast path (`if (_rpc is not null) return true;`,
   checked *outside* `_startGate`), so a concurrent caller (e.g. the semantic colorizer
   refreshing while another `UpsertDocumentAsync` call's own handshake was still in flight)
   could fire a request on that connection before the handshake was actually done. `_rpc` is
   now assigned only after the full handshake (`initialize` + `initialized` + the startup
   delay) completes, so every concurrent caller genuinely waits at `_startGate` until the
   connection is real. And: `LspLanguageService` used to leave `_rpc` permanently non-null
   after the underlying process died, so a crash (from this bug, or any other cause) disabled
   the language service silently and permanently until the whole app restarted.
   `JsonRpc.Disconnected` is now handled (`OnRpcDisconnected`): it nulls `_rpc`, clears
   `_openDocuments` (so the next `UpsertDocumentAsync` resends a real `didOpen` rather than a
   `didChange` against a document the new process never opened), and `EnsureStartedAsync`
   cleans up the stale dead `_process` before spawning a replacement. This auto-restart is
   defense in depth for any *future* crash, now that the specific crash this session
   investigated is fixed.
2. **`semanticTokens/full` can go unanswered** in some hand-rolled setups when the client
   declares an empty token-type legend; the app's `SupportedSemanticTokenTypes` is non-empty
   and works when the server stays alive.
3. **Incremental-build staleness of the Base project.** Repeatedly, `dotnet build` on
   `ICSharpCode.SharpDevelop.csproj` reported success while
   `obj/Debug/net10.0-windows/ICSharpCode.SharpDevelop.dll` (and thus the copied copy in
   `src/Main/SharpDevelop/bin/Debug/net10.0-windows/`) still lacked the newest edits. The
   app (`dotnet run --no-build`) then ran stale code and looked like "the change did
   nothing". Verify changes are actually in the built DLL (e.g. search for a new string
   literal in `obj/Debug/.../ICSharpCode.SharpDevelop.dll`) before launching.
4. **`TypeScript.xshd` is a 2010 lexer.** Fine for keywords/comments/strings; member names
   rely on semantic tokens, and JSX/template-literal syntax is not tokenized.

## Remaining work

- **Fixture + integration test.** Add a `tests/fixtures/`-style TS sample (or extend the
  F#-pattern fixture; the AspNetCore/Razor and F# tests in
  `tests/OpenDevelop.IntegrationTests/AddInTests.cs` are the templates — open the file,
  assert `od.active-view` reports `syntaxHighlighting == "TypeScript"`), then assert the
  Go server process is alive and, once problem 1 is fixed, that semantic tokens arrive.
- **Decide the TS7 binary pin.** Preview (`@typescript/native-preview`) vs GA
  (`typescript`); the resolver currently accepts either (tsgo or tsc) and prefers the
  first non-shim found. Pin a known-good version in the npm global install that ships with
  the repo/dev machine.
- **Remove the legacy projects** `TypeScriptBinding` and `Scripting` from
  `SharpDevelop.sln`.
- **Drop diagnostic instrumentation** (`SyntaxModeDoozer` logging, `LspServiceManager`
  debug log, `OnServerLogMessage`) once stable — or keep `OnServerLogMessage`, it is useful.
- **JSX / template-literal highlighting** if the 2010 xshd proves too thin for real files.