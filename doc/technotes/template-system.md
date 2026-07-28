# Template system (New Item / New Project)

**Status (2026-07-28): one shared implementation.** Both hosts back "New Item"/"New Project" with
`Microsoft.TemplateEngine` (the same engine `dotnet new` and modern Visual Studio use), not
SharpDevelop's or MonoDevelop's own proprietary template formats - see UnoDevelop's own
`externals/OpenDevelop/doc/technotes/template-system.md` for the original decision rationale (industry-standard `template.json`
format, shares the same global cache `dotnet new install` populates, one engine for both file and
project templates).

`TemplateDiscoveryService`/`TemplateSummary`/`TemplateInstantiationResult`
(`Main/Base/Project/Src/Templates/`) were two near-identical, independently-written copies - found
while unifying the `ProjectBrowserController` new-item/new-project commands (see
doc/technotes/solution-explorer.md). `TemplateSummary`/`TemplateInstantiationResult` differed only
by doc comments (UnoDevelop's copy had them, OpenDevelop's had been stripped); `TemplateDiscoveryService`
differed by comments plus exactly one line - which `*TemplateEngineHost.Create()` to call.

Unified onto UnoDevelop's fuller-commented versions (canonical, doc comments kept). The two
`*TemplateEngineHost` classes (`UnoTemplateEngineHost`/`OpenDevelopTemplateEngineHost`) were
themselves identical except for the `HostIdentifier` constant ("unodevelop" vs "opendevelop", used
only informationally - it identifies the calling host to Microsoft.TemplateEngine, e.g. in its own
logs) - folded into a single parameterized `TemplateEngineHost.Create(string hostIdentifier)`, with
`TemplateDiscoveryService`'s parameterless constructor picking the identifier via `#if HAS_UNO`
instead of delegating to a per-host class that existed only to hold one string constant.

Nothing was left per-host here: unlike `ProjectBrowserController` (native dialog/clipboard calls)
or the debugger's DAP session (real launch/attach semantic differences), this file set had no
actual host-specific behavior once the identifier string was parameterized - the whole duplication
was accidental (two people/sessions independently porting the same `Microsoft.TemplateEngine.IDE`
usage), not a case of a shared interface with genuinely different backends.

Verified via `UnoDevelop.slnx` build, OpenDevelop's own Base + App layer builds (`ICSharpCode.SharpDevelop.csproj`,
`SharpDevelop.csproj`), and the full test suite (`UnoDevelop.Core.Tests`, `UnoDevelop.IntegrationTests`).
