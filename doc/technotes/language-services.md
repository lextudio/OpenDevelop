# Language Services Architecture

OpenDevelop and UnoDevelop should share language-service capabilities through IDE-level contracts, not by letting UI code talk directly to Roslyn/LSP or by reviving the old SharpDevelop parser stack.

The important separation is:

1. UI host layer
2. IDE semantic service layer
3. Backend implementation layer

## Layers

### 1. UI Host Layer

The UI host is either WPF OpenDevelop or UnoDevelop. This layer owns editor controls, pads, commands, menus, tooltips, navigation UI, and host-specific threading or windowing behavior.

It should not directly depend on Roslyn or LSP objects. It also should not use old parser implementation details such as `IParser`, `ParserDescriptor`, `ParserServiceEntry`, or `AssemblyParserService`.

Host code should call shared IDE-level contracts and render the returned DTOs.

### 2. IDE Semantic Service Layer

This is the shared compatibility and product contract layer. It lives in OpenDevelop Base and is linked by UnoDevelop.

Canonical APIs include:

- `ILanguageService`
- `LanguageServiceRegistry`
- `DocumentId`
- `CompletionResult`
- `QuickInfo`
- `LanguageDiagnostic`
- `NavigationTarget`
- `TextEdit`
- `CodeActionInfo`

`IParserService` remains only as a legacy compatibility facade for old SharpDevelop-era callers. New features should not be added to `IParserService`; they should be added to `ILanguageService` or another focused IDE semantic contract.

### 3. Backend Implementation Layer

Backends implement the IDE semantic contracts:

- C# and Visual Basic: `CSharpVBLanguageService` backed by Roslyn.
- Other languages: `LspLanguageService` backed by an LSP server.
- Fallback: `NoOpLanguageService`.

Backend-specific objects such as Roslyn `Document`, Roslyn `ISymbol`, LSP protocol objects, and workspace internals must stay behind the shared IDE contracts.

## Compatibility Facade

The legacy direction is:

```text
WPF / Uno UI
    -> shared IDE contracts
        -> Roslyn / LSP / NoOp
```

For old code that still calls `SD.ParserService`, the compatibility direction is:

```text
IParserService
    -> LanguageServiceRegistry
        -> ILanguageService
            -> Roslyn / LSP / NoOp
```

`IParserService` should therefore behave as a language-service adapter, not as the real parser backend.

Expected responsibilities:

- `Parse*` synchronizes the document text to `ILanguageService.UpsertDocumentAsync`.
- `Parse*` maintains legacy `ParseInformation` and `IUnresolvedFile` caches for old listeners.
- `AddOwnerProject`, `RemoveOwnerProject`, and `RegisterUnresolvedFile` keep `ProjectContentContainer` and parse update events working.
- `GetCompilation*` returns a safe compatibility compilation for old code paths.
- `ResolveContext` returns an `UnknownCodeContext` fallback when no richer Roslyn-backed context is available.
- `Resolve*` should gradually migrate to semantic APIs such as navigation, quick info, or a future symbol API; it should not revive NRefactory parser behavior.

The compatibility service is named `LanguageServiceParserAdapter` to make its role explicit: it adapts legacy `IParserService` callers to modern `ILanguageService` implementations.

## Do Not Revive The Old Parser Stack

The old `SharpDevelop/Parser` stack depends on NRefactory-era concepts:

- `ParserService`
- `ParserServiceEntry`
- `ParserDescriptor`
- `AssemblyParserService`
- `CecilLoader`
- serialized project-content caches

That stack is not the desired common implementation for modern OpenDevelop/UnoDevelop. `AssemblyParserService` especially pulls toward old Cecil/NRefactory assembly loading, while the current codebase already has Roslyn and LSP language services.

When a legacy caller still requires `IParserService`, adapt it to the modern language-service layer instead of linking the old parser backend.

## Migration Rules

Prefer these moves:

- UI features use `LanguageServiceRegistry.GetService(fileName)` and call `ILanguageService`.
- Document text changes use `ILanguageService.UpsertDocumentAsync` or `OnTextChanged`.
- New semantic operations are added to `ILanguageService` as DTO-based APIs.
- Old `IParserService` calls are kept working through the adapter until the caller is migrated.
- Shared files live in OpenDevelop and are linked by UnoDevelop.

Avoid these moves:

- Do not introduce UI-specific conditionals into shared semantic contracts.
- Do not expose Roslyn or LSP types through UI-facing APIs.
- Do not add new feature surface to `IParserService`.
- Do not bring back NRefactory/Cecil just to satisfy old parser call sites.
- Do not create Uno-only parser stubs when the behavior belongs in OpenDevelop shared code.

## Current Target

The desired steady state is:

- `ILanguageService` is the primary language feature API.
- `LanguageServiceRegistry` selects Roslyn, LSP, or NoOp by file type.
- `IParserService` is a compatibility adapter over `ILanguageService`.
- `ProjectContentContainer` remains shared with OpenDevelop.
- UI hosts remain thin and host-specific.

This lets WPF OpenDevelop and UnoDevelop share IDE semantics while keeping backend implementation choices replaceable.
