# SharpDevelop Class Diagram WPF Migration Assessment

## Implementation Status

The OpenDevelop migration is now active in
`src/AddIns/DisplayBindings/ClassDiagram/ClassDiagramAddin`.

The historical `ClassDiagramApp` was a developer/demo harness, not an out-of-process IDE design:
it loaded `ClassCanvas.dll` by reflection and used hard-coded local test paths, while the real
SharpDevelop AddIn hosted `ClassCanvas` in process. The migrated architecture nevertheless keeps
the useful part of that idea as a future option: `ClassDiagram.Core` is UI/IDE-independent so an
embedded surface and an external executable can share exactly the same Roslyn model, persistence,
relationship analysis, and MSAGL layout implementation. The IDE-hosted surface remains the default
until the WPF surface itself is extracted and shared; shipping a second, divergent renderer would
defeat the intended simplification.

The first usable WPF/Roslyn slice includes:

- a `LibreWPF.Sdk` AddIn targeting `net10.0-windows`;
- project-context-menu creation of a `.cd` document;
- Roslyn compilation and semantic-model discovery for classes, records, interfaces, structs,
  enums, delegates, generic parameters, base types, and displayed members;
- fully qualified symbol identities for inheritance and code relationships, including correct
  resolution of generic/array element types and same-named types in different namespaces;
- WPF class cards with inheritance/implementation connectors, scrolling, and zoom;
- UML inheritance and implementation edges with hollow triangle markers and dashed realization;
- Roslyn association discovery from fields/properties/events and dependency discovery from
  method signatures, rendered with distinct solid/dashed arrows;
- conservative aggregation inference for collections and composition inference for directly
  initialized owned members, rendered with hollow/filled diamonds at the owner endpoint;
- independent visibility toggles for inheritance/implementation, association, and dependency
  relationships;
- persistent in-view node selection with direct inheritance and code-relationship neighborhood
  highlighting;
- MSAGL Sugiyama automatic layout using measured collapsed/expanded node bounds and all known
  relationship topology;
- WPF `ActualHeight`/desired-size feedback for subsequent automatic layout passes, with safe
  estimated bounds during the initial load;
- MSAGL edge routes sampled into WPF polylines after automatic arrangement, with safe straight-line
  fallback after manual node movement;
- editable, draggable note nodes with legacy `<Comment>` import and versioned persistence;
- PNG export of the complete diagram canvas;
- lossless retention of unknown root attributes and unrecognized root elements when a `.cd` file
  is loaded and saved;
- inclusion in `SharpDevelop.sln`, `SharpDevelop.Tests.sln`, and the installer component list;
- draggable class cards with persisted positions;
- automatic arrangement and per-node/all-node expansion state;
- independently collapsible field, property, event, and method groups, including import and
  persistence of the corresponding legacy SharpDevelop state;
- double-click navigation from a type card to its source file;
- double-click navigation from each displayed member to its declaration line;
- cancellable, versioned background refresh that preserves node and note state;
- automatic debounced refresh when watched C# project sources change on disk;
- refresh from source without regenerating the `.cd` file; and
- versioned `.cd` persistence using relative source-file paths, positions, and collapsed state.

The legacy WinForms `ClassCanvas`, `DiagramRouter`, and SharpDevelop DOM implementation remain
in the source tree for compatibility research, but are deliberately excluded from the migrated
AddIn build. The obsolete standalone `ClassDiagramApp` sample is not the feature implementation
and may be removed independently.

The current slice uses MSAGL Sugiyama automatic arrangement and permits manual layout editing
afterward. It imports legacy SharpDevelop node coordinates/collapse state and navigates to the
declaration line. Relationship endpoints are resolved through Roslyn symbols; the ownership kind
remains intentionally conservative because C# signatures alone cannot prove object lifetime.
Keeping the domain/persistence model independent of the WPF canvas makes that layout replacement
possible without changing the `.cd` format again.

The regression suite in `src/AddIns/DisplayBindings/ClassDiagram/Test` covers Roslyn discovery,
semantic relationship resolution (including namespace collisions and generic/array element
types), cancellable background refresh/state preservation, versioned persistence, legacy XML
state/comment import, and MSAGL layout and route output.

## Background

SharpDevelop's Class Diagram feature is primarily a code-structure viewer rather than a full bidirectional class designer.

Its main responsibilities are:

- Displaying classes, interfaces, structs, enums, delegates, and their members
- Showing inheritance, implementation, association, and dependency relationships
- Automatically arranging the diagram
- Allowing users to manually adjust node positions
- Expanding or collapsing member sections
- Navigating from a class or member to source code
- Saving diagram layout and visibility state

It does not need the full interaction model of a diagram editor such as creating classes, drawing relationships, editing members, or performing code refactorings directly on the canvas.

Because of this, a general-purpose WPF node editor such as Nodify is probably unnecessary for the initial migration.

## Recommended Direction

Use **Microsoft Automatic Graph Layout (MSAGL)** as the main rendering, layout, and interaction framework.

The recommended architecture is:

```text
Roslyn Compilation
    |
    v
ClassDiagramModel
    |
    v
Microsoft.Msagl.Drawing.Graph
    |
    v
MSAGL layout and edge routing
    |
    v
Customized MSAGL WPF GraphViewer
```

The main recommendation is:

> Fork or wrap MSAGL's WPF GraphViewer, add support for custom WPF node controls, and retain MSAGL's layout, routing, zooming, panning, hit testing, and node movement behavior.

## Why MSAGL Fits the Requirement

MSAGL already provides most of the difficult infrastructure required by a read-only class diagram:

- Hierarchical and general graph layout
- Node-size-aware layout
- Edge routing and obstacle avoidance
- Zoom and pan
- Hit testing
- Node dragging for manual layout adjustment
- Layout editing support
- Arrow rendering
- Background layout calculation
- Graph export capabilities

This closely matches the original SharpDevelop Class Diagram behavior.

The user may move classes to improve the layout, but that is still layout editing rather than source-code editing.

## Main Integration Challenge

The main missing piece is the visual representation of a class node.

MSAGL normally renders relatively simple graph nodes:

```text
+----------+
| Customer |
+----------+
```

SharpDevelop requires a compound node:

```text
+---------------------------+
| Customer                  |
+---------------------------+
| + Name : string           |
| - id : Guid               |
+---------------------------+
| + SaveAsync() : Task      |
| + Delete() : void         |
+---------------------------+
```

A class node may contain:

- Type name
- Type icon or stereotype
- Generic parameters
- Base type information
- Fields
- Properties
- Events
- Methods
- Visibility icons
- Expand and collapse state
- Member navigation targets

The WPF viewer therefore needs an extension point that can create a custom `FrameworkElement` for each graph node.

A possible API is:

```csharp
public Func<Node, FrameworkElement>? NodeElementFactory { get; set; }
```

The viewer can then replace its default text-label creation:

```csharp
var element = CreateTextBlockForDrawingObj(drawingNode);
```

with:

```csharp
var element =
    NodeElementFactory?.Invoke(drawingNode)
    ?? CreateTextBlockForDrawingObj(drawingNode);
```

The resulting `FrameworkElement` must be measured before layout so that MSAGL receives the correct node width and height.

## Responsibility Split

### MSAGL Responsibilities

MSAGL should handle:

- Initial automatic layout
- Hierarchical inheritance layout
- Edge routing
- Obstacle avoidance
- Node dragging
- Connection updates after node movement
- Zoom
- Pan
- Fit to view
- Hit testing
- Selection highlighting
- Basic graph export

### OpenDevelop Responsibilities

OpenDevelop should handle:

- Roslyn symbol analysis
- Class diagram domain model
- Type and member presentation
- Type icons
- Member visibility icons
- Member grouping
- Expand and collapse state
- Navigation to source
- Context-menu commands
- `.cd` file compatibility
- Persisting manual positions
- Persisting visibility and expansion state
- UML-specific relationship appearance

## Proposed Project Structure

```text
OpenDevelop.ClassDiagram.Core
    ClassDiagramModel
    TypeNode
    MemberNode
    Relationship
    RoslynSymbolAdapter
    RelationshipAnalyzer

OpenDevelop.ClassDiagram.Wpf
    ClassDiagramView
    ClassNodeControl
    ClassNodeViewModel
    MemberViewModel
    GraphViewerAdapter
    UmlEdgeRenderer

OpenDevelop.ClassDiagram.Layout
    IClassDiagramLayoutEngine
    MsaglLayoutEngine

OpenDevelop.ClassDiagram.Persistence
    SharpDevelopCdReader
    SharpDevelopCdWriter
    DiagramLayoutState
```

## Domain Model

The graph library should not become the domain model.

A separate class diagram model should describe the code structure:

```csharp
public sealed class ClassDiagramModel
{
    public IReadOnlyList<TypeNode> Types { get; init; } = [];
    public IReadOnlyList<Relationship> Relationships { get; init; } = [];
}

public sealed class TypeNode
{
    public required TypeIdentity Identity { get; init; }
    public required string DisplayName { get; init; }
    public required TypeKind Kind { get; init; }
    public IReadOnlyList<MemberNode> Members { get; init; } = [];
    public DiagramNodeState State { get; init; } = new();
}
```

MSAGL nodes and edges should be generated from this model.

This separation makes it possible to replace or upgrade the graph viewer later without rewriting the Roslyn analysis or persistence layers.

## Roslyn Integration

The old SharpDevelop Class Diagram depends on SharpDevelop DOM and NRefactory concepts such as `IClass`.

The WPF migration should replace them with Roslyn.

The model should not persist direct `ISymbol` or `INamedTypeSymbol` instances because symbol instances may change when the workspace or compilation is rebuilt.

A stable identity can use:

```csharp
public sealed record TypeIdentity(
    string ProjectId,
    string DocumentationCommentId,
    string? AssemblyIdentity);
```

During an active workspace session, Roslyn `SymbolKey` may also be used to resolve the current symbol.

The Roslyn layer should identify:

- Classes
- Interfaces
- Structs
- Records
- Enums
- Delegates
- Nested types
- Generic type parameters
- Base classes
- Implemented interfaces
- Public, protected, internal, and private members
- Associations inferred from fields and properties
- Dependencies inferred from parameters and return types

## Layout Strategy

A class diagram may contain two different graph structures:

1. A hierarchy dominated by inheritance and interface implementation
2. A general dependency graph dominated by associations and dependencies

For inheritance-heavy diagrams, use a layered layout:

```csharp
graph.LayoutAlgorithmSettings =
    new SugiyamaLayoutSettings
    {
        LayerSeparation = 50,
        NodeSeparation = 30
    };
```

This places base classes and interfaces above derived types.

For dependency-heavy diagrams, an MDS layout may be more appropriate:

```csharp
graph.LayoutAlgorithmSettings =
    new MdsLayoutSettings();
```

A practical implementation can select the layout automatically based on the ratio of inheritance edges to other relationship types.

Manual positions loaded from a `.cd` file should override automatic placement where possible.

## UML Relationship Rendering

The relationship model should distinguish at least:

```csharp
public enum RelationshipKind
{
    Inheritance,
    Implementation,
    Association,
    Dependency,
    Aggregation,
    Composition
}
```

Suggested visual mapping:

| Relationship | Appearance |
|---|---|
| Inheritance | Solid line with hollow triangle |
| Implementation | Dashed line with hollow triangle |
| Association | Solid line |
| Dependency | Dashed line with arrow |
| Aggregation | Solid line with hollow diamond |
| Composition | Solid line with filled diamond |

MSAGL can calculate the edge path and endpoint geometry.

OpenDevelop may need a custom WPF edge renderer for UML-specific triangle and diamond markers, especially if the built-in arrow styles are insufficient.

## Class Node Rendering

`ClassNodeControl` should be a normal WPF control or `UserControl`.

A possible structure is:

```text
Border
  Grid
    Header
      Type icon
      Type name
      Generic parameters

    Member groups
      Fields
      Properties
      Events
      Methods
```

Each member row should carry navigation information:

```csharp
public sealed class MemberViewModel
{
    public required string DisplayText { get; init; }
    public required Accessibility Visibility { get; init; }
    public required MemberKind Kind { get; init; }
    public required SourceLocation Location { get; init; }
}
```

Double-clicking a member should invoke the IDE navigation service rather than placing editing logic inside the class diagram component.

## SharpDevelop `.cd` Compatibility

Compatibility should be implemented in a separate persistence layer.

The reader should import:

- Included types
- Node positions
- Node sizes where available
- Hidden and visible members
- Expanded and collapsed sections
- Relationship visibility
- Diagram zoom or viewport state, if present

The writer may initially preserve only the subset supported by OpenDevelop.

Unknown attributes and elements should ideally be retained or ignored safely so that old files do not become unusable.

## Suggested Migration Phases

### Phase 1: Read-Only Prototype

Implement:

- Roslyn type discovery
- Class and interface nodes
- Inheritance and implementation edges
- MSAGL automatic layout
- Zoom and pan
- Double-click navigation to type definition

This validates that MSAGL works correctly under WPF and LibreWPF.

### Phase 2: Member Display

Add:

- Fields
- Properties
- Events
- Methods
- Visibility icons
- Expand and collapse
- Dynamic node measurement
- Layout recalculation after expansion

### Phase 3: SharpDevelop Compatibility

Add:

- `.cd` file reader
- Saved node positions
- Saved expansion state
- Hidden types and members
- Context-menu commands
- Manual layout persistence

### Phase 4: Relationship Completeness

Add:

- Associations
- Dependencies
- Aggregation
- Composition
- UML-specific line and endpoint rendering
- Relationship filtering

### Phase 5: Performance and Polish

Add:

- Background Roslyn analysis
- Incremental graph updates
- Layout cancellation
- Large-diagram filtering
- Search and highlight
- Mini-map if needed
- Export to SVG or image

## Performance Considerations

Complex WPF controls inside graph nodes may become expensive when a diagram contains hundreds of types.

Recommended precautions:

- Avoid one control per punctuation token
- Use a single lightweight row per member
- Collapse member groups by default for large diagrams
- Measure nodes only when their content changes
- Avoid rebuilding the full graph after every Roslyn workspace event
- Apply incremental updates where possible
- Run layout calculations off the UI thread
- Cancel outdated layout operations
- Limit association and dependency inference unless explicitly enabled

For very large solutions, the default view should probably show only selected types and their immediate relationships.

## Risks

### Custom WPF Node Support

The MSAGL WPF viewer may require a maintained fork to support arbitrary node controls cleanly.

This is the largest technical uncertainty and should be validated first.

### LibreWPF Compatibility

MSAGL's WPF viewer may depend on WPF APIs not yet fully implemented by LibreWPF.

The initial spike should test:

- `Canvas`
- `Path`
- `Geometry`
- Transforms
- Mouse capture
- Hit testing
- Text measurement
- Zoom and pan
- Dispatcher behavior
- Printing or export APIs

### Node Measurement and Layout

Class nodes change size when member groups expand or collapse.

The implementation must:

1. Measure the WPF node
2. Update the MSAGL node boundary
3. Recalculate or reroute the graph
4. Preserve the user's manually adjusted positions where possible

### UML Marker Support

MSAGL's built-in arrows may not fully cover UML diamonds and hollow triangles.

A custom edge rendering layer may be required.

## Alternatives

### Nodify

Nodify is a strong choice for an interactive node editor, but it is probably unnecessary for the original SharpDevelop feature set.

It becomes relevant only if OpenDevelop later supports:

- Creating types from the diagram
- Drawing new relationships
- Editing members in place
- Connecting and reconnecting endpoints
- Full undo and redo
- Clipboard-based diagram editing

### GraphX

GraphX includes layout, routing, connection points, zoom, and diagram controls.

It could produce a prototype quickly, but it has a larger historical codebase and would likely require substantial modernization and maintenance.

### GraphShape

GraphShape provides WPF graph layout controls but less editor behavior.

It may work for a basic viewer, but MSAGL has stronger layout and routing capabilities.

## Final Recommendation

For the SharpDevelop Class Diagram migration, use:

- **Roslyn** for source-code analysis
- **A separate OpenDevelop class diagram model** for domain state
- **MSAGL Core and Layout** for graph layout and edge routing
- **A customized MSAGL WPF GraphViewer** for display and interaction
- **A custom `ClassNodeControl`** for the SharpDevelop-style class box
- **A custom UML edge renderer** where built-in arrow styles are insufficient
- **A dedicated `.cd` compatibility layer** for existing diagram files

Nodify should not be introduced unless the feature later evolves from a viewer into a full class-diagram editor.

The first engineering task should be a small compatibility spike that renders several custom WPF class nodes inside MSAGL, performs a Sugiyama layout, supports node dragging, and runs successfully on both standard WPF and LibreWPF.
