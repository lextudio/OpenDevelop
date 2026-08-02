# OpenDevelop C# and VB Binding Roslyn Migration Plan

**Status: Draft plan**
**Date: 2026-08-02**
**Applicable repository:** `lextudio/OpenDevelop`

**Architectural relationship:** This document constrains `language-services.md` with respect to
C#/VB Binding, AddIn ownership, and lifecycle. Shared editor features must use Roslyn through
`LanguageServiceRegistry`/`ILanguageService`; `RoslynWorkspaceHelper` must not be established as a
long-term API sitting alongside the unified language service.

## 1. Goals

OpenDevelop is removing NRefactory and rebuilding the C# and Visual Basic language services on
Roslyn.

The migration must satisfy two goals at once:

1. Implement Parser, Completion, Semantic Highlighting, Diagnostics, Formatting, Code Actions, and
   Refactoring using modern Roslyn APIs.
2. Preserve SharpDevelop's original AddIn install and lifecycle boundaries.

The second point matters especially. `CSharpBinding` and `VBBinding` are not just code
directories - they are units of functionality the user can install, enable, disable, and
uninstall.

Therefore:

> After disabling a language Binding AddIn, every IDE feature specific to that language must
> completely stop registering and running.

Shared assemblies may provide infrastructure, but must not activate language features on their own
based on `.cs`, `.vb`, `.csproj`, or `.vbproj`.

---

## 2. Core architectural principles

### 2.1 Distinguish code reuse from feature ownership

Generic implementations may live in Base or the AvalonEdit AddIn, but registration ownership of
language features must stay with the corresponding Binding AddIn.

Suggested boundary:

```text
SharpDevelop Base
    Generic Roslyn Workspace and language-host infrastructure
    Does not itself register C# or VB features

AvalonEdit AddIn
    Generic editor UI, renderers, and extension points
    Does not itself enable C# or VB features by extension

CSharpBinding AddIn
    Registers and owns all C#-specific features

VBBinding AddIn
    Registers and owns all VB-specific features
```

For example, the following classes may live in a shared assembly:

```text
RoslynWorkspaceService
RoslynDocumentSynchronizer
RoslynDiagnosticHost
RoslynAnalyzerHost
RoslynCodeActionHost
SemanticHighlightingRenderer
DiagnosticMarkerRenderer
SignatureHelpWindow
QuickActionsWindow
```

But registration of the following providers must be done separately by each Binding AddIn:

```text
CSharpLanguageParticipant
VisualBasicLanguageParticipant
CSharpCompletionProvider
VisualBasicCompletionProvider
CSharpSemanticHighlightingProvider
VisualBasicSemanticHighlightingProvider
```

### 2.2 The AddIn is the final install unit

Splitting each language capability into its own user-visible AddIn is not recommended:

```text
CSharp.Completion.addin
CSharp.Formatting.addin
CSharp.Diagnostics.addin
```

Keep instead:

```text
CSharpBinding AddIn
VBBinding AddIn
```

A single AddIn can import multiple implementation assemblies, e.g.:

```xml
<Runtime>
    <Import assembly="CSharpBinding.dll" />
    <Import assembly="CSharpBinding.CodeAnalysis.dll" />
    <Import assembly="CSharpBinding.Refactoring.dll" />
</Runtime>
```

This allows splitting at the code level while keeping a single, unified install/upgrade/enable/
disable experience.

---

## 3. Current state and what needs correcting

### 3.1 Current C# state

The C# Roslyn Parser and Completion are currently registered in `AvalonEdit.AddIn.addin`:

```xml
<Parser id="C#-Roslyn" ... />
<CodeCompletionBinding id="C#-Roslyn" extensions=".cs" ... />
```

This means C# Parser and Completion keep working even after `CSharpBinding` is disabled.

These registrations should move back into a modernized `CSharpBinding.addin`.

### 3.2 Current VB state

The VB Roslyn Parser and Completion are already registered by:

```text
src/AddIns/BackendBindings/VBBinding/Project/VBBinding.addin
```

as:

```xml
<Parser id="VB-Roslyn" ... />
<CodeCompletionBinding id="VB-Roslyn" extensions=".vb" ... />
```

This ownership direction is correct.

But the shared `RoslynWorkspaceHelper` still hardcodes the language decision based on `.csproj`
and `.vbproj`. Even with the language AddIn disabled, the shared Workspace can still recognize and
load that language's projects.

So both C# and VB need to move to a language-participant registration mechanism.

---

## 4. Shared Roslyn infrastructure

The current `RoslynWorkspaceHelper` should gradually evolve into an instantiated service, e.g.:

```csharp
public interface IRoslynWorkspaceService
{
    IDisposable RegisterLanguage(IRoslynLanguageParticipant participant);

    Solution CurrentSolution { get; }

    Document? FindDocument(
        string filePath,
        string? liveText = null);

    void InvalidateProject(IProject project);
}
```

The language-participant interface:

```csharp
public interface IRoslynLanguageParticipant
{
    string LanguageName { get; }

    IReadOnlyCollection<string> ProjectExtensions { get; }

    IReadOnlyCollection<string> SourceExtensions { get; }

    ParseOptions CreateParseOptions(
        LanguageServiceProjectSnapshot snapshot);

    CompilationOptions CreateCompilationOptions(
        LanguageServiceProjectSnapshot snapshot);

    bool CanLoadProject(IProject project);
}
```

C# Binding registers on load:

```csharp
registration = workspace.RegisterLanguage(
    new CSharpLanguageParticipant());
```

VB Binding registers on load:

```csharp
registration = workspace.RegisterLanguage(
    new VisualBasicLanguageParticipant());
```

The registration is released when the AddIn is disabled/unloaded:

```csharp
registration.Dispose();
```

The Workspace Service should then:

1. Remove that language's Roslyn Projects.
2. Clear the live buffer overrides for its documents.
3. Cancel any in-flight classification/diagnostic/completion requests for that language.
4. Notify open editors to re-query for a language provider.
5. Stop accepting new projects and documents for that language.

---

## 5. Responsibilities of the AvalonEdit AddIn

The AvalonEdit AddIn should only provide language-agnostic editor capabilities:

```text
Basic syntax highlighting
Completion Window
Signature Help Window
Quick Actions UI
Semantic span renderer
Diagnostic marker renderer
Folding manager
Snippet engine
Text editor lifecycle
Language extension-point discovery
```

It should not contain logic like:

```csharp
if (extension == ".cs")
    AttachRoslynCSharpFeatures();

if (extension == ".vb")
    AttachRoslynVisualBasicFeatures();
```

It should instead query already-registered providers:

```csharp
semanticSession =
    SD.LanguageFeatureService
      .GetSemanticHighlightingProvider(fileName)
      ?.Attach(editor, textView);
```

The following generic extension points are recommended:

```csharp
public interface ISemanticHighlightingProvider
{
    bool CanHandle(FileName fileName);
    IDisposable? Attach(ITextEditor editor, TextView textView);
}

public interface IFoldingProvider
{
    bool CanHandle(FileName fileName);
    Task<IReadOnlyList<FoldingRegion>> GetFoldingsAsync(
        ITextEditor editor,
        CancellationToken cancellationToken);
}

public interface ISignatureHelpProvider
{
    bool CanHandle(FileName fileName);
    Task<SignatureHelpResult?> GetSignatureHelpAsync(
        ITextEditor editor,
        CancellationToken cancellationToken);
}

public interface ILanguageDiagnosticProvider
{
    bool CanHandle(FileName fileName);
    Task<IReadOnlyList<EditorDiagnostic>> GetDiagnosticsAsync(
        ITextEditor editor,
        CancellationToken cancellationToken);
}

public interface ILanguageCodeActionProvider
{
    bool CanHandle(FileName fileName);
    Task<IReadOnlyList<EditorCodeAction>> GetActionsAsync(
        ITextEditor editor,
        TextSpan selection,
        CancellationToken cancellationToken);
}
```

These interfaces and their UI can be shared, but the provider is registered by the corresponding
Binding AddIn.

---

# Part I: CSharpBinding migration plan

## 6. Feature ownership of CSharpBinding

The modernized `CSharpBinding` should continue to own:

```text
.cs file recognition
.csproj project recognition
C# Roslyn Workspace participant
C# Parser
C# Completion
C# Signature Help
C# Semantic Highlighting
C# Caret Reference Highlighting
C# Folding
C# Formatting
C# Bracket Matching
C# Typing Assistance
C# XML Documentation Completion
C# Compiler Diagnostics
C# Analyzer and Code Fix
C# Refactoring
C# Snippet semantic elements
C# Ambience / Symbol Display
C# project property pages
C# icons and file filters
```

After disabling `CSharpBinding`, `.cs` files should still open as plain text, but must not have any
of the C#-specific features above.

## 7. Migrating C# AddIn registrations

The following registrations should move from `AvalonEdit.AddIn.addin` back into
`CSharpBinding.addin`:

```xml
<Path name="/SharpDevelop/Parser">
    <Parser id="C#-Roslyn"
            supportedfilenamepattern="\.cs$"
            projectfileextension=".csproj"
            class="CSharpBinding.Roslyn.CSharpRoslynParser" />
</Path>

<Path name="/SharpDevelop/ViewContent/TextEditor/CodeCompletion">
    <CodeCompletionBinding
        id="C#-Roslyn"
        extensions=".cs"
        class="CSharpBinding.Roslyn.CSharpCompletionBinding" />
</Path>
```

Features added afterwards should also be registered by the same AddIn:

```xml
<Path name="/SharpDevelop/ViewContent/TextEditor/SemanticHighlighting">
    <Class extensions=".cs"
           class="CSharpBinding.Editor.CSharpSemanticHighlightingProvider" />
</Path>

<Path name="/SharpDevelop/ViewContent/TextEditor/Folding">
    <Class extensions=".cs"
           class="CSharpBinding.Editor.CSharpFoldingProvider" />
</Path>

<Path name="/SharpDevelop/ViewContent/TextEditor/SignatureHelp">
    <Class extensions=".cs"
           class="CSharpBinding.Editor.CSharpSignatureHelpProvider" />
</Path>

<Path name="/SharpDevelop/ViewContent/TextEditor/Diagnostics">
    <Class extensions=".cs"
           class="CSharpBinding.CodeAnalysis.CSharpDiagnosticProvider" />
</Path>
```

## 8. C# migration strategy per feature

### 8.1 Parser and semantic model

**Current migration state:** Completion, Semantic Highlighting, Quick Info, Ctrl+Click Definition,
and class/member navigation now use `LanguageServiceRegistry`/`ILanguageService`. The legacy
`IParserService` is now the registry-backed `LanguageServiceParserAdapter`. Base/derived type and
override queries now return backend-neutral hierarchy DTOs through `ILanguageService`. Help-keyword
lookup, snippet containing-type inference, target-framework workspace refresh, and both Ctrl+Click
and command-based Go to Definition have also moved to focused common contracts.

The old NRefactory Parser, `CSharpFullParseInformation`, and the old type system are not being
revived.

Use instead:

```text
Roslyn Document
SyntaxTree
SemanticModel
Compilation
ISymbol
SymbolFinder
```

The existing `RoslynParser` can serve as a temporary compatibility adapter for legacy parser
consumers, but live semantic UI features must consume DTOs from `ILanguageService`; Roslyn types
stay inside the C#/VB backend.

### 8.2 Completion

**Current status: the unified contract entry point is done.** `RoslynCodeCompletionBinding` is now
only responsible for AvalonEdit's trigger handling and window adaptation; document sync and
completion queries go through the `ILanguageService` selected by `LanguageServiceRegistry`, and no
longer call `RoslynWorkspaceHelper`/the Roslyn `CompletionService` directly.

Continue using:

```csharp
CompletionService.GetCompletionsAsync(...)
CompletionService.GetChangeAsync(...)
```

Do not port the old ones one by one:

```text
TypeCompletionData
EntityCompletionData
OverrideCompletionData
ImportCompletionData
EnumMemberCompletionData
```

Focus the migration effort on UI adaptation instead:

```text
Trigger characters
Commit characters
Sorting
Filtering
Icons
Async cancellation
Document version validation
Automatic using
```

### 8.3 Signature Help

Implement as an independent provider, not mixed into Completion's lifecycle.

Prefer public Roslyn services; where a given version lacks a stable public entry point, fall back
to:

```text
InvocationExpressionSyntax
ObjectCreationExpressionSyntax
ElementAccessExpressionSyntax
GenericNameSyntax
SemanticModel.GetSymbolInfo
SemanticModel.GetMemberGroup
```

### 8.4 Semantic Highlighting

**Current status: the first unified-contract migration slice is done.** CSharpBinding registers
`CSharpVBLanguageService` for `.cs`; AvalonEdit obtains the service through
`LanguageServiceRegistry` and calls `ILanguageService.GetSemanticTokensAsync`. The old
`RoslynWorkspaceHelper.GetSemanticTokens` is only a transitional implementation and is no longer
the editor's semantic-highlighting entry point.

AvalonEdit's C# `.xshd` continues to handle basic syntax highlighting.

The Roslyn semantic layer only handles symbol classifications AvalonEdit can't tell apart on its
own, e.g.:

```text
class name
record class name
struct name
record struct name
interface name
delegate name
enum name
type parameter name
method name
extension method name
field name
enum member name
constant name
property name
event name
parameter name
local name
namespace name
```

Phase 1 may restore just the old SharpDevelop's four groups:

```text
ReferenceTypes
ValueTypes
MethodCall
FieldAccess
```

Plain syntax classifications Roslyn returns must be filtered out, to avoid re-coloring
keyword/string/comment/number/punctuation.

### 8.5 Caret Reference Highlighting

Independent of Semantic Highlighting.

When the caret moves, only look up references to the same symbol within the current document.
Solution-wide `SymbolFinder.FindReferencesAsync` is only for an explicit Find All References.

### 8.6 Folding

Use the C# SyntaxTree to generate folding regions:

```text
namespace
type
member body
accessor
#region
documentation comment
multiline comment
```

Statement-level block folding should be optional, to avoid a cluttered UI.

### 8.7 Formatting

**Current status:** the existing Reformat command now calls `ILanguageService.FormatAsync` through
the registry. A non-empty selection uses range formatting; otherwise it formats the full document.
Returned `TextEdit` DTOs are applied from the end of the document toward the beginning. Languages
without a registered service retain the legacy `IFormattingStrategy` fallback.

Defer to the Roslyn Formatter and `.editorconfig`:

```text
Format Document
Format Selection
Local formatting while typing
```

The old `CSharpFormattingOptionsContainer` is no longer the source of truth for configuration.

The old project-level, solution-level, and global formatting UI may be kept, but its backing store
should read/write `.editorconfig` or a unified Roslyn option abstraction.

### 8.8 Diagnostics, Analyzers, and Code Fix

Split into three layers:

```text
Compiler diagnostics
DiagnosticAnalyzer diagnostics
OpenDevelop IDE-only diagnostics
```

Do not migrate the hundreds of old NRefactory IssueProviders.

Build a shared Analyzer Host, with CSharpBinding registering the C# execution entry point.
Support:

```text
Project AnalyzerReference
NuGet analyzer packages
SDK built-in analyzers
OpenDevelop's own analyzers
```

Code Fix uses the public `CodeFixProvider` API, executed through OpenDevelop's own provider host,
without depending on Roslyn's internal Visual Studio services.

### 8.9 Refactoring and code generation

**Current status: Find References is unified.** The command calls
`ILanguageService.FindReferencesAsync`; C#/VB implement it with Roslyn `SymbolFinder`, while LSP
languages use `textDocument/references`. Only backend-neutral symbol names and navigation ranges
cross into the Search Results UI. Rename and the remaining refactorings still require migration.

Prioritize keeping the high-value operations:

```text
Rename
Find References
Find Base
Find Derived
Extract Interface
Generate Constructor
Generate Properties
Implement Interface
Override Members
Override ToString
Override Equals/GetHashCode
Move Type to File
```

Use:

```text
Renamer
SymbolFinder
SyntaxGenerator
DocumentEditor
SyntaxEditor
SyntaxFactory
Formatter.Annotation
Simplifier.Annotation
```

Do not revive the old `EditorScript`, string-concatenation-based code generation, or the full
NRefactory Context Action system.

### 8.10 XML Documentation Completion

Use the C# SyntaxTree to determine documentation trivia, and provide:

```text
summary
remarks
param
typeparam
returns
exception
see
seealso
inheritdoc
```

`param` and `typeparam` are generated from the currently-declared symbol.

### 8.11 Ambience

Use:

```csharp
ISymbol.ToDisplayString(SymbolDisplayFormat)
```

to prepare different formats for Completion, Tooltip, Navigation, Class Browser, and full
signatures.

### 8.12 Project system and property pages

`CSharpProjectBinding` only keeps the project-type declaration and AddIn registration.

Project loading, references, target framework, and Compile items are handled by the shared
MSBuild/CPS project system.

C# property pages read/write through the generic Project Property Store:

```text
LangVersion
Nullable
AllowUnsafeBlocks
DefineConstants
Optimize
WarningLevel
TreatWarningsAsErrors
NoWarn
WarningsAsErrors
DocumentationFile
CheckForOverflowUnderflow
ImplicitUsings
```

### 8.13 AssemblyInfo

SDK-style projects should prefer modifying MSBuild properties.

Only legacy-style projects should use Roslyn to modify assembly attributes in `AssemblyInfo.cs`.

### 8.14 WinForms Designer

Keep the source for now but exclude it from compilation.

Also remove CSharpBinding's forced preload dependency on FormsDesigner.

The dependency direction going forward should be:

```text
FormsDesigner AddIn
    depends on CSharpBinding
```

rather than CSharpBinding being forced to load the Designer just for ordinary C# editing.

---

# Part II: VBBinding migration plan

## 9. VBBinding's target boundary

`VBBinding` should have the same full language ownership as `CSharpBinding`, rather than being
responsible for just `.vbproj` and Build Options.

Ultimately it should own:

```text
.vb file recognition
.vbproj project recognition
VB Roslyn Workspace participant
VB Parser
VB Completion
VB Signature Help
VB Semantic Highlighting
VB Caret Reference Highlighting
VB Folding
VB Formatting
VB Typing Assistance
VB XML Documentation Completion
VB Compiler Diagnostics
VB Analyzers and Code Fix
VB Refactoring
VB Symbol Display
VB project property pages
VB Project Imports
VB icons and file filters
```

After disabling `VBBinding`, `.vb` files should open as plain text only.

## 10. Preserve VB's currently-correct registration ownership

`VBBinding.addin` already registers:

```xml
<Parser id="VB-Roslyn" ... />
<CodeCompletionBinding id="VB-Roslyn" extensions=".vb" ... />
```

This part should stay inside VBBinding.

But the class names could move from the overly generic:

```text
RoslynParser
RoslynCodeCompletionBinding
```

to more explicit language adapters:

```text
VisualBasicRoslynParser
VisualBasicCompletionBinding
```

while still delegating internally to the shared implementation.

This lets the AddIn registrations and diagnostic logs clearly express language ownership.

## 11. VisualBasicLanguageParticipant

VBBinding registers on load:

```csharp
public sealed class VisualBasicLanguageParticipant
    : IRoslynLanguageParticipant
{
    public string LanguageName => LanguageNames.VisualBasic;

    public IReadOnlyCollection<string> ProjectExtensions
        => new[] { ".vbproj" };

    public IReadOnlyCollection<string> SourceExtensions
        => new[] { ".vb" };

    public ParseOptions CreateParseOptions(
        LanguageServiceProjectSnapshot snapshot)
    {
        return new VisualBasicParseOptions(
            languageVersion: ParseLanguageVersion(snapshot.LanguageVersion),
            preprocessorSymbols: ParseConstants(snapshot));
    }

    public CompilationOptions CreateCompilationOptions(
        LanguageServiceProjectSnapshot snapshot)
    {
        return new VisualBasicCompilationOptions(
            outputKind: snapshot.OutputKind,
            rootNamespace: snapshot.RootNamespace,
            optionStrict: snapshot.OptionStrict,
            optionInfer: snapshot.OptionInfer,
            optionExplicit: snapshot.OptionExplicit,
            optionCompareText: snapshot.OptionCompareText);
    }
}
```

VB's Workspace configuration can't just copy C#'s - it needs to correctly handle:

```text
RootNamespace
Option Strict
Option Infer
Option Explicit
Option Compare
DefineConstants' name/value form
MyType
VBRuntime
Imports
```

## 12. VB Completion

Use the Roslyn `CompletionService`, sharing the AvalonEdit Completion UI and generic adapter.

Needs focused testing on VB-specific scenarios:

```text
Automatic Imports insertion
Handles
Implements
WithEvents
My namespace
XML literals
Named arguments
Object initializers
Collection initializers
Query syntax
Line continuation
```

The same Completion Host can be shared, but provider registration, trigger rules, and behavior
configuration belong to VBBinding.

## 13. VB Signature Help

Covers:

```text
Method invocation
Constructor invocation
Default property
Attribute constructor
Generic Of clauses
Delegate invocation
RaiseEvent
```

VB's generic syntax is:

```vb
Method(Of T1, T2)(...)
```

so trigger and active-parameter resolution can't reuse C#'s angle-bracket logic.

## 14. VB Semantic Highlighting

**Current status: the first unified-contract migration slice is done.** VBBinding owns the
language-service registration for `.vb`; the renderer shares the unified contract and the
four-group color mapping with C#, but registration lifecycle is still controlled by each Binding
independently.

AvalonEdit's basic VB `.xshd` handles keyword, string, comment, number, and operator.

The Roslyn semantic layer only covers symbol classifications.

The classification-to-theme-color mapping can be shared with C#:

```text
ClassName          -> ReferenceTypes
StructureName      -> ValueTypes
InterfaceName      -> ReferenceTypes
ModuleName         -> ReferenceTypes or its own Module style
EnumName           -> ValueTypes
DelegateName       -> ReferenceTypes
MethodName         -> MethodCall
ExtensionMethodName-> MethodCall
FieldName          -> FieldAccess
EnumMemberName     -> FieldAccess
PropertyName       -> Property
EventName          -> Event
ParameterName      -> Parameter
LocalName          -> Local
NamespaceName      -> Namespace
```

But VB needs extra confirmation for:

```text
Module
My namespace
default property
WithEvents field
event handler method
XML literal names
```

Roslyn Classification already covers most symbol classifications; XML literals can continue to be
handled by basic syntax highlighting or Roslyn embedded classifications, but must not be
double-colored with AvalonEdit.

## 15. VB Folding

Use the Visual Basic SyntaxTree.

Main folding nodes:

```text
NamespaceBlockSyntax
ClassBlockSyntax
StructureBlockSyntax
InterfaceBlockSyntax
ModuleBlockSyntax
EnumBlockSyntax
MethodBlockSyntax
ConstructorBlockSyntax
PropertyBlockSyntax
EventBlockSyntax
AccessorBlockSyntax
MultiLineIfBlockSyntax
SelectBlockSyntax
TryBlockSyntax
WithBlockSyntax
SyncLockBlockSyntax
UsingBlockSyntax
RegionDirectiveTriviaSyntax
DocumentationCommentTriviaSyntax
```

Default folding should prioritize namespace, type, and member. Control-flow block folding can be a
configurable opt-in.

## 16. VB Formatting

**Current status:** VB uses the same registry-backed Format Selection/Document command as C#; the
selected `.vb` service supplies Roslyn formatting edits, while registration ownership remains in
VBBinding.

Use the Roslyn Formatter, with `.editorconfig` as the preferred configuration source.

Needs to handle VB-specific options and style:

```text
Keyword casing conventions
Line continuation rules
Colon-separated statements
Imports sorting
XML literal indentation
Query expression indentation
With block indentation
Single-line vs. multi-line If
```

The old `VBBinding.OptionPanels.TextEditorOptions` is currently excluded; it can be redesigned once
the new Roslyn formatting foundation is in place, rather than reviving the old implementation.

## 17. VB Diagnostics and Analyzers

Share the Analyzer Host with C#, with VBBinding registering the VB execution entry point.

Data sources:

```text
Visual Basic compiler diagnostics
DiagnosticAnalyzers that support VB
OpenDevelop VB-specific diagnostics
```

Must respect an analyzer's `SupportedDiagnostics` and language declaration - don't force a
C#-only analyzer onto a VB Compilation.

## 18. VB Code Fix and Refactoring

Prioritize high-value operations Roslyn already has or that are easy to implement:

```text
Rename
Find References
Go to Definition
Find Base
Find Derived
Implement Interface
Generate Constructor
Generate Property
Override Members
Encapsulate Field
Move Type to File
Remove or Sort Imports
Add Missing Imports
```

VB code generation must use `SyntaxGenerator` or the Visual Basic SyntaxFactory - not generate C#
first and then convert.

VB's `Handles`, `Implements`, default property, Module, and My namespace need dedicated testing.

## 19. VB XML Documentation Completion

Support:

```text
summary
remarks
param
typeparam
returns
exception
see
seealso
inheritdoc
```

`param` and `typeparam` come from the VB declaration symbol.

Needs to recognize VB documentation comment trivia:

```vb
''' <summary>
''' ...
''' </summary>
```

## 20. VB Project Imports

The old AddIn's `ProjectImports` property page is commented out, but this is a valuable feature for
VB projects.

It's recommended to restore it as a modern project property page, reading/writing directly:

```xml
<ItemGroup>
  <Import Include="System" />
  <Import Include="System.Collections.Generic" />
</ItemGroup>
```

or whatever VB imports representation the project system actually uses.

The UI should support:

```text
Viewing the currently effective Imports
Adding and removing a project-level Import
Distinguishing explicit project configuration from an SDK's implicit Import
Validating whether a namespace or type exists
```

This feature should belong to VBBinding, not the generic project-system UI.

## 21. VB Build Options

The existing `VBBinding.OptionPanels.BuildOptions` can keep its UI concept, but the backing store
should switch to the generic Project Property Store.

VB-specific properties:

```text
RootNamespace
OptionStrict
OptionInfer
OptionExplicit
OptionCompare
DefineConstants
MyType
VBRuntime
StartupObject
RemoveIntegerChecks
```

Generic properties:

```text
OutputType
AssemblyName
TargetFramework
Optimize
DebugType
PlatformTarget
TreatWarningsAsErrors
NoWarn
WarningsAsErrors
DocumentationFile
```

## 22. VbcEncodingFixingLogger

Needs re-evaluating whether `VbcEncodingFixingLogger` is still necessary under modern MSBuild and
the current Roslyn compiler.

Migration steps:

1. Identify the specific historical encoding issue it corrects.
2. Try to reproduce it against a modern SDK-style VB project and the current `vbc` task.
3. If the issue no longer exists, remove the logger filter.
4. If it still exists, scope the behavior to only the legacy project types/compiler versions
   actually affected.
5. Add integration tests, to avoid unconditionally altering modern compiler output.

Don't keep it by default just because it existed in the old AddIn.

## 23. VB Project Binding

`VBProjectBinding` keeps the AddIn registration and the compatibility GUID:

```text
{F184B08F-C81C-45F6-A57F-5ABD9991F28F}
```

but no longer separately implements project evaluation, reference resolution, or file
enumeration.

Those capabilities are provided by the shared MSBuild/CPS project system.

VBBinding is responsible for declaring:

```text
.vbproj
.vb
VB language identity
VB project icon
VB-specific property pages
VisualBasicLanguageParticipant
```

---

# Part III: Sharing and isolation between C# and VB

## 24. What can be shared

```text
Roslyn Workspace management
Document live-text synchronization
ProjectReference and MetadataReference synchronization
Analyzer loading and execution
CodeFixProvider host
CodeRefactoringProvider host
Completion Window adapter
Signature Help UI
Semantic span renderer
Diagnostic marker renderer
Quick Actions UI
CodeAction application
Document version and cancellation management
Symbol icon mapping
Basic SymbolDisplay helper
```

## 25. What must stay language-specific

```text
AddIn registration
Workspace language participant
ParseOptions
CompilationOptions
Completion trigger details
Signature Help syntax positioning
Typing assistance
Folding syntax walker
XML doc trivia detection
Formatting options and property pages
Project-specific properties
Code-generation syntax details
Language-specific diagnostics
```

## 26. Behavior a shared service must not perform automatically

The shared layer must not:

```text
Auto-activate C# based on .cs
Auto-activate VB based on .vb
Auto-create a C# Roslyn Project based on .csproj
Auto-create a VB Roslyn Project based on .vbproj
Scan and run every language's analyzers
Attach a Roslyn colorizer to every AvalonEdit instance
```

The shared layer may only react to the explicit registration of an already-loaded Binding AddIn.

---

## 27. Suggested directory layout

```text
src/Main/Base/Project/Roslyn/
    RoslynWorkspaceService.cs
    RoslynDocumentSynchronizer.cs
    RoslynProjectSynchronizer.cs
    RoslynAnalyzerHost.cs
    RoslynCodeActionHost.cs
    RoslynSymbolIconService.cs
    IRoslynLanguageParticipant.cs

src/AddIns/DisplayBindings/AvalonEdit.AddIn/
    LanguageFeatures/
        ISemanticHighlightingProvider.cs
        IFoldingProvider.cs
        ISignatureHelpProvider.cs
        ILanguageDiagnosticProvider.cs
        ILanguageCodeActionProvider.cs
    Rendering/
        SemanticHighlightingRenderer.cs
        DiagnosticMarkerRenderer.cs
    UI/
        SignatureHelpWindow.cs
        QuickActionsWindow.cs

src/AddIns/BackendBindings/CSharpBinding/
    Project/
        CSharpBinding.csproj
        CSharpBinding.addin
    Roslyn/
        CSharpLanguageParticipant.cs
        CSharpRoslynParser.cs
        CSharpCompletionBinding.cs
    Editor/
        CSharpSemanticHighlightingProvider.cs
        CSharpSignatureHelpProvider.cs
        CSharpFoldingProvider.cs
        CSharpReferenceHighlighter.cs
        CSharpTypingHandler.cs
        CSharpXmlDocCompletionProvider.cs
        CSharpBracketSearcher.cs
    CodeAnalysis/
        CSharpDiagnosticProvider.cs
        CSharpCodeActionProvider.cs
    Refactoring/
        ...
    OptionPanels/
        ...

src/AddIns/BackendBindings/VBBinding/
    Project/
        VBBinding.vbproj
        VBBinding.addin
    Roslyn/
        VisualBasicLanguageParticipant.vb
        VisualBasicRoslynParser.vb
        VisualBasicCompletionBinding.vb
    Editor/
        VisualBasicSemanticHighlightingProvider.vb
        VisualBasicSignatureHelpProvider.vb
        VisualBasicFoldingProvider.vb
        VisualBasicReferenceHighlighter.vb
        VisualBasicTypingHandler.vb
        VisualBasicXmlDocCompletionProvider.vb
    CodeAnalysis/
        VisualBasicDiagnosticProvider.vb
        VisualBasicCodeActionProvider.vb
    Refactoring/
        ...
    OptionPanels/
        BuildOptions.xaml
        ProjectImports.xaml
        FormattingOptions.xaml
```

Implementation classes don't have to be written in the corresponding language. VBBinding can keep
using VB, or gradually switch to C# - what matters is assembly and AddIn ownership, not source
language.

---

## 28. Expected behavior when an AddIn is disabled

### Disabling CSharpBinding

```text
.cs files can still be opened as plain text
.csproj is no longer registered as a C# project type by any Binding
C# Roslyn Projects are removed from the Workspace
C# Completion disappears
C# Signature Help disappears
C# Semantic Highlighting disappears
C# Diagnostics and Quick Actions stop
C# Refactoring menus and commands disappear
C# project property pages disappear
Open editors detach their C# controllers
```

### Disabling VBBinding

```text
.vb files can still be opened as plain text
.vbproj is no longer registered as a VB project type by any Binding
VB Roslyn Projects are removed from the Workspace
VB Completion disappears
VB Signature Help disappears
VB Semantic Highlighting disappears
VB Diagnostics and Quick Actions stop
VB Refactoring menus and commands disappear
VB project property pages and Project Imports disappear
Open editors detach their VB controllers
```

### What may remain after disabling

```text
Generic AvalonEdit text editing
Generic lexical highlighting, depending on where SyntaxMode ownership lands
Language-agnostic features: search, replace, undo, bookmarks, etc.
Generic MSBuild project-system infrastructure
Roslyn base libraries that are simply never called
```

If the intent is for a language's lexical highlighting to also disappear once its Binding is
disabled, then that SyntaxMode registration must also live in the Binding AddIn, not in the
AvalonEdit core.

---

## 29. Migration phases

### Phase 0: Fix the ownership boundary

1. Move the C# Parser and Completion registrations from `AvalonEdit.AddIn.addin` back into
   `CSharpBinding.addin`.
2. Keep the VB Parser and Completion in `VBBinding.addin`.
3. Replace `RoslynWorkspaceHelper`'s C#/VB hardcoding with `IRoslynLanguageParticipant`.
4. Add provider de-registration and editor detach for AddIn unload/disable.
5. Confirm that no language-specific background work keeps running after disabling a Binding.

### Phase 1: The everyday editing loop

C# and VB move forward together:

```text
Completion refinement
Signature Help
Semantic Highlighting
Caret Reference Highlighting
Folding
Format Document
Format Selection
XML Documentation Completion
```

C# can be finished first and its shared UI/host reused for VB, but the VB provider must be
registered and tested separately.

### Phase 2: Diagnostics and Quick Actions

```text
Compiler diagnostics
Analyzer host
Problems pad integration
Diagnostic markers
CodeFixProvider host
CodeRefactoringProvider host
Quick Actions UI
```

### Phase 3: High-value refactorings

C#:

```text
Generate Constructor
Generate Properties
Implement Interface
Override Members
Equals/GetHashCode
ToString
Move Type to File
```

VB:

```text
Generate Constructor
Generate Property
Implement Interface
Override Members
Encapsulate Field
Remove/Sort Imports
Move Type to File
```

### Phase 4: Project properties and configuration

C#:

```text
Build Options
LangVersion
Nullable
Warnings
.editorconfig
AssemblyInfo
```

VB:

```text
Build Options
RootNamespace
Option Strict/Infer/Explicit/Compare
Project Imports
MyType
VBRuntime
Warnings
.editorconfig
```

### Phase 5: Deferred items

```text
WinForms Designer
VB → C# project conversion
Low-value old NRefactory issues
Historical compatibility-logic cleanup
Complex analyzer marketplace management
```

---

## 30. Test plan

### AddIn lifecycle tests

Every language needs tests for:

```text
Binding enabled at startup
Binding disabled at startup
Enabling the Binding while running
Disabling the Binding while running
Disabling while a source file is open
Disabling while a completion/diagnostic request is in flight
Feature recovers after re-enabling
```

### C# feature tests

```text
Completion and automatic using
Signature Help
Semantic classification layered over AvalonEdit lexical highlighting
Formatting and .editorconfig
Compiler diagnostics
Analyzer diagnostics
Code Fix
Rename and Find References
Multi-project ProjectReference
Unsaved documents
```

### VB feature tests

```text
Completion and Imports
Signature Help's Of generic syntax
Semantic classification
RootNamespace
Option Strict/Infer/Explicit/Compare
Project Imports
Handles and Implements
My namespace
XML literals
Compiler diagnostics
Analyzer diagnostics
Code Fix
Multi-project ProjectReference
Unsaved documents
```

### Shared-service isolation tests

```text
No VB project is loaded when only CSharpBinding is enabled
No C# project is loaded when only VBBinding is enabled
The Workspace has no C#/VB project when both Bindings are disabled
Unloading one language doesn't affect the other language's Roslyn project
C# and VB project references work correctly within the same solution
```

---

## 31. Definition of done

CSharpBinding is done when:

```text
Every C#-specific feature is registered by the CSharpBinding AddIn
Nothing C#-specific remains after disabling CSharpBinding
No dependency on NRefactory
Everyday editing, diagnostics, formatting, and high-value refactoring are all provided by Roslyn
WinForms Designer can be deferred independently
```

VBBinding is done when:

```text
Every VB-specific feature is registered by the VBBinding AddIn
Nothing VB-specific remains after disabling VBBinding
Parser, Completion, Highlighting, Diagnostics, and Formatting are all provided by Roslyn
VB project-level compilation options and Project Imports are correctly supported
Shared Roslyn infrastructure doesn't come at the cost of AddIn lifecycle isolation
```

---

## 32. Final principle

The migration target is user-facing capability, not the old NRefactory classes.

But the final owner of a language capability must still be its corresponding Binding AddIn:

```text
Shared infrastructure can be reused
Language registration must not drift
Disabling an AddIn must take full effect
C# and VB should keep a symmetric lifecycle model
```

This achieves a modern Roslyn architecture, less duplicated code, and full control over the
original SharpDevelop/OpenDevelop AddIn model, all at the same time.
