# Open-Source CPS Shim — Implementation Plan

Goal: build a clean-room reimplementation of the CPS framework surface
(`Microsoft.VisualStudio.ProjectSystem.*`) so that MIT-licensed
`dotnet/project-system` code compiles and links unchanged into OpenDevelop,
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
  SharpDevelopProjectTreeProvider.cs            ← SD IProject → MutableProjectTree (bridge)
  CpsTreeConverter.cs                            ← MutableProjectTree → TreeViewNode (UI bridge)

externals/project-system/                        ← git submodule (MIT), source of Linked/ files
```

---

## Slice map

Slices 7, 13 and 49 are intentionally absent: they only described UI wiring that is not part of this repository.

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

### Slice 6 — Wire to SharpDevelop project model ✅
`SharpDevelopProjectTreeProvider : ProjectTreeProviderBase` (SharpDevelop project).
Builds `MutableProjectTree` from `IProject`: References virtual folder + file-system subtree.
`CpsTreeConverter` bridges CPS `IProjectTree` → `TreeViewNode` / `SolutionExplorerNodeContext`.
The project browser's tree builder (`ProjectBrowserTreeBuilder`) consumes the provider's tree.

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

### Slice 14 — Centralize bridge tree flags ✅
Added `ProjectTreeFlags` constants for the tree semantics introduced by the CPS bridge:
- `DependenciesFolder`
- `PackagesFolder`
- `PackageReference`
- `ProjectReference`
- `LinkedFile`

Each is exposed both as a top-level property and under `ProjectTreeFlags.Common`, matching the rest of the shim surface (`externals/cps/src/ProjectSystem/Tree/ProjectTreeFlags.cs`). `SharpDevelopProjectTreeProvider` uses these constants instead of ad hoc `ProjectTreeFlags.Create("...")` calls.

### Slice 15 — Carry SharpDevelop ProjectItem bindings through CPS tree ✅
`SharpDevelopProjectTreeProvider` now attaches original SharpDevelop `ProjectItem` instances to live CPS tree nodes using an internal `ConditionalWeakTable<IProjectTree, ProjectItem>` side map. This keeps the clean-room CPS shim free of SharpDevelop-specific APIs while still preserving command context for Solution Explorer.

Live project nodes now bind:
- Reference / ProjectReference nodes
- PackageReference nodes
- included file nodes, including dependent and linked files

`CpsTreeConverter` now carries the `IProject` context through recursive conversion and restores `BoundProjectItem` from the side map when creating `SolutionExplorerNodeContext`. XML fallback nodes for unloaded projects remain unbound, as expected.

### Slice 16 — Link upstream DependencyGroupTypes ✅
Linked `DependencyGroupTypes.cs` from upstream `dotnet/project-system` after extending the clean-room stubs it needs:
- `Resources` now exposes the built-in dependency group captions (`Analyzers`, `Assemblies`, `COM`, `Frameworks`, `Packages`, `Projects`, `SDKs`)
- `KnownProjectImageMonikers` now exposes zero-value stubs for the dependency group icon monikers used by upstream code

This removes another local skip while keeping real icon resolution as a later UI concern.

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

### Slice 25 — Apply upstream dependency flags in the CPS bridge ✅
Updated `SharpDevelopProjectTreeProvider` so generated Reference / ProjectReference / PackageReference nodes now carry upstream dependency semantics:
- references use `DependencyTreeFlags.ResolvedDependencyFlags`
- package references keep the bridge `PackageReference` flag and also carry resolved dependency flags
- project references use a dedicated bridge `ProjectReference` flag instead of relying on `ResolvedReference`

`CpsTreeConverter` now detects project references via `ProjectTreeFlags.Common.ProjectReference`. This avoids misclassifying ordinary resolved assembly references after adopting upstream `ResolvedDependencyFlags`, because upstream uses `ResolvedReference` for all resolved dependency nodes, not only project references.

This keeps the current UI shape (`References` / `Packages`) while aligning node flags with the upstream dependency model already linked in previous slices.

### Slice 26 — Classify bridge reference nodes by dependency type ✅
Expanded `SharpDevelopProjectTreeProvider` reference handling so live and unloaded projects share dependency-type classification for:
- `Reference`
- `ProjectReference`
- `Analyzer`
- `COMReference`
- `FrameworkReference`

The unloaded XML fallback now includes Analyzer / COM / Framework reference items instead of silently ignoring them. Reference captions are normalized per item type, and flags now include browse/folder-browse semantics where the upstream dependency model exposes public flags:
- analyzers: `FileSystemEntity` + `SupportsBrowse`
- COM references: `SupportsBrowse`
- framework references: `SupportsFolderBrowse`

This keeps the existing `References` folder UI while making generated nodes better match the linked MSBuild dependency factories.

### Slice 27 — Switch bridge to Dependencies root grouping ✅
Changed the current CPS bridge from sibling `References` / `Packages` roots to a VS/CPS-style `Dependencies` root.

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

`SharpDevelopProjectTreeProvider` now assigns `BrowseObjectProperties` for:
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

`SharpDevelopProjectTreeProvider` now collects dependency specs from live SharpDevelop projects or unloaded project XML, passes them through the facade, then normalizes public flags (`DependenciesFolder`, `PackagesFolder`, `ProjectReference`, `PackageReference`) for the existing Solution Explorer converter.

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

`SharpDevelopProjectTreeProvider` now detects target frameworks from live `MSBuildBasedProject` evaluation (`TargetFrameworks` / `TargetFramework`) and from unloaded project XML. Dependency flag normalization is recursive so package/reference public flags are restored under TFM slice nodes as well as direct dependency groups.

Added `UnloadedMultiTargetProjectBuildsTargetFrameworkDependencySlices` to lock this shape down.

### Slice 33 — Filter unloaded dependencies by TargetFramework conditions ✅
Added per-dependency target-framework applicability to `DependencyBridgeItem` and filtered dependencies per `DependenciesSnapshotSlice`.

For unloaded project XML, `SharpDevelopProjectTreeProvider` now recognizes common item conditions such as:

```
Condition="'$(TargetFramework)' == 'net8.0'"
```

and applies those items only to the matching TFM slice. The multi-target test now verifies common packages appear in both slices while TFM-specific packages appear only under their matching `net8.0` / `net9.0` node.

This is intentionally a small bridge parser for common `TargetFramework == value` item conditions, not a full MSBuild condition evaluator.

### Slice 34 — Add SDKReference to the upstream dependency bridge ✅
Extended dependency collection to recognize `SDKReference` in both live SharpDevelop projects and unloaded project XML.

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
  `IUnconfiguredProjectServices` (minimal — there is no per-project fault-isolation or
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

**Not yet wired to a real `IProjectDependenciesSubTreeProvider` implementation** — there is
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
  is a no-op — there is no JoinableTaskFactory-based join-blocking threading model to protect
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
  behaviorally equivalent for the single-writer-per-source usage here. Also
  `ConfiguredDependencyFilterBlock` (no-op pass-through: there is one active configuration
  per slice, nothing to filter against yet), `UnwrapCollectionChainedProjectValueDataSource<TCollection,TItem>`
  (a `ChainedProjectValueDataSourceBase` that is *also* a real `ITargetBlock`, so it can be `LinkTo`'d
  directly or `Post`'d to), `DisposableDelegate`.
- `Dataflow/OnceInitializedOnceDisposedAsync.cs` — async lifecycle base
  (`InitializeAsync`/`InitializeCoreAsync`/`DisposeCoreAsync`).
- `Dataflow/ActiveConfigurationServices.cs` — `ConfigurationSubscriptionSources`,
  `IActiveConfigurationGroupSubscriptionService`, `IActiveConfiguredProjectProvider`,
  `IProjectThreadingService` (opaque — the dependency pipeline is driven from its own
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
`ProjectDataSources.SyncLinkTo`) produces a *correct* `DependenciesSnapshot` from the
existing evaluation data — not just that the types compile.

- `Bridge/DependencyBridgeSubscriptionTranslator.cs` — converts a project's
  `IReadOnlyList<DependencyBridgeItem>` (already gathered by `SharpDevelopProjectTreeProvider` from
  live MSBuild evaluation or unloaded project XML) into a full-width `IProjectSubscriptionUpdate`.
  **Key constraint discovered**: `MSBuildDependencySubscriber.Transform()` indexes
  `update.ProjectChanges[unresolvedRuleName]` directly (not `TryGetValue`), so every posted update
  must contain an entry for *all seven* registered factories' rule names, even when a dependency
  type has zero current items (with `Difference.AnyChanges = false` so
  `MSBuildDependencyCollection.TryUpdate` cheaply skips it instead of creating an empty group).
  Includes concrete `IProjectSubscriptionUpdate`/`IProjectChangeDescription`/`IProjectChangeSnapshot`/`IProjectChangeDiff`
  implementations. Reuses `DependencyTreeBridgeBuilder.GetFactory`/`MergeMetadata` (changed from
  `private` to `internal`) rather than duplicating factory-mapping/metadata-default logic.
- `Bridge/*ActiveConfigurationSubscriptionSource.cs` — concrete per-slice
  `IActiveConfigurationSubscriptionSource`, plus `SingleValueDataSource<T>`, a data source that
  always has exactly one value. Relies on real `BroadcastBlock<T>`'s "late linkers see the last
  posted value" semantics: all data is posted *before* the lazy consumer side links to it (which
  happens deep inside `ChainedProjectValueDataSourceBase`'s first `SourceBlock` access), so no
  explicit post/link ordering or wait/retry loop is needed.
- `Bridge/*ProjectSystemServices.cs` — concrete `IProjectThreadingService` (opaque — no
  JoinableTaskFactory model), `IUnconfiguredProjectCommonServices`, `IUnconfiguredProjectTasksService`
  (no project-unload model, so `LoadedProjectAsync` just invokes its delegate),
  `IProjectFaultHandlerService` (no-op), `IActiveConfiguredProjectProvider`,
  `IActiveConfigurationGroupSubscriptionService` (publishes a fixed, one-time set of slices —
  target frameworks are resolved up front rather than discovering them incrementally).
- `Dataflow/ManualComposition.cs` — since the shim's MEF attributes (slice 8/11) don't drive any
  real container/catalog, this reflects into the private `[ImportMany] OrderPrecedenceImportCollection<T>`
  fields on `MSBuildDependencySubscriber`/`DependenciesSnapshotProvider` and calls the collection's
  existing public `.Add()` — mechanical field injection, not a MEF reimplementation.
- `Bridge/SharpDevelopDependenciesSnapshotFactory.cs` — orchestrates the above into
  `BuildSnapshotAsync(projectPath, itemsByTargetFramework)`, returning a `DependenciesSnapshot` (or
  `null` on a 5s timeout, for callers to fall back to the existing bridge).

The pipeline is exercised by feeding hand-built `DependencyBridgeItem`s through it. A
single-target project yields exactly the `Packages`/`Assemblies`/`Projects` groups for the items
supplied and *no* `Analyzers`/`SDKs`/`Frameworks`/`COM` groups (the always-present-but-often-empty
rule entries are skipped rather than surfacing empty groups or throwing); a multi-target project
yields one `DependenciesSnapshotSlice` per TFM. No test project in this repository covers it.

**Not yet done**: this slice proves the real pipeline *works*, but doesn't *replace* the
imperative `DependencyTreeBridgeBuilder` path, nor does anything post incremental updates in
response to project item changes (every `BuildSnapshotAsync` call here is a fresh one-shot
construction of the whole dataflow graph, not a long-lived incrementally-updated subscription).
That was addressed in slices 46-47.

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
- Rewired `Bridge/SharpDevelopDependenciesSnapshotFactory.cs` to use `CompositionScope.Activate<T>()`
  for both `MSBuildDependencySubscriber` and `DependenciesSnapshotProvider`, registering per-project
  instances (`UnconfiguredProject`, the various bridge threading/tasks services
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

### Slice 46 — Async dependency-tree construction through the real dataflow pipeline ✅

The dataflow pipeline (slices 41-45) was proven correct in isolation; this slice makes the
project tree provider consume it, falling back to the imperative path whenever the pipeline
doesn't produce a result.

- `Bridge/SharpDevelopDependenciesSnapshotFactory.BuildTreeAsync` — wraps `BuildSnapshotAsync`,
  converting the resulting `DependenciesSnapshot` into a `MutableProjectTree` via the *same*
  `DependenciesTreeBuilder`/`BridgeTreeOperations` construction
  `DependencyTreeBridgeBuilder.BuildDependenciesTree` already uses (`BridgeTreeOperations`/
  `BridgeUnconfiguredProject`/`BridgeConfiguredProject`/`BridgeCatalogSnapshot` are `internal`
  for reuse), so the result is a drop-in replacement wherever the imperative path's tree is
  consumed, and it returns null (rather than throwing) if the pipeline doesn't produce a snapshot.
- `SharpDevelopProjectTreeProvider.cs` (`src/Main/SharpDevelop/Services`) — the item-gathering
  logic (live `IProject` evaluation or unloaded-XML parsing) lives in a shared
  `GatherDependencyItems`; `GroupItemsByTargetFramework` buckets the flat, per-item-TFM-filtered
  list into the per-TFM-list shape `BuildTreeAsync` expects (a project with no TFMs gets a single
  `""` bucket); `BuildTreeAsync()`/`AddSpecialNodesAsync` sit alongside the synchronous
  `BuildTree()`/`AddSpecialNodes` (kept as the fallback path):
  `dependenciesNode = await SharpDevelopDependenciesSnapshotFactory.BuildTreeAsync(...) ?? DependencyTreeBridgeBuilder.BuildDependenciesTree(...)`.
- `SharpDevelopProjectTreeProvider` calls the public static
  `SharpDevelopDependenciesSnapshotFactory.BuildTreeAsync` directly.

### Slice 47 — Persistent per-project dataflow sessions, with real diff tracking ✅

Replaced the "rebuild the whole dataflow graph every call" behavior with a kept-alive
`DependenciesSnapshotSession` per project path: the graph (composed parts, dataflow blocks, links)
is built once and reused across `BuildSnapshotAsync`/`BuildTreeAsync` calls — only new evaluation
data is posted into the existing graph. A session is discarded and rebuilt if a project's target
framework set changes (the graph's slice topology is fixed at construction).

- `Bridge/DependenciesSnapshotSession.cs` — owns one `DependenciesSnapshotProvider` +
  one active-configuration subscription source per TFM slice, built once in `CreateAsync`
  (calls `EnsureInitializedAsync()` but posts no data — the per-slice `MSBuildDependencySubscriber`
  output stays empty until the first real post, same as a design-time build that hasn't run).
  `RefreshAsync` posts new data and awaits the resulting snapshot.
- `SharpDevelopDependenciesSnapshotFactory` keeps a `Dictionary<string, DependenciesSnapshotSession>`
  keyed by project path, disposing and rebuilding a session when the TFM key set no longer matches.

**Two real correctness bugs surfaced by repeated calls against the same session** — a one-shot
build cannot expose them, since a freshly-built graph has no state to get wrong:

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

### Slice 48 — Reclaim per-project sessions ✅

`DependenciesSnapshotSession`s (slice 47) must be disposed when a project leaves the solution, or
its dataflow graph leaks for the lifetime of the process. `IProjectService` has no dedicated
"project removed" event (only per-item `ProjectItemAdded`/`ProjectItemRemoved`, which doesn't
distinguish "whole project unloaded" from "one file removed"), so the factory reconciles sessions
against a caller-supplied set of live project paths:

- `SharpDevelopDependenciesSnapshotFactory.PruneSessionsExceptAsync(activeProjectPaths)` — disposes
  and forgets any session whose project path isn't in the given set. `ClearAllAsync()` is
  `PruneSessionsExceptAsync([])`, for solution close.

Nothing in `src/Main/SharpDevelop` calls either method yet, so sessions are not currently reclaimed.

### Slice 50 — `MSBuildBasedProject.ReloadFromDisk()` ✅

`MSBuildBasedProject.ReloadFromDisk()` (`src/Main/Base/Project/Src/Project/MSBuildBasedProject.cs`)
re-reads a project's `.csproj` (and `.csproj.user`, if present) and rebuilds its `Items` list from
the fresh evaluation, for when the project file changes outside the IDE. It reuses the existing
`PerformUpdateOnProjectFile` pattern (lock `SyncRoot` → unload the cached MSBuild evaluator →
mutate → rebuild the `Items` list) that the class already used internally for property changes.
Deliberately narrower than the original `LoadProjectInternal`: no interactive ToolsVersion-upgrade
prompt, no GUID re-read — those are one-time-load concerns not expected to matter for a live
reload. **Key correctness detail**: `ProjectRootElement.Open(path, collection)` returns the
`ProjectCollection`'s already-cached instance for a path it has seen before — it does *not*
notice the file changed on disk. The fix is to call `.Reload(throwIfUnsavedChanges: false)` on
the *existing* `ProjectRootElement` instance instead, which forces it to re-read from disk.
`projectFile`/`userProjectFile` are private fields with no existing setter, so this had to be a
method on the base class itself — a subclass can't reassign them from outside.

`ProjectService.ReloadProject` calls it and reloads the solution instead if the in-place reload
fails. Constructing a real `MSBuildBasedProject` needs a full `ISolution`/`ProjectCollection`
bootstrap, so this path is verified manually: edit a project's `.csproj` in an external editor or
via `dotnet add package` in a terminal and confirm the project's items update.

---

## Skipped files and why

Re-audited 2026-07-26 — half of these turned out to already be
non-issues:

| File | Status |
|---|---|
| `ProjectTreeFlagsExtensions.cs` | **Not a gap.** `ProjectTreeFlags.Common` is deliberately a `static class` of constants in this shim (not upstream's `Common` struct), so the one overload typing a param as `ProjectTreeFlags.Common` can't compile by design — but the actual functionality (`IsProjectRoot`/`IsFolder`/etc.) already lives directly in `Tree/ProjectTreeExtensions.cs`. Nothing missing. |
| `MetadataExtensions.cs` (upstream) | **Not a gap.** `GetProjectItemProperties` needs `IProjectRuleSnapshot` (CPS SDK); already replaced by the shim's own `ProjectItemMetadata.cs`. |
| `DependencyExtensions.cs` (old) | **Not a gap.** Retired code, superseded by the dataflow-layer work described elsewhere in this doc. |
| `IProjectTreeExtensions.cs` | **Not a gap.** Its 4 tree-navigation methods are already in `Tree/ProjectTreeExtensions.cs`; `FindChildForDependency` has its own shim in `ProjectSystemManaged/DependencyProjectTreeExtensions.cs`; the one remaining method, `GetBrowseObjectPropertiesViaSnapshotIfAvailable`, is only called by `WindowsFormsEditorProvider.cs` (VS/WinForms-designer-specific, not part of this project's surface) — nothing calls it here. |
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
