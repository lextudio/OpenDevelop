# Open-Source CPS Shim — Implementation Plan

Goal: build a clean-room reimplementation of the CPS framework surface
(`Microsoft.VisualStudio.ProjectSystem.*`) so that MIT-licensed
`dotnet/project-system` code compiles and links unchanged into UnoDevelop,
replacing the current MSBuild/SharpDevelop-based Solution Explorer backend.

**Licensing red line**: reconstruct only from project-system MIT sources +
public API docs. Never decompile the closed CPS SDK assemblies.

---

## Assembly layout

```
src/Main/ProjectSystem/                          ← clean-room CPS shim
  Microsoft.VisualStudio.ProjectSystem.Shim.csproj
  Contracts/
    ProjectSystemContractAttribute.cs            ← [ProjectSystemContract] + ImportCardinality stubs
  Imaging/
    ImageMoniker.cs                              ← Microsoft.VisualStudio.Imaging.Interop stub
  Properties/
    IRule.cs / IRuleSchema.cs
    IProjectPropertiesContext.cs
    IPropertySheet.cs
    IProjectCatalogSnapshot.cs / IPropertyPagesCatalog.cs
    MetadataExtensions.cs                        ← GetStringProperty / TryGetStringProperty
  Tree/
    ProjectTreeFlags.cs                          ← core flag set (readonly struct)
    IProjectTree.cs                              ← IProjectTree / IProjectTree2 / IProjectItemTree / IProjectItemTree2
    IProjectTreePropertiesProvider.cs            ← IProjectTreePropertiesProvider + context stubs
    ProjectImageMoniker.cs                       ← (Guid, int) image key
    MutableProjectTree.cs                        ← mutable builder (IProjectTree2 / IProjectItemTree2)
    ProjectTreeExtensions.cs                     ← FindChild*, GetSelfAndDescendants, IsProjectRoot, etc.
  IProjectTreeProvider.cs
  ProjectTreeProviderBase.cs                     ← abstract base with default GetPath / FindByPath
  ProjectConfiguration.cs                        ← ProjectConfigurationSlice / ConfiguredProject / UnconfiguredProject stubs

src/Main/ProjectSystemManaged/                   ← MIT upstream files linked from externals/project-system
  Microsoft.VisualStudio.ProjectSystem.Managed.csproj
  GlobalUsings.cs                                ← global using System.Collections.Immutable
  Linked/  (all linked, not copied)
    StringComparers.cs
    Utilities/ProjectChangeDiffExtensions.cs
    ProjectTreeProviderExtensions.cs
    ProjectTreeFlagsExtensions.cs                ← SKIPPED (HasFlag(flags, Common) param incompatibility)
    Tree/Dependencies/
      DependencyTreeFlags.cs
      DiagnosticLevel.cs
      DependencyGroupType.cs
      DependencyGroupTypes.cs
      Legacy/IDependencyModel.cs
      Legacy/IDependenciesChanges.cs
      Legacy/IProjectDependenciesSubTreeProvider.cs
      Snapshot/
        IDependency.cs
        Dependency.cs
        DependencyExtensions.cs
        DependenciesSnapshot.cs
        DependenciesSnapshotSlice.cs
      Subscriptions/
        IDependencySubscriber.cs
        IDependencySliceSubscriber.cs
        ProjectItemMetadata.cs
        MSBuildDependencies/
          IMSBuildDependencyFactory.cs
          MSBuildDependency.cs
          MSBuildDependencyCollection.cs
          MSBuildDependencyFactoryBase.cs
          PackageDependencyFactory.cs
          ProjectDependencyFactory.cs
          AssemblyDependencyFactory.cs
          FrameworkDependencyFactory.cs
          SdkDependencyFactory.cs
          AnalyzerDependencyFactory.cs
          ComDependencyFactory.cs
    Tree/
      AbstractSpecialFolderProjectTreePropertiesProvider.cs
    Imaging/
      IProjectImageProvider.cs
    Properties/
      MetadataExtensions.cs                      ← SKIPPED (GetProjectItemProperties needs IProjectRuleSnapshot)

src/Main/SharpDevelop/Services/
  UnoDevelopProjectTreeProvider.cs               ← SD IProject → MutableProjectTree (bridge)
  CpsTreeConverter.cs                            ← MutableProjectTree → TreeViewNode (UI bridge)

externals/project-system/                        ← git submodule (MIT), source of Linked/ files
```

---

## Slice map

### Slice 1 — ProjectTreeFlags ✅
`readonly struct` with `ImmutableHashSet<string>` backing, case-insensitive.
`Common.*` nested static class + top-level shorthands (`Reference`, `VirtualFolder`, etc.).
`Create(ProjectTreeFlags)` identity overload + `Add(ProjectTreeFlags)` for upstream code.

### Slice 2 — Tree node interfaces ✅
`ProjectImageMoniker` · `IProjectTree` / `IProjectTree2` / `IProjectItemTree` / `IProjectItemTree2`.
Stubs: `IRule` / `IRuleSchema` · `IProjectPropertiesContext` · `IPropertySheet`.

### Slice 3 — Mutable tree builder + IProjectTreeProvider ✅
`MutableProjectTree` / `MutableProjectItemTree` — mutable builder types exposed as `IProjectTree2/IProjectItemTree2`.
`IProjectTreeProvider` interface.

### Slice 4 — ProjectTreeExtensions ✅
Shim-side extension methods: `FindChildWithCaption`, `FindChildWithFlags`, `FindByFilePath`,
`GetSelfAndDescendants`, `IsProjectRoot`, `IsIncludedInProject`, `IsMissingOnDisk`, `IsFolder`.

### Slice 5 — ProjectTreeProviderBase ✅
Abstract base with default `GetPath`, `GetAddNewItemDirectory`, `FindByPath`.

### Slice 6 — Wire to UnoDevelop project model ✅
`UnoDevelopProjectTreeProvider : ProjectTreeProviderBase` (SharpDevelop project).
Builds `MutableProjectTree` from `IProject`: References virtual folder + file-system subtree.
`CpsTreeConverter` bridges CPS `IProjectTree` → `TreeViewNode` / `SolutionExplorerNodeContext`.
`SolutionExplorerTreeBuilder.CreateProjectNode` uses CPS path when `IProject` is available;
XML fallback retained for solution items not yet loaded as `IProject`.

### Slice 7 — Dead-code cleanup in SolutionExplorerTreeBuilder ✅
Removed ~90 lines replaced by CPS path:
`BuildProjectSpecialNodes(IProject?)` + `(IReadOnlyCollection<ProjectItem>)`,
`CreateReferenceNode/ProjectReferenceNode/PackageReferenceNode` `ProjectItem` overloads,
`BuildProjectContentNodes` `project is not null` branch.

### Slice 8 — CPS contract + imaging stubs ✅
`[ProjectSystemContract]` · `ImportCardinality` · `ProjectSystemContractScope` · `ImageMoniker`.
`IProjectTreePropertiesProvider` / `IProjectTreeCustomizablePropertyContext/Values`.

### Slice 9 — ConfiguredProject surface stubs ✅
`ProjectConfigurationSlice` · `ConfiguredProject` · `UnconfiguredProject` · `ProjectConfiguration`.
`IProjectCatalogSnapshot` · `IPropertyPagesCatalog`.
`MetadataExtensions` (shim-side: `GetStringProperty` / `TryGetStringProperty`).

### Slice 10 — Managed.csproj: link upstream MIT files ✅
13 files linked from `externals/project-system` (see layout above).
Also added `Microsoft.VisualStudio.Validation` (MIT) NuGet for `Requires`/`Assumes`.

### Slice 11 — MEF attributes + DependenciesTreeBuilder ✅
New shim stubs:
- `Composition/MefAttributes.cs` — `ExportAttribute`, `ImportAttribute`, `ImportManyAttribute`, `ImportingConstructorAttribute` in `Microsoft.VisualStudio.Composition` namespace
- `Composition/OrderPrecedenceImportCollection.cs` — `OrderPrecedenceImportCollection<T>`, `IOrderPrecedenceMetadataView`, `ImportOrderPrecedenceComparer`
- `References/ReferencesProjectTreeCustomizablePropertyValues.cs` — data bag + `ContractName`; explicit interface bridge for non-nullable Icon/ExpandedIcon
- `Imaging/KnownProjectImageMonikers.cs` — stubs for `ReferenceGroup`, `Library`, `*Error`, `*Warning` monikers
- `ConfigurationGeneral.cs` — `TargetFrameworkProperty` + `TargetFrameworksProperty` string constants
- `Properties/ProjectPropertiesContext.cs` — `GetContext()` factory for `IProjectPropertiesContext`
- Added `SetProperties(…)` / `Add(IProjectTree)` / `Remove(IProjectTree)` to `IProjectTree` interface
- Implemented immutable-style mutations in `MutableProjectTree` / `MutableProjectItemTree`

New Managed files:
- `Resources.cs` — `DependenciesNodeName` string stub
- `DependencyProjectTreeExtensions.cs` — `FindChildForDependency` extension needing both assemblies
- `HashSetExtensions.cs` — `AddRange` (not in BCL HashSet)
- `GlobalUsings.cs` — added `global using Microsoft.VisualStudio.Imaging`

Linked upstream (15 files total, +2 this slice):
- `IProjectTreeOperations.cs` — linked
- `DependenciesTreeBuilder.cs` — linked

### Slice 12 — ProjectCapability / ProjectCapabilities ✅
`ProjectCapabilities.cs` stub — `CSharp`, `VB`, `HandlesOwnReload`, `PackageReferences`, `SharedAssetsProject`, etc.
Linked upstream: `ProjectCapability.cs`, `ProjectImageKey.cs`, `Order.cs`, `IProjectCapabilitiesService.cs`,
`ProjectImageProviderAggregator.cs`, `ProjectRootImageProjectTreePropertiesProvider.cs` (22 files total).

Skipped: `AppDesignerFolderProjectTreePropertiesProvider.cs` — needs `IProjectTreeSettingsProvider` + `IProjectRuleSnapshot` (CPS SDK).

MEF stubs extended: `[AppliesTo]` / `[Order(int)]` / `[Import(Type)]` overload added to shim.
Global usings added to Managed: `Microsoft.VisualStudio.Composition`, `Microsoft.VisualStudio.Imaging`.

### Slice 13 — Replace SolutionExplorerTreeBuilder fallback ✅
`SolutionExplorerTreeBuilder.CreateProjectNode` now always hands project nodes to the CPS bridge:
- loaded projects use `new UnoDevelopProjectTreeProvider(IProject, ShowAllFiles)`
- solution-file-only projects use `new UnoDevelopProjectTreeProvider(projectPath, displayName, ShowAllFiles)`

Deleted the remaining project XML/tree fallback from `SolutionExplorerTreeBuilder`, including:
- `BuildProjectSpecialNodesFromProjectFile`
- `BuildProjectContentNodes`
- local `ProjectTreeFolder` adapter and related conversion helpers

The project-path provider still reads project XML internally for unloaded projects, but that parsing now belongs to the CPS bridge instead of the UI tree builder. Live and unloaded projects share the same CPS-to-TreeView conversion path through `CpsTreeConverter`.

### Slice 14 — Centralize Uno tree flags ✅
Added `ProjectTreeFlags` constants for UnoDevelop tree semantics introduced by the CPS bridge:
- `DependenciesFolder`
- `PackagesFolder`
- `PackageReference`
- `ProjectReference`
- `LinkedFile`

Each is exposed both as a top-level property and under `ProjectTreeFlags.Common`, matching the rest of the shim surface. `UnoDevelopProjectTreeProvider` and `CpsTreeConverter` now use these constants instead of ad hoc `ProjectTreeFlags.Create("...")` calls.

### Slice 15 — Carry SharpDevelop ProjectItem bindings through CPS tree ✅
`UnoDevelopProjectTreeProvider` now attaches original SharpDevelop `ProjectItem` instances to live CPS tree nodes using an internal `ConditionalWeakTable<IProjectTree, ProjectItem>` side map. This keeps the clean-room CPS shim free of SharpDevelop-specific APIs while still preserving command context for Solution Explorer.

Live project nodes now bind:
- Reference / ProjectReference nodes
- PackageReference nodes
- included file nodes, including dependent and linked files

`CpsTreeConverter` now carries the `IProject` context through recursive conversion and restores `BoundProjectItem` from the side map when creating `SolutionExplorerNodeContext`. XML fallback nodes for unloaded projects remain unbound, as expected.

### Slice 16 — Link upstream DependencyGroupTypes ✅
Linked `DependencyGroupTypes.cs` from upstream `dotnet/project-system` after extending the clean-room stubs it needs:
- `Resources` now exposes the built-in dependency group captions (`Analyzers`, `Assemblies`, `COM`, `Frameworks`, `Packages`, `Projects`, `SDKs`)
- `KnownProjectImageMonikers` now exposes zero-value stubs for the dependency group icon monikers used by upstream code

This removes another local skip while keeping real icon resolution as a later UnoDevelop UI concern.

### Slice 17 — Link upstream Dependency model ✅
Linked upstream `Tree/Dependencies/Snapshot/Dependency.cs`, the base implementation used by built-in dependency types.

Added small clean-room support shims in `ProjectSystemManaged`:
- `PooledStringBuilder` — local `StringBuilder` wrapper matching the upstream debug-only usage pattern
- `ImmutableStringDictionary<T>` — ordinal / ordinal-ignore-case immutable dictionary factories used by upstream model code
- `Folder` rule constants — minimal schema/item/property names required for dependency browse-object properties

This unblocks the MSBuild dependency factory classes without introducing the external `Microsoft.VisualStudio.Buffers.PooledObjects` package.

### Slice 18 — Link MSBuild dependency model core ✅
Linked the upstream MSBuild dependency core:
- `IMSBuildDependencyFactory.cs`
- `MSBuildDependency.cs`
- `MSBuildDependencyFactoryBase.cs`
- `MSBuildDependencyCollection.cs`
- `ProjectChangeDiff.cs`

Added clean-room support shims:
- `IProjectChangeDescription` / `IProjectChangeSnapshot` / `IProjectChangeDiff`
- `ProjectChangeSnapshotExtensions.IsEvaluationSucceeded()`
- `ImmutableStringHashSet`
- bool metadata helpers (`TryGetBoolProperty` / `GetBoolProperty`)

This creates the base needed for upstream Package/Project/Assembly/Framework dependency factories. The concrete factory exports are still intentionally deferred because they require rule-schema constants for each MSBuild item type.

### Slice 19 — Link Package/Project dependency factories ✅
Linked the first concrete upstream MSBuild dependency factories:
- `PackageDependencyFactory.cs`
- `ProjectDependencyFactory.cs`

Added clean-room constants required by those factories:
- `PackageReference` / `ResolvedPackageReference` rule names and item types
- `ProjectReference` / `ResolvedProjectReference` rule names and item types
- `ProjectCapabilities.ProjectReferences`
- zero-value private icon monikers for NuGet and project references

These factories are now available for future dependency snapshot integration, while the current Solution Explorer bridge still owns the visible `Packages` / `References` groups.

### Slice 20 — Link Assembly/Framework/SDK dependency factories ✅
Linked more upstream MSBuild dependency factories:
- `AssemblyDependencyFactory.cs`
- `FrameworkDependencyFactory.cs`
- `SdkDependencyFactory.cs`

Added clean-room constants/stubs needed by those factories:
- `AssemblyReference` / `ResolvedAssemblyReference`
- `FrameworkReference` / `ResolvedFrameworkReference`
- `SdkReference` / `ResolvedSdkReference`
- `ProjectCapabilities.AssemblyReferences`, `WinRTReferences`, `SdkReferences`
- private icon moniker stubs for references, frameworks, and SDKs
- `Microsoft.VisualStudio.Text.LazyStringSplit` minimal enumerable adapter

The remaining concrete factories are Analyzer and COM.

### Slice 21 — Link Analyzer/COM dependency factories ✅
Linked the final concrete upstream MSBuild dependency factories:
- `AnalyzerDependencyFactory.cs`
- `ComDependencyFactory.cs`

Added clean-room constants/stubs needed by those factories:
- `AnalyzerReference` / `ResolvedAnalyzerReference`
- `ComReference` / `ResolvedCOMReference`
- `ProjectCapabilities.ComReferences`
- private icon moniker stubs for analyzer and COM dependencies
- top-level `ProjectTreeFlags.FileSystemEntity` shorthand

At this point the built-in upstream MSBuild dependency factory set is linked: Package, Project, Assembly, Framework, SDK, Analyzer, and COM.

### Slice 22 — Link dependency subscriber extension-point interfaces ✅
Linked upstream dependency subscription extension-point interfaces:
- `IDependencySubscriber.cs`
- `IDependencySliceSubscriber.cs`

Added minimal clean-room data-source contracts:
- `IProjectValueDataSource<T>`
- `IActiveConfigurationSubscriptionSource`

This establishes the public shape used by dependency subscribers without pulling in the full dataflow implementation yet. `MSBuildDependencySubscriber` and `DependenciesSnapshotProvider` are intentionally still deferred because they require the project-system dataflow base classes and active configuration subscription services.

### Slice 23 — Link legacy dependency provider interfaces ✅
Linked legacy dependency extension-point interfaces:
- `IDependenciesChanges.cs`
- `IProjectDependenciesSubTreeProvider.cs`

Added `NamedIdentity` to the clean-room shim for obsolete compatibility constructors on `DependenciesChangedEventArgs`.

`LegacyDependencySubscriber` remains deferred because it bridges those event-based providers into the newer dependency snapshot pipeline via dataflow blocks.

### Slice 24 — Link ProjectChangeDiffExtensions ✅
Linked upstream `Utilities/ProjectChangeDiffExtensions.cs`.

This adds `NormalizeRenames()` over the clean-room `IProjectChangeDiff` / linked `ProjectChangeDiff` model and completes a small helper around dependency collection change processing without pulling in dataflow runtime services.

### Slice 25 — Apply upstream dependency flags in UnoDevelop bridge ✅
Updated `UnoDevelopProjectTreeProvider` so generated Reference / ProjectReference / PackageReference nodes now carry upstream dependency semantics:
- references use `DependencyTreeFlags.ResolvedDependencyFlags`
- package references keep the Uno `PackageReference` flag and also carry resolved dependency flags
- project references use a dedicated Uno `ProjectReference` flag instead of relying on `ResolvedReference`

`CpsTreeConverter` now detects project references via `ProjectTreeFlags.Common.ProjectReference`. This avoids misclassifying ordinary resolved assembly references after adopting upstream `ResolvedDependencyFlags`, because upstream uses `ResolvedReference` for all resolved dependency nodes, not only project references.

This keeps the current UnoDevelop UI shape (`References` / `Packages`) while aligning node flags with the upstream dependency model already linked in previous slices.

### Slice 26 — Classify bridge reference nodes by dependency type ✅
Expanded `UnoDevelopProjectTreeProvider` reference handling so live and unloaded projects share dependency-type classification for:
- `Reference`
- `ProjectReference`
- `Analyzer`
- `COMReference`
- `FrameworkReference`

The unloaded XML fallback now includes Analyzer / COM / Framework reference items instead of silently ignoring them. Reference captions are normalized per item type, and flags now include browse/folder-browse semantics where the upstream dependency model exposes public flags:
- analyzers: `FileSystemEntity` + `SupportsBrowse`
- COM references: `SupportsBrowse`
- framework references: `SupportsFolderBrowse`

This keeps UnoDevelop's existing `References` folder UI while making generated nodes better match the linked MSBuild dependency factories.

### Slice 27 — Switch bridge to Dependencies root grouping ✅
Changed the current UnoDevelop CPS bridge from sibling `References` / `Packages` roots to a VS/CPS-style `Dependencies` root.

The generated tree now groups dependency leaves as:
- `Dependencies`
- `Assemblies`
- `Projects`
- `Packages`
- `Analyzers`
- `COM`
- `Frameworks`

Added `ProjectTreeFlags.DependenciesFolder` / `ProjectTreeFlags.Common.DependenciesFolder` and `SolutionExplorerNodeKind.DependenciesFolder`. The leaf nodes still use the existing `Reference`, `ProjectReference`, and `PackageReference` kinds, so existing remove/open-reference command paths keep working.

This is still a bridge-generated tree, not the full `DependenciesTreeProvider` dataflow path, but its visible shape now matches the upstream dependency model much more closely.

### Slice 28 — Attach dependency browse-object metadata ✅
Added a shim-side `SimpleRule` implementation and public dependency rule-name constants so bridge-generated dependency leaves can carry CPS browse-object identity.

`UnoDevelopProjectTreeProvider` now assigns `BrowseObjectProperties` for:
- `Reference`
- `ProjectReference`
- `PackageReference`
- `Analyzer`
- `COMReference`
- `FrameworkReference`

The rule `ItemName` stays tied to the MSBuild item include, while `Name` / `ItemType` use the matching dependency schema. This aligns bridge nodes with upstream matching code such as `FindChildForDependency`, and gives future Properties / Remove / browse commands a stable CPS-style context instead of relying only on captions or flags.

### Slice 29 — Route bridge dependency tree construction through upstream builder ✅
Added `DependencyTreeBridgeBuilder` in `ProjectSystemManaged` as a narrow public facade over linked internal upstream types:
- `Dependency`
- `DependenciesSnapshot`
- `DependenciesSnapshotSlice`
- `DependenciesTreeBuilder`
- built-in `DependencyGroupTypes`

`UnoDevelopProjectTreeProvider` now collects dependency specs from live SharpDevelop projects or unloaded project XML, passes them through the facade, then normalizes public UnoDevelop flags (`DependenciesFolder`, `PackagesFolder`, `ProjectReference`, `PackageReference`) for the existing Solution Explorer converter.

This removes the bridge's hand-built dependency group tree while preserving SharpDevelop `ProjectItem` bindings by rebinding generated dependency leaves through their browse-object `ItemName`.

### Slice 30 — Use upstream MSBuild dependency factories in the bridge ✅
Changed `DependencyTreeBridgeBuilder` so bridge dependency specs are converted through the linked upstream MSBuild dependency factories:
- `AssemblyDependencyFactory`
- `ProjectDependencyFactory`
- `PackageDependencyFactory`
- `AnalyzerDependencyFactory`
- `ComDependencyFactory`
- `FrameworkDependencyFactory`
- `SdkDependencyFactory`

This moves captions, dependency flags, group types, schema item types, and browse-object metadata closer to the real CPS dependency model. The bridge now supplies only the evaluated MSBuild item identity/path/version metadata; upstream factories produce the dependency objects that feed `DependenciesTreeBuilder`.

Added `ProjectSystemTreeProviderTests.UnloadedProjectBuildsDependenciesTreeViaCpsBridge` to lock down the unloaded-project path for Dependencies / Packages / Projects grouping and public flags consumed by the Solution Explorer converter.

### Slice 31 — Carry real MSBuild item metadata into dependency factories ✅
Expanded `DependencyBridgeItem` from fixed `Version` data to a full immutable metadata map.

Live SharpDevelop projects now pass all evaluated `ProjectItem.MetadataNames` values into the bridge. Unloaded project XML now passes both item attributes and child metadata elements, excluding only `Include`. The facade still supplies safe defaults for upstream metadata required by dependency factories (`OriginalItemSpec`, `Name`, `Visible`, `IsImplicitlyDefined`, `DefiningProjectFullPath`, `Identity`, `FullPath`), but real project metadata overrides those defaults.

The unloaded-project tree test now covers metadata-driven filtering by adding a `FrameworkReference` with `<Visible>false</Visible>` and verifying the `Frameworks` group is omitted. This proves the bridge is feeding upstream factory visibility logic instead of just shaping the tree manually.

### Slice 32 — Support target-framework slices in bridge dependency trees ✅
Extended `DependencyTreeBridgeBuilder.BuildDependenciesTree` to accept target frameworks and build one `DependenciesSnapshotSlice` per TFM. Multi-target projects now exercise the upstream `DependenciesTreeBuilder` multi-slice path, producing TFM nodes such as `net8.0` and `net9.0` below `Dependencies`.

`UnoDevelopProjectTreeProvider` now detects target frameworks from live `MSBuildBasedProject` evaluation (`TargetFrameworks` / `TargetFramework`) and from unloaded project XML. Dependency flag normalization is recursive so package/reference public flags are restored under TFM slice nodes as well as direct dependency groups.

Added `UnloadedMultiTargetProjectBuildsTargetFrameworkDependencySlices` to lock this shape down.

### Slice 33 — Filter unloaded dependencies by TargetFramework conditions ✅
Added per-dependency target-framework applicability to `DependencyBridgeItem` and filtered dependencies per `DependenciesSnapshotSlice`.

For unloaded project XML, `UnoDevelopProjectTreeProvider` now recognizes common item conditions such as:

```
Condition="'$(TargetFramework)' == 'net8.0'"
```

and applies those items only to the matching TFM slice. The multi-target test now verifies common packages appear in both slices while TFM-specific packages appear only under their matching `net8.0` / `net9.0` node.

This is intentionally a small bridge parser for common `TargetFramework == value` item conditions, not a full MSBuild condition evaluator.

### Slice 34 — Add SDKReference to the upstream dependency bridge ✅
Extended UnoDevelop dependency collection to recognize `SDKReference` in both live SharpDevelop projects and unloaded project XML.

The bridge was already capable of using upstream `SdkDependencyFactory`; this slice wires the missing provider entry points so SDK references now flow into the real CPS dependency builder and appear under the upstream `SDKs` group. Added `DependencyRuleNames.SdkReference` and expanded the unloaded-project test to cover SDK captions, group flags, and browse-object item type.

### Slice 35 — Broaden unloaded TargetFramework condition parsing ✅
Expanded the unloaded-project condition parser used for dependency applicability:

- supports both single-quoted and double-quoted comparisons
- supports reversed comparisons such as `"net9.0" == "$(TargetFramework)"`
- supports `!=` when the project target-framework list is known, mapping exclusions to all other TFM slices
- keeps `null` as "unknown/unrestricted" and an empty set as "matches no target frameworks"

This still does not attempt to be a full MSBuild condition evaluator. It only handles the common `$(TargetFramework)` equality/inequality comparisons needed to keep dependency slices close to VS for SDK-style projects.

### Slice 36 — Accumulate repeated TargetFrameworks in unloaded projects ✅
Improved unloaded-project target-framework discovery for SDK-style projects that build `TargetFrameworks` incrementally:

```
<TargetFrameworks>$(TargetFrameworks);net8.0</TargetFrameworks>
<TargetFrameworks>$(TargetFrameworks);net9.0</TargetFrameworks>
<TargetFrameworks>$(TargetFrameworks);net462</TargetFrameworks>
```

The XML fallback now scans all `TargetFrameworks` elements in document order, filters out unresolved MSBuild property tokens such as `$(TargetFrameworks)`, de-duplicates the concrete TFM values, and only falls back to singular `TargetFramework` when no plural entries are found. Added test coverage matching this pattern so unloaded dependency slices remain useful for projects like obfuscar before full MSBuild evaluation is available.

### Slice 37/38 — Dataflow value/versioning + lifecycle contracts ✅

Discovered that the dataflow/lifecycle base types needed by the deferred subscriber classes
(`OnceInitializedOnceDisposed`, `ChainedProjectValueDataSourceBase<T>`,
`ProjectValueDataSourceBase<T>`, `DataflowBlockSlim`, `IBroadcastBlock<T>`,
`IProjectVersionedValue<T>`, `IProjectSubscriptionUpdate`) live in the **closed**
`Microsoft.VisualStudio.ProjectSystem.dll` base assembly, not in the MIT `.Managed` repo we
vendor — they cannot be linked like slices 1-36. Reconstructed the subset needed by
`LegacyDependencySubscriber` as clean-room stubs, backed by the real (in-box, net10.0 shared
framework) `System.Threading.Tasks.Dataflow` types wherever possible instead of hand-rolling
dataflow semantics:

- `Dataflow/DataflowSupport.cs` — `IProjectVersionedValue<T>` / `ProjectVersionedValue<T>`,
  `Empty.ProjectValueVersions`, `IBroadcastBlock<T>` (wraps the real `BroadcastBlock<T>`),
  `SafePublicize()` (wraps a source block to hide post/complete from consumers), `DataflowBlockSlim.CreateBroadcastBlock<T>`.
- `ProjectDataSourceContracts.cs` — extended `IProjectValueDataSource<T>` with real
  `DataSourceKey`/`DataSourceVersion`/`SourceBlock` members; added `IProjectCommonServices` /
  `IUnconfiguredProjectServices` (minimal — UnoDevelop has no per-project fault-isolation or
  unload model yet) and `ProjectValueDataSourceBase<T>`.
- `Imaging/ImageMonikerExtensions.cs` — `ToProjectSystemType()` converting the VS SDK interop
  `ImageMoniker` struct to our `ProjectImageMoniker`.
- `Composition/OrderPrecedenceImportCollection.cs` — added `ToImmutableValueArray()` extension.

`IProjectChangeDescription`/`IProjectChangeSnapshot`/`IProjectChangeDiff` needed by the fuller
`MSBuildDependencySubscriber`/`DependenciesSnapshotProvider` were already present from slices
17/24/38-prep (`ProjectChanges.cs`).

### Slice 40 — Link LegacyDependencySubscriber ✅

Linked `Tree/Dependencies/Legacy/LegacyDependencySubscriber.cs` — the smallest of the three
deferred subscribers, bridging old-style `IProjectDependenciesSubTreeProvider.DependenciesChanged`
C# events into the dataflow-based `IDependencySubscriber` pipeline via a
`ProjectValueDataSourceBase<T>`-derived source. Compiles and links unchanged against the slice
37/38 stubs above.

**Not yet wired to a real `IProjectDependenciesSubTreeProvider` implementation** — UnoDevelop has
no legacy dependency provider today (e.g. no NPM/WebTools-style provider), so this subscriber has
no registered `[Export(typeof(IProjectDependenciesSubTreeProvider))]` to activate. It compiles and
is available for a future provider, but doesn't yet change runtime behavior.

### Slice 41 — Link MSBuildDependencySubscriber ✅

Linked `Tree/Dependencies/Subscriptions/MSBuildDependencySubscriber.cs`, which chains MSBuild
dependency factories onto the CPS design-time-build dataflow surface. Required reconstructing
more closed-SDK surface than slice 37/38 covered — none of it upstream MIT source, all clean-room
from public usage patterns only:

- `Dataflow/OnceInitializedOnceDisposed.cs` — thread-safe lazy-init/dispose-once base.
- `ProjectDataSourceContracts.cs` — `ChainedProjectValueDataSourceBase<T>` (owns an internal
  broadcast block; derived classes wire upstream sources into the ingestion target block supplied
  to `LinkExternalInput`, called lazily on first `SourceBlock` access). `JoinUpstreamDataSources`
  is a no-op — UnoDevelop has no JoinableTaskFactory-based join-blocking threading model to protect
  against deadlocks in.
- `Dataflow/ProjectSubscriptionContracts.cs` — `IProjectSubscriptionUpdate`, extended
  `IActiveConfigurationSubscriptionSource` with `ProjectRuleSource`/`JointRuleSource`,
  `RuleNameLinkOptions` + `DataflowOption.WithRuleNames`/`WithJointRuleNames` (rule-name filtering
  implemented internally via a real `TransformManyBlock`, since there's no faithful public
  single-predicate `LinkTo` overload to reuse), `ProjectRuleSource` (concrete, postable), and
  `Derive()` (applies a transform to a versioned value while preserving its `DataSourceVersions`).
- `Dataflow/DisposableBag.cs` — collection-initializer-friendly disposable aggregate, matching
  CPS's `Microsoft.VisualStudio.ProjectSystem.Utilities.DisposableBag` usage (`new DisposableBag { a, b, c }`).
- `Composition/OrderPrecedenceImportCollection.cs` — added `ToImmutableValueArray()`.
- Made `IProjectChangeDescription`/`IProjectChangeSnapshot`/`IProjectChangeDiff` (`ProjectChanges.cs`,
  slices 17/24) public — they were `internal` and not yet exposed across the assembly's public API.

**Not yet wired to a real design-time-build subscription** — `ProjectRuleSource`/`JointRuleSource`
compile and are structurally correct, but nothing yet posts real MSBuild evaluation/build data into
them (see slice 42/43 notes below).

### Slice 42 — Link DependenciesSnapshotProvider ✅

Linked `Tree/Dependencies/Subscriptions/DependenciesSnapshotProvider.cs`, which merges unconfigured
and per-slice configured dependency sources into one `DependenciesSnapshot`. This needed CPS's
`ProjectDataSources` sync-link framework — a "combine N independently-versioned dataflow streams
into one consistent value" primitive — plus a batch of active-configuration/threading/fault-handling
service contracts. All closed-SDK surface, all clean-room:

- `Dataflow/ProjectDataSources.cs` — `ProjectDataSources.SyncLinkTo` (two overloads: a 3-source
  heterogeneous-tuple form and an N-source homogeneous-collection form), implemented as a
  from-scratch "combine latest, merge `DataSourceVersions`" algorithm. This is **simpler than
  upstream** — no back-pressure or consistency-window gating for out-of-order versions — but
  behaviorally equivalent for UnoDevelop's single-writer-per-source usage. Also
  `ConfiguredDependencyFilterBlock` (no-op pass-through: UnoDevelop has one active configuration
  per slice, nothing to filter against yet), `UnwrapCollectionChainedProjectValueDataSource<TCollection,TItem>`
  (a `ChainedProjectValueDataSourceBase` that is *also* a real `ITargetBlock`, so it can be `LinkTo`'d
  directly or `Post`'d to), `DisposableDelegate`.
- `Dataflow/OnceInitializedOnceDisposedAsync.cs` — async lifecycle base
  (`InitializeAsync`/`InitializeCoreAsync`/`DisposeCoreAsync`).
- `Dataflow/ActiveConfigurationServices.cs` — `ConfigurationSubscriptionSources`,
  `IActiveConfigurationGroupSubscriptionService`, `IActiveConfiguredProjectProvider`,
  `IProjectThreadingService` (opaque — UnoDevelop drives its dependency pipeline from its own
  dispatcher, not a JoinableTaskFactory join/switch model), `IUnconfiguredProjectCommonServices`,
  `IUnconfiguredProjectTasksService` (no project-unload model to protect against, so
  `LoadedProjectAsync` can just invoke its delegate), `IProjectFaultHandlerService`/`ProjectFaultSeverity`.
- `LinqExtensions.cs` — CPS's own allocation-avoiding `FirstOrDefault<TSource,TArg>` overload.
- `ProjectConfigurationSlice.IsPrimaryActiveSlice(ProjectConfiguration)` — added to the existing
  shim type (`ProjectConfiguration.cs`), matches slice dimensions against the active configuration.
- Extended `IBroadcastBlock<T>` to also implement `ITargetBlock<T>` (needed once a broadcast block
  is used directly as a `LinkTo` target, not just posted to).

### Slice 43 — Wire real evaluation data through the dataflow pipeline, end-to-end ✅

Closed the gap left at the end of slice 42: nothing previously constructed a
`DependenciesSnapshotProvider`, populated its MEF-shaped extension points, or posted real
dependency data into it. This slice builds the missing translator and manual composition layer,
and proves the whole pipeline (`MSBuildDependencySubscriber` → `DependenciesSnapshotProvider` →
`ProjectDataSources.SyncLinkTo`) produces a *correct* `DependenciesSnapshot` from UnoDevelop's
existing evaluation data — not just that the types compile.

- `Bridge/DependencyBridgeSubscriptionTranslator.cs` — converts a project's
  `IReadOnlyList<DependencyBridgeItem>` (already gathered by `UnoDevelopProjectTreeProvider` from
  live MSBuild evaluation or unloaded project XML) into a full-width `IProjectSubscriptionUpdate`.
  **Key constraint discovered**: `MSBuildDependencySubscriber.Transform()` indexes
  `update.ProjectChanges[unresolvedRuleName]` directly (not `TryGetValue`), so every posted update
  must contain an entry for *all seven* registered factories' rule names, even when a dependency
  type has zero current items (with `Difference.AnyChanges = false` so
  `MSBuildDependencyCollection.TryUpdate` cheaply skips it instead of creating an empty group).
  Includes concrete `IProjectSubscriptionUpdate`/`IProjectChangeDescription`/`IProjectChangeSnapshot`/`IProjectChangeDiff`
  implementations. Reuses `DependencyTreeBridgeBuilder.GetFactory`/`MergeMetadata` (changed from
  `private` to `internal`) rather than duplicating factory-mapping/metadata-default logic.
- `Bridge/UnoDevelopActiveConfigurationSubscriptionSource.cs` — concrete per-slice
  `IActiveConfigurationSubscriptionSource`, plus `SingleValueDataSource<T>`, a data source that
  always has exactly one value. Relies on real `BroadcastBlock<T>`'s "late linkers see the last
  posted value" semantics: all data is posted *before* the lazy consumer side links to it (which
  happens deep inside `ChainedProjectValueDataSourceBase`'s first `SourceBlock` access), so no
  explicit post/link ordering or wait/retry loop is needed.
- `Bridge/UnoDevelopProjectSystemServices.cs` — concrete `IProjectThreadingService` (opaque — no
  JoinableTaskFactory model), `IUnconfiguredProjectCommonServices`, `IUnconfiguredProjectTasksService`
  (no project-unload model, so `LoadedProjectAsync` just invokes its delegate),
  `IProjectFaultHandlerService` (no-op), `IActiveConfiguredProjectProvider`,
  `IActiveConfigurationGroupSubscriptionService` (publishes a fixed, one-time set of slices —
  UnoDevelop resolves target frameworks up front rather than discovering them incrementally).
- `Dataflow/ManualComposition.cs` — since the shim's MEF attributes (slice 8/11) don't drive any
  real container/catalog, this reflects into the private `[ImportMany] OrderPrecedenceImportCollection<T>`
  fields on `MSBuildDependencySubscriber`/`DependenciesSnapshotProvider` and calls the collection's
  existing public `.Add()` — mechanical field injection, not a MEF reimplementation.
- `Bridge/UnoDevelopDependenciesSnapshotFactory.cs` — orchestrates the above into
  `BuildSnapshotAsync(projectPath, itemsByTargetFramework)`, returning a `DependenciesSnapshot` (or
  `null` on a 5s timeout, for callers to fall back to the existing bridge).

**Verified end-to-end**, not just compiled: `UnoDevelopDependenciesSnapshotFactoryTests.cs`
(`src/Tests/UnoDevelop.Core.Tests/`, gated by a new `InternalsVisibleTo` on the `.Managed` csproj)
feeds hand-built `DependencyBridgeItem`s through the real pipeline and asserts on the resulting
`DependenciesSnapshot`:
- A single-target project produces exactly the `Packages`/`Assemblies`/`Projects` groups for the
  items supplied, and *no* `Analyzers`/`SDKs`/`Frameworks`/`COM` groups — proving the
  always-present-but-often-empty rule entries are correctly skipped rather than surfacing empty
  groups or throwing.
- A multi-target (`net8.0`/`net9.0`) project produces one `DependenciesSnapshotSlice` per TFM, with
  a package present in both slices' evaluation data correctly appearing in both, and a
  TFM-exclusive package appearing in only its slice.

Both tests pass; the full existing suite (154 tests) and the downstream `SharpDevelop.csproj` build
remain green.

**Not yet done**: `UnoDevelopProjectTreeProvider`/`SolutionExplorerTreeBuilder` still call the
imperative `DependencyTreeBridgeBuilder` path for the live Solution Explorer UI — this slice proves
the real pipeline *works*, but doesn't yet *replace* the bridge, nor does anything post incremental
updates in response to `ProjectItemAdded`/`ProjectItemRemoved`/`UnoProjectChangeWatcher.ChangedExternally`
(every `BuildSnapshotAsync` call is still a fresh one-shot construction of the whole dataflow graph,
not a long-lived incrementally-updated subscription). Switching Solution Explorer over to consume
`UnoDevelopDependenciesSnapshotFactory`/a long-lived `DependenciesSnapshotProvider` instance instead
of `DependencyTreeBridgeBuilder`, and wiring those SharpDevelop events to `PostUpdate` calls on a
kept-alive `UnoDevelopActiveConfigurationSubscriptionSource`, remains a distinct follow-up slice.

### Slice 44 — Real VS MEF, not clean-room composition attributes ✅

Corrected a mistaken assumption from earlier investigation: `[Export]`/`[ImportingConstructor]`/
`[ImportMany]`/`[AppliesTo]`/`OnceInitializedOnceDisposed`/`ChainedProjectValueDataSourceBase` etc.
are **not** provided by the open-source `Microsoft.VisualStudio.Composition` ("VS MEF") package —
downloading and inspecting that package's actual exported types (17.13.41 from nuget.org) confirms
it contains only the composition **engine** (`ExportProvider`, `CompositionConfiguration`,
`AttributedPartDiscovery`, `PartDiscovery`), not attribute types. Those attributes live in the
closed `Microsoft.VisualStudio.ProjectSystem` SDK package (confirmed via
`externals/project-system/eng/imports/HostAgnostic.props`, which references
`Microsoft.VisualStudio.ProjectSystem`, `Microsoft.VisualStudio.Composition`, and
`Microsoft.VisualStudio.Threading`/`Validation` as three separate, distinct packages).

What *is* real and usable, though: `AttributedPartDiscovery` (the real VS MEF engine) discovers
parts via the standard MEF2 `System.Composition.ExportAttribute`/`ImportingConstructorAttribute`/
`ImportManyAttribute` (a real, MIT, BCL-adjacent package — `System.Composition.AttributedModel`) —
and .NET's attribute reflection (`GetCustomAttribute<T>`) matches **subclasses**. So instead of
slice 8/11's clean-room attributes deriving from plain `Attribute`, they now derive from the real
`System.Composition` base classes:

- `Composition/MefAttributes.cs` — `ExportAttribute`/`ImportAttribute`/`ImportManyAttribute` now
  subclass `System.Composition.ExportAttribute`/`ImportAttribute`/`ImportManyAttribute` directly
  (adding back CPS-convention constructor overloads like `[Import(typeof(X))]` that the real base
  doesn't have, as a genuinely-new member rather than reimplementing MEF semantics).
  `ImportingConstructorAttribute` **cannot** be subclassed this way — the real
  `System.Composition.ImportingConstructorAttribute` is `sealed`. Removed our own class entirely;
  `ProjectSystemManaged/GlobalUsings.cs` now aliases the bare name directly to the real sealed type
  (`global using ImportingConstructorAttribute = System.Composition.ImportingConstructorAttribute;`),
  so real VS MEF's constructor-selection logic recognizes it on every linked upstream file without
  editing any of them. `AppliesToAttribute`/`OrderAttribute` remain pure clean-room stubs — they're
  genuinely CPS-SDK-specific (project-capability filtering / MEF ordering), not part of
  `System.Composition` at all, and this shim still discovers all parts unconditionally rather than
  modeling per-project-capability filtering.
- Added real `PackageReference`s: `System.Composition.AttributedModel` (Shim + Managed projects),
  `Microsoft.VisualStudio.Composition` (Managed project).
- `Bridge/RealMefHost.cs` — a genuine composition host: `AttributedPartDiscovery` scans the linked
  assembly, `ComposableCatalog`/`CompositionConfiguration` build a real part graph, and
  `CreateExportProviderFactory().CreateExportProvider()` produces a real `ExportProvider`.
  `DependencyBridgeSubscriptionTranslator.AllFactories` now calls
  `RealMefHost.ExportProvider.GetExportedValues<IMSBuildDependencyFactory>()` instead of a
  hand-maintained list mirroring `DependencyTreeBridgeBuilder.GetFactory`'s switch — this reflects
  whatever `[Export(typeof(IMSBuildDependencyFactory))]` parts are actually linked, discovered for
  real rather than hardcoded.
- Pinned `MessagePack` to `2.5.302` (direct `PackageReference`, same pattern as the existing
  `Tmds.DBus.Protocol` CVE pin) — `Microsoft.VisualStudio.Composition` transitively pulls the
  vulnerable `2.5.192` for its part-graph caching serialization.

**Deliberately not attempted**: composing `MSBuildDependencySubscriber`/`DependenciesSnapshotProvider`
themselves through the real `ExportProvider`. Both need a specific per-project `UnconfiguredProject`
runtime instance injected via `[ImportingConstructor]`, which plain attributed discovery can't
supply — real CPS solves this with a hierarchical, per-project-scoped MEF composition (global scope
→ unconfigured-project scope → configured-project scope) that this shim does not model. Those two
classes still get their `[ImportMany]` fields populated via `Dataflow/ManualComposition.cs`'s
reflection-based injection (slice 43) — now composing the real-MEF-discovered `AllFactories` list,
so at least the *values* are genuinely composed, even though the *injection into these two specific
instances* is still a hand-rolled substitute for project-scoped MEF.

Verified: the slice 43 end-to-end tests (`UnoDevelopDependenciesSnapshotFactoryTests`) pass against
the real `ExportProvider`-discovered factories, and the full suite (154 tests) plus the downstream
`SharpDevelop.csproj` build remain green.

### Slice 45 — Generic attribute-driven composition glue, replacing per-field hardcoding ✅

Slice 44 established that VS MEF's public API has no primitive for injecting a specific per-call
runtime instance (like one project's `UnconfiguredProject`) into a composed part — real CPS solves
this with proprietary per-project hierarchical MEF scopes this shim doesn't model. Rather than
stopping there, this slice writes a small **generic** composition layer on top of the real engine
instead of slice 43's `ManualComposition.ImportMany(instance, "hardcoded field name", hardcoded values)`
calls:

- `Dataflow/CompositionScope.cs` — `CompositionScope` wraps a real `ExportProvider` plus a set of
  per-call instance/many-instance overrides. `Activate<T>()` finds `T`'s `[ImportingConstructor]`
  (or its sole public constructor) via reflection, resolves each parameter — override first, else
  the real `ExportProvider.GetExportedValue<T>()`/`GetExportedValues<T>()` invoked reflectively for
  the runtime `Type` (there's no public way to look up VS MEF's internal contract-name derivation
  for an arbitrary `Type` at runtime, so the real generic methods are invoked via `MakeGenericMethod`
  instead of reimplementing that logic) — constructs the instance, then finds and populates any
  `[ImportMany] OrderPrecedenceImportCollection<T>` fields the same way. This is what a real MEF
  container does when *activating* a part, just scoped to one call instead of a static catalog, and
  driven entirely by the real attribute metadata rather than field-name strings.
- Rewired `Bridge/UnoDevelopDependenciesSnapshotFactory.cs` to use `CompositionScope.Activate<T>()`
  for both `MSBuildDependencySubscriber` and `DependenciesSnapshotProvider`, registering per-project
  instances (`UnconfiguredProject`, the various `UnoDevelopThreadingService`/`UnoDevelopUnconfiguredProjectTasksService`/etc.
  from slice 43) as scope overrides, and empty/single-item collections for `IDependencySubscriber`/
  `IDependencySliceSubscriber` — needed because the assembly's *other* `[Export(typeof(IDependencySubscriber))]`
  parts (e.g. `LegacyDependencySubscriber`) also require per-project data the real `ExportProvider`
  can't supply, so resolving that collection through it directly would fail composition.
- Deleted `Dataflow/ManualComposition.cs` (slice 43) — fully superseded, no remaining callers.

Two implementation snags worth recording for future slices touching this code:
- `WithInstance<T>(value)`'s type parameter must be **explicitly specified** to match the contract
  type a constructor parameter/import expects (e.g. `WithInstance<UnconfiguredProject>(concreteInstance)`,
  not relying on C#'s type inference from a concrete subclass) — overrides are keyed by exact `Type`,
  and a param typed as the base class won't match an override keyed by a derived class.
- `Lazy<T, TMetadataView>`'s constructor requires an exactly-`Func<T>`-typed value factory; a
  `Func<object>` cast doesn't satisfy `Activator.CreateInstance`'s constructor binding. Building the
  correctly-typed delegate via `Expression.Lambda(funcType, Expression.Constant(value, contractType)).Compile()`
  resolved it.

Verified: the slice 43 end-to-end tests pass unchanged against this new activation path, and the
full suite (154 tests) plus the downstream `SharpDevelop.csproj` build remain green.

### Slice 46 — Wire the real dataflow pipeline into the live Solution Explorer UI ✅

The dataflow pipeline (slices 41-45) was proven correct in isolation, but Solution Explorer's live
tree still called the imperative `DependencyTreeBridgeBuilder` path on every refresh. This slice
switches it over, accepting the regression risk of touching working, tested UI code — mitigated by
falling back to the imperative path whenever the dataflow pipeline doesn't produce a result.

- `Bridge/UnoDevelopDependenciesSnapshotFactory.BuildTreeAsync` — new method wrapping
  `BuildSnapshotAsync`, converting the resulting `DependenciesSnapshot` into a `MutableProjectTree`
  via the *same* `DependenciesTreeBuilder`/`BridgeTreeOperations` construction
  `DependencyTreeBridgeBuilder.BuildDependenciesTree` already uses (made `BridgeTreeOperations`/
  `BridgeUnconfiguredProject`/`BridgeConfiguredProject`/`BridgeCatalogSnapshot` `internal` for reuse,
  slices 43/46) — so the result is a drop-in replacement wherever the imperative path's tree is
  consumed, and returns null (rather than throwing) if the pipeline doesn't produce a snapshot.
- `UnoDevelopProjectTreeProvider.cs` — extracted the existing item-gathering logic (live `IProject`
  evaluation or unloaded-XML parsing) out of `AddSpecialNodes` into a shared `GatherDependencyItems`,
  added `GroupItemsByTargetFramework` (buckets the flat, per-item-TFM-filtered list into the
  per-TFM-list shape `BuildTreeAsync` expects — a project with no TFMs gets a single `""` bucket),
  and added `BuildTreeAsync()`/`AddSpecialNodesAsync` alongside the existing synchronous
  `BuildTree()`/`AddSpecialNodes` (kept, unchanged, as the fallback path):
  `dependenciesNode = await UnoDevelopDependenciesSnapshotFactory.BuildTreeAsync(...) ?? DependencyTreeBridgeBuilder.BuildDependenciesTree(...)`.
- `SolutionExplorerTreeBuilder.cs` — `CreateSolutionNode`/`CreateTreeNodeFromSolutionItem`/
  `CreateProjectNode`/`CreateProjectNodeViaCps` all renamed with an `Async` suffix and converted to
  `Task<TreeViewNode>`-returning methods, awaiting `provider.BuildTreeAsync()` instead of calling
  `BuildTree()` synchronously.
- `MainPage.SolutionExplorer.cs`/`MainPage.xaml.cs` — `LoadSolutionTree`/`RefreshSolutionTree` became
  `LoadSolutionTreeAsync`/`RefreshSolutionTreeAsync`; the four SharpDevelop event handlers
  (`OnCurrentSolutionChanged`/`OnSolutionOpened`/`OnSolutionClosed`/`OnProjectItemCollectionChanged`)
  became `async void` (the only option for event-delegate-shaped handlers) awaiting the async refresh;
  the `IUnoSolutionExplorerHost.RefreshSolutionTree()` interface method and the `SolutionExplorerPad`
  attach handler use the established fire-and-forget pattern (`_ = RefreshSolutionTreeAsync();`,
  matching the Tests pad's `RefreshTests()`/`RefreshTestsAsync()` precedent) since their call sites
  can't themselves become async.
- Added `InternalsVisibleTo` from `Microsoft.VisualStudio.ProjectSystem.Managed` to `UnoDevelop`
  (the `SharpDevelop.csproj` assembly name) so `UnoDevelopProjectTreeProvider` can call the internal
  `BuildTreeAsync` directly.

**Verified with a new end-to-end test**, not just compiled: `ProjectSystemTreeProviderTests.UnloadedProjectBuildsDependenciesTreeViaRealDataflowPipeline`
runs the same project fixture as the existing bridge test through `BuildTreeAsync()` and asserts an
equivalent tree shape (`Assemblies`/`Packages`/`Projects` groups, correct captions/flags) — proving
the real dataflow pipeline now drives the live tree-construction path, not just an isolated factory
call. Full suite (155 tests) and the downstream `SharpDevelop.csproj` build remain green.

**Still not incremental** (as of slice 46): every `BuildTreeAsync()` call constructed a fresh
`DependenciesSnapshotProvider`/dataflow graph from scratch. Addressed in slice 47.

### Slice 47 — Persistent per-project dataflow sessions, with real diff tracking ✅

Replaced the "rebuild the whole dataflow graph every call" behavior with a kept-alive
`DependenciesSnapshotSession` per project path: the graph (composed parts, dataflow blocks, links)
is built once and reused across `BuildSnapshotAsync`/`BuildTreeAsync` calls — only new evaluation
data is posted into the existing graph. A session is discarded and rebuilt if a project's target
framework set changes (the graph's slice topology is fixed at construction).

- `Bridge/DependenciesSnapshotSession.cs` — owns one `DependenciesSnapshotProvider` +
  one `UnoDevelopActiveConfigurationSubscriptionSource` per TFM slice, built once in `CreateAsync`
  (calls `EnsureInitializedAsync()` but posts no data — the per-slice `MSBuildDependencySubscriber`
  output stays empty until the first real post, same as a design-time build that hasn't run).
  `RefreshAsync` posts new data and awaits the resulting snapshot.
- `UnoDevelopDependenciesSnapshotFactory` now keeps a `Dictionary<string, DependenciesSnapshotSession>`
  keyed by project path, disposing and rebuilding a session when the TFM key set no longer matches.

**Two real correctness bugs surfaced by testing repeated calls against the same session** — neither
would have been caught by the one-shot slice 43-46 tests, since a freshly-built graph has no state
to get wrong:

1. **Stale-result race.** `RefreshAsync` originally captured the *first* value emitted after
   posting — but since the dataflow graph is now a long-lived, continuously-running pipeline, a
   still-in-flight emission from a *previous* call could arrive during a later call's window and
   get mistakenly captured. Fixed by stamping a monotonically increasing generation number into
   each posted update's `DataSourceVersions` (`DependenciesSnapshotSession.GenerationKey`, real VS
   MEF's own mechanism for exactly this) and only accepting a snapshot whose merged version is at
   or past the generation just posted.
2. **Missing removals + a silent dataflow fault.** `DependencyBridgeSubscriptionTranslator.BuildUpdate`
   originally reported every call's full item set as `Difference.AddedItems` with `RemovedItems`
   always empty — correct for a one-shot build, but wrong once `MSBuildDependencyCollection`
   persists `_dependencyById` across calls: a deleted dependency would never be removed from the
   accumulated snapshot. Fixed by tracking real prior-item-metadata per rule name
   (`priorItemsByRuleName`, threaded from the session) and computing genuine added/removed/changed
   sets. This surfaced a second, sharper bug: `MSBuildDependencyCollection`'s removal handling does
   `buildProjectChange.Before.Items[resolvedItemSpec]` — a **hard indexer**, not a safe lookup —
   to resolve a removed item's original evaluation id. The translator's `Before` snapshot had
   always been empty (fine when nothing is ever removed), so any actual removal threw
   `KeyNotFoundException` inside a `TransformBlock`, which **silently faults the block forever**
   with no visible error — the pipeline just stops emitting anything, indistinguishable from a
   hang. Fixed by populating `Before` with genuine prior item metadata, not just prior ids.

**Verified by tests that only make sense against a persistent session** (impossible to write
meaningfully against slice 43's one-shot design): `RepeatedCallsForSameProject_ReuseSessionAndReflectUpdatedData`
calls `BuildSnapshotAsync` three times for the same project path — add, add-another, then *remove
the first* — asserting each result reflects exactly the current data, not stale or duplicated state.
`ChangingTargetFrameworkSet_RebuildsSessionInsteadOfFailing` proves a TFM-set change correctly
discards and rebuilds rather than reusing a topologically-incompatible session. Full suite
(157 tests) and the downstream `SharpDevelop.csproj` build remain green.

**Still not wired to live events** (as of slice 47): sessions were only refreshed on a full Solution
Explorer rebuild, and never disposed when a project was removed or the solution closed. The
disposal half is addressed in slice 48; targeted per-event posting (independent of a full tree
rebuild) remains a future slice.

### Slice 48 — Reclaim per-project sessions on project removal / solution close ✅

`DependenciesSnapshotSession`s (slice 47) were never disposed, so removing a project from the
solution — or closing it — leaked its dataflow graph for the lifetime of the process. There's no
dedicated "project removed" event on `IProjectService`/`UnoProjectService` to hook (only per-item
`ProjectItemAdded`/`ProjectItemRemoved`, which doesn't distinguish "whole project unloaded" from
"one file removed"), so instead of chasing unreliable event-based detection, this reconciles
sessions against the live project set after every full rebuild:

- `UnoDevelopDependenciesSnapshotFactory.PruneSessionsExceptAsync(activeProjectPaths)` — disposes
  and forgets any session whose project path isn't in the given set. `ClearAllAsync()` is
  `PruneSessionsExceptAsync([])`, for solution close.
- `SolutionExplorerTreeBuilder.cs` — a `VisitedProjectPaths` list (cleared at the start of each
  `CreateSolutionNodeAsync` call, populated by `CreateProjectNodeViaCpsAsync` for every project
  actually built) is used to prune at the end of both `CreateSolutionNodeAsync` overloads —
  reclaiming sessions for any project no longer present, regardless of *why* (removed, unloaded,
  renamed).
- `MainPage.xaml.cs`'s `OnSolutionClosed` calls `ClearAllAsync()` directly, *before* the refresh —
  needed because closing a solution can fall back to a plain directory listing with no resolvable
  solution file, which never reaches `CreateSolutionNodeAsync`'s prune-after-rebuild path.

**Verified**: `PruneSessionsExceptAsync_DisposesUnvisitedProjectSessionsAndAllowsRebuild` builds
sessions for two projects, prunes one out, and confirms not just that its dictionary entry is gone
but that the underlying provider was actually disposed and a subsequent rebuild for that same path
produces a fresh, correctly-working session rather than throwing against a disposed provider or
hanging. Full suite (158 tests) and the downstream `SharpDevelop.csproj` build remain green.

**Still not wired to live events** (as of slice 48): `ProjectItemAdded`/`ProjectItemRemoved` always
triggered a full Solution Explorer refresh (state capture, rebuild every project, state restore).

### Slice 49 — Targeted single-project refresh instead of a full Solution Explorer rebuild ✅

Closes the original slice-22/23 motivation: `MainPage.OnProjectItemCollectionChanged` now rebuilds
only the affected project's `TreeViewNode` and splices it back into the existing tree, instead of
running `CaptureSolutionTreeState()` → `LoadSolutionTreeAsync()` (rebuild *every* project) →
`RestoreSolutionTreeState()`.

- `SolutionExplorerTreeBuilder.RefreshProjectNodeAsync(tree, projectPath, project, displayName)` —
  recursively finds the project's node anywhere in the tree by `(Kind == Project, FullPath ==
  projectPath)` (handles projects nested in solution folders, not just top-level), rebuilds it via
  the existing `CreateProjectNodeViaCpsAsync` (itself unchanged — it was already safe to call in
  isolation, no dependency on shared traversal state beyond `ShowAllFiles`), preserves the old
  node's expanded state and selection, and replaces it in place by index — sibling order and every
  *other* project's node/expansion/scroll position are untouched. Returns `false` if the project's
  node can't be found (e.g. before the tree has been built once), so the caller can fall back to a
  full refresh.
- `MainPage.OnProjectItemCollectionChanged` (MainPage.xaml.cs) tries the targeted path first via
  `e.Project.FileName`, falling back to `RefreshSolutionTreeAsync()` only if that returns false.

This also means a single-project item change now only touches *that* project's
`DependenciesSnapshotSession` (slice 47) — the persistent dataflow graph's own incremental diffing
was already correct per-project; this slice stops the *UI* from discarding and rebuilding every
other project's tree state on every keystroke-adjacent change.

**Not covered by the automated test suite**: `TreeView`/`TreeViewNode` (WinUI controls) aren't
instantiated anywhere in this headless NUnit suite — they need a running UI dispatcher, consistent
with the rest of `MainPage.xaml.cs` having no direct unit tests. This needs manual/in-app
verification: add or remove a file in one project of a multi-project solution and confirm only that
project's node refreshes (other projects' expansion state and scroll position are undisturbed).
`SolutionExplorerTreeBuilder`'s build remains green and its non-UI logic (`CreateProjectNodeViaCpsAsync`
→ `UnoDevelopDependenciesSnapshotFactory`) is exercised by the existing slice 41-48 tests.

**Was not covered** (as of slice 49): `UnoProjectChangeWatcher.ChangedExternally` (external
`.csproj` edits, e.g. `dotnet add package` in a terminal) wasn't wired to any refresh. Addressed in
slice 50.

### Slice 50 — Reload on external edit, closing the incremental-updates arc ✅

Wires the previously-orphaned `UnoProjectChangeWatcher` (it existed but nothing ever instantiated
it) so an externally-edited `.csproj` reloads the live project model and refreshes just that
project's Dependencies node — reusing slice 49's targeted-refresh path unchanged.

This one carries real risk the earlier slices didn't: it required adding a genuine reload
capability to `MSBuildBasedProject` itself (`externals/SharpDevelop`), the shared base class behind
build, IntelliSense, and Solution Explorer — not just the isolated `ProjectSystemManaged` dataflow
layer everything from slice 41 onward lived in.

- `MSBuildBasedProject.ReloadFromDisk()` (new method, wrapped in `#if HAS_UNO` per this port's
  convention for Uno-specific additions to the shared SharpDevelop base) reuses the existing
  `PerformUpdateOnProjectFile` pattern (lock `SyncRoot` → unload the cached MSBuild evaluator →
  mutate → rebuild the `Items` list) that the class already used internally for property changes.
  Deliberately narrower than the original `LoadProjectInternal`: no interactive ToolsVersion-upgrade
  prompt, no GUID re-read — those are one-time-load concerns not expected to matter for a live
  reload. **Key correctness detail**: `ProjectRootElement.Open(path, collection)` returns the
  `ProjectCollection`'s already-cached instance for a path it has seen before — it does *not*
  notice the file changed on disk. The fix is to call `.Reload(throwIfUnsavedChanges: false)` on
  the *existing* `ProjectRootElement` instance instead, which forces it to re-read from disk.
  `projectFile`/`userProjectFile` are private fields with no existing setter, so this had to be a
  new method on the base class itself — a subclass can't reassign them from outside.
- `UnoProjectModel` (`UnoProjectService.cs`) now owns a `UnoProjectChangeWatcher` for its own
  `.csproj`, created in its constructor and disposed alongside the project. On `ChangedExternally`,
  it calls `ReloadFromDisk()` (catching and logging failures rather than leaving the project
  half-updated if the file is mid-write or has invalid XML) and then raises the *existing*
  `ProjectItemAdded` event via `UnoProjectService.RaiseProjectItemAdded(new ProjectItemEventArgs(this, null!))`
  — reusing slice 49's `MainPage.OnProjectItemCollectionChanged` → `RefreshProjectNodeAsync` path
  exactly, with zero new UI-layer code. `ProjectItemEventArgs.ProjectItem` is unused by that handler
  for this purpose, so `null` is fine.

**Known accepted edge case**: the file watcher can't distinguish an external edit from UnoDevelop's
own save of the same file (e.g. adding a package reference through the IDE's own UI) — a redundant
reload in that case is harmless (idempotent) but not optimized away.

**Not covered by the automated test suite**: constructing a real `MSBuildBasedProject`/`UnoProjectModel`
requires a full `ISolution`/`ProjectCollection` bootstrap not currently exercised by any existing
test (the dataflow-layer tests all use the "unloaded project" XML-parsing path precisely to avoid
this). This needs manual/in-app verification: with a solution open, edit a project's `.csproj` in
an external editor or via `dotnet add package` in a terminal, and confirm the Dependencies tree
updates without any manual refresh. The build succeeding (including the vendored
`MSBuildBasedProject.cs` change) and the full existing suite (158 tests) remaining green are the
available automated signal.

This closes the arc that began at slice 22/23: full solution rebuild → real dataflow pipeline →
real MEF composition → persistent sessions with correct diffing → targeted UI refresh → refresh on
external edits. Everything that triggers a Solution Explorer dependency-tree update now does so
incrementally, for just the affected project, through the real CPS pipeline.

---

## Skipped files and why

Re-audited 2026-07-26 (opendevelop-sync.md Phase 2.1) — half of these turned out to already be
non-issues:

| File | Status |
|---|---|
| `ProjectTreeFlagsExtensions.cs` | **Not a gap.** `ProjectTreeFlags.Common` is deliberately a `static class` of constants in this shim (not upstream's `Common` struct), so the one overload typing a param as `ProjectTreeFlags.Common` can't compile by design — but the actual functionality (`IsProjectRoot`/`IsFolder`/etc.) already lives directly in `Tree/ProjectTreeExtensions.cs`. Nothing missing. |
| `MetadataExtensions.cs` (upstream) | **Not a gap.** `GetProjectItemProperties` needs `IProjectRuleSnapshot` (CPS SDK); already replaced by the shim's own `ProjectItemMetadata.cs`. |
| `DependencyExtensions.cs` (old) | **Not a gap.** Retired code, superseded by the dataflow-layer work described elsewhere in this doc. |
| `IProjectTreeExtensions.cs` | **Not a gap.** Its 4 tree-navigation methods are already in `Tree/ProjectTreeExtensions.cs`; `FindChildForDependency` has its own shim in `ProjectSystemManaged/DependencyProjectTreeExtensions.cs`; the one remaining method, `GetBrowseObjectPropertiesViaSnapshotIfAvailable`, is only called by `WindowsFormsEditorProvider.cs` (VS/WinForms-designer-specific, not part of UnoDevelop's surface) — nothing calls it here. |
| `AppDesignerFolderProjectTreePropertiesProvider.cs` | **Real gap, confirmed.** Tried linking it directly (same pattern as everything else in this csproj) — needs `IProjectTreeSettingsProvider`, `IProjectRuleSnapshot`, and `IProjectDesignerService`, none of which exist in this shim. Genuinely needs real CPS SDK rule-snapshot plumbing, not just MEF wiring. Deliberately left unlinked; low priority (cosmetic "AppDesigner"/Properties-folder icon only). |
| `ProjectRootImageProjectTreePropertiesProvider.cs` | **Was already linked**, not actually skipped — the "MEF + IProjectCapabilitiesService not shimmed" note was stale. Verified: `[Export]`/`[AppliesTo]`/`[Order]`/`IProjectCapabilitiesService` all compose correctly via `RealMefHost.cs`'s real attributed MEF discovery (`Microsoft.VisualStudio.Composition`), and the file builds today (`ProjectSystemManaged/Microsoft.VisualStudio.ProjectSystem.Managed.csproj:187`). |

---

## IProjectTree members used by project-system

| Member | Type | Notes |
|---|---|---|
| Caption | string | display label |
| FilePath | string? | absolute path or null |
| Flags | ProjectTreeFlags | capability flags |
| Parent | IProjectTree? | null for root |
| Children | IEnumerable\<IProjectTree\> | direct children |
| Root | IProjectTree | walk to root |
| IsFolder | bool | Flags.Contains(Folder) |
| IsRoot | bool | Parent == null |
| BrowseObjectProperties | IRule? | Properties window |
| Icon | ProjectImageMoniker? | collapsed icon |
| ExpandedIcon | ProjectImageMoniker? | expanded icon |
| Visible | bool | visible in tree |
| DisplayOrder | int | sort key |
