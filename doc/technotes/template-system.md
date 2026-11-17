# Template system (New Item / New Project)

**Status (2026-07-28): one shared implementation.** "New Item"/"New Project" are backed by
`Microsoft.TemplateEngine` (the same engine `dotnet new` and modern Visual Studio use), not
SharpDevelop's or MonoDevelop's own proprietary template formats. The reasons: industry-standard
`template.json` format, the same global cache `dotnet new install` populates, and one engine for
both file and project templates.

`TemplateDiscoveryService`/`TemplateSummary`/`TemplateInstantiationResult`
(`Main/Base/Project/Src/Templates/`) are the single implementation behind the `ProjectBrowserController`
new-item/new-project commands (see doc/technotes/solution-explorer.md), with doc comments kept.

`TemplateEngineHost.Create(string hostIdentifier)` builds the engine host; the identifier
("opendevelop") is used only informationally - it identifies the calling host to
Microsoft.TemplateEngine, e.g. in its own logs.

Verified via OpenDevelop's own Base + App layer builds (`ICSharpCode.SharpDevelop.csproj`,
`SharpDevelop.csproj`).
