// OpenDevelop-owned layout DTO (doc/technotes/ilspy.md, "2026-08 architecture update" ->
// "Docking and layout replacement" step 4): a versioned, AvalonDock-independent snapshot of the
// pane/panel tree, plus a converter to/from the live AvalonDock.Layout.LayoutRoot object graph.
//
// Steps 1 (this Capture/Apply converter) and 2 (wiring it into DockWorkspace.SaveLayout/
// RestoreLayout as the actual persisted format, AvalonDock XML kept only as a template import
// format) are done - see doc/technotes/ilspy.md's "Real versioned layout DTO" entries.
//
// Step 3, done: LayoutDocumentSnapshot/LayoutSnapshot.Documents, a Capture(DockWorkspace) overload,
// and ReopenDocuments to restore them. The "virtual documents need special handling" concern
// raised while scoping this turned out to already be solved: IViewContent.PrimaryFileName (not
// PrimaryFile) is the general identity every document exposes, real or not - ILSpyAddIn's
// DecompiledViewContent already overrides it to a synthetic "ilspy://<assembly>/<type>.cs" FileName
// (ILSpyDecompilerService.DecompiledTypeReference.ToFileName/FromFileName, a reversible encoding),
// and ILSpyDisplayBinding is already registered (ILSpyAddIn.addin, fileNamePattern "^ilspy://") to
// resolve exactly that scheme through the ordinary SD.FileService.OpenFile pipeline - the same one
// OpenLoadedModuleInILSpyCommand.cs already uses to open one. So there is no new "reopenable?"
// extension point to build: Capture/ReopenDocuments work identically for real and virtual
// documents by treating PrimaryFileName as the one identity that matters, skipping only the one
// real non-identity case (an unsaved/untitled real file has no meaningful path to reopen by).
//
// Floating windows are not captured either (same gap as before) - Capture records a placeholder
// for document panes so the panel *shape* round-trips, but never document content/identity beyond
// the flat Documents list above, and Apply reuses whatever document pane(s) already exist in the
// live tree rather than trying to recreate them from the snapshot.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Controls;

using AvalonDock.Layout;

namespace ICSharpCode.SharpDevelop.Workbench;

/// <summary>One node of the captured panel tree - either a split panel or a pane of anchorables.</summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(LayoutSplitSnapshot), "split")]
[JsonDerivedType(typeof(LayoutAnchorablePaneSnapshot), "anchorablePane")]
[JsonDerivedType(typeof(LayoutDocumentAreaSnapshot), "documentArea")]
public abstract class LayoutNodeSnapshot
{
}

/// <summary>A `LayoutPanel` (or `LayoutAnchorablePaneGroup`/`LayoutDocumentPaneGroup`) split container.</summary>
public sealed class LayoutSplitSnapshot : LayoutNodeSnapshot
{
    public Orientation Orientation { get; set; }
    public List<LayoutNodeSnapshot> Children { get; set; } = new();
}

/// <summary>A `LayoutAnchorablePane` - a named group of docked tool-pane anchorables.</summary>
public sealed class LayoutAnchorablePaneSnapshot : LayoutNodeSnapshot
{
    public string Name { get; set; }
    public double? DockWidth { get; set; }
    public double? DockHeight { get; set; }
    public List<AnchorableSnapshot> Anchorables { get; set; } = new();
}

/// <summary>One docked anchorable's identity/state within its pane, in tab order.</summary>
public sealed class AnchorableSnapshot
{
    public string ContentId { get; set; }
    public bool IsSelected { get; set; }
    public bool IsVisible { get; set; }
}

/// <summary>
/// Placeholder for a `LayoutDocumentPane`/`LayoutDocumentPaneGroup` - marks where in the panel
/// tree the document area sits, without capturing which documents are open (see file header).
/// </summary>
public sealed class LayoutDocumentAreaSnapshot : LayoutNodeSnapshot
{
}

/// <summary>
/// One open, file-backed document's identity/state (step 3's first slice - see file header for
/// why this is identity only, not content, and why virtual documents are excluded entirely).
/// </summary>
public sealed class LayoutDocumentSnapshot
{
    public string FileName { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>The versioned root of a captured layout - <see cref="LayoutSnapshotConverter"/>'s unit of work.</summary>
public sealed class LayoutSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public LayoutNodeSnapshot Root { get; set; }
    public List<LayoutDocumentSnapshot> Documents { get; set; } = new();
}

/// <summary>
/// Converts between a live AvalonDock <see cref="LayoutRoot"/> and the OpenDevelop-owned
/// <see cref="LayoutSnapshot"/> DTO. <see cref="Capture"/> is a pure read; <see cref="Apply"/>
/// reuses existing <see cref="LayoutAnchorable"/>/document-pane instances already present in the
/// live tree (matched by <see cref="LayoutContent.ContentId"/>) rather than constructing new ones -
/// a fresh <see cref="LayoutAnchorable"/> would have no bound `Content`, since that only happens
/// through the `AnchorablesSource`/`DocumentsSource` bindings `DockWorkspace` sets up once.
/// Anchorables the snapshot doesn't mention are left exactly where they already are.
/// </summary>
public static class LayoutSnapshotConverter
{
    public static LayoutSnapshot Capture(LayoutRoot root)
    {
        if (root == null)
            throw new ArgumentNullException(nameof(root));
        return new LayoutSnapshot { Root = ConvertToSnapshot(root.RootPanel) };
    }

    /// <summary>
    /// <see cref="Capture(LayoutRoot)"/> plus the step-3 document-identity slice: every currently
    /// open document's <see cref="IViewContent.PrimaryFileName"/> - real or virtual alike (see
    /// file header) - except an unsaved/untitled real file, which has no meaningful path to
    /// reopen by and is silently excluded rather than recorded as a broken entry.
    /// </summary>
    internal static LayoutSnapshot Capture(DockWorkspace workspace)
    {
        if (workspace == null)
            throw new ArgumentNullException(nameof(workspace));
        var snapshot = Capture(workspace.Layout);
        var activeDocument = workspace.ActiveDocument;
        foreach (var document in workspace.Documents)
        {
            var view = document.ActiveViewContent;
            var fileName = view?.PrimaryFileName;
            if (fileName == null)
                continue;
            if (view.PrimaryFile != null && view.PrimaryFile.IsUntitled)
                continue;
            snapshot.Documents.Add(new LayoutDocumentSnapshot
            {
                FileName = fileName.ToString(),
                IsActive = document == activeDocument,
            });
        }
        return snapshot;
    }

    /// <summary>
    /// The other half of the step-3 slice: reopens every document a <see cref="LayoutSnapshot"/>
    /// recorded that ISN'T ALREADY OPEN, via the ordinary <c>SD.FileService.OpenFile</c> pipeline -
    /// works uniformly for real and virtual (e.g. ILSpy <c>ilspy://</c>) file names alike, per the
    /// file header. Any individual document that fails to reopen (a deleted file, an addin whose
    /// display binding isn't loaded, ...) is logged and skipped rather than aborting the rest of
    /// the list.
    ///
    /// Only touches documents not already open: <c>RestoreLayout</c> runs on every layout switch,
    /// not just app startup, so a naive "reopen everything, activate whichever the snapshot marked
    /// active" would re-select and steal focus from an already-open document on every single
    /// switch back to a layout whose documents never actually closed - measured, this made an
    /// unrelated integration test (IlSpyAddInTests' reference-click-navigation step) flake at a
    /// ~75% rate by fighting its own subsequent navigation for `ActiveViewContent`. Skipping
    /// already-open documents entirely (no reopen, no reselect) makes a same-session layout switch
    /// with nothing new to restore a true no-op, exactly as it was before this slice existed.
    /// </summary>
    internal static void ReopenDocuments(LayoutSnapshot snapshot)
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        foreach (var documentSnapshot in snapshot.Documents)
        {
            var fileName = ICSharpCode.Core.FileName.Create(documentSnapshot.FileName);
            if (ICSharpCode.SharpDevelop.SD.FileService.GetOpenFile(fileName) != null)
                continue; // Already open - leave it (and whatever currently has focus) alone.
            try
            {
                ICSharpCode.SharpDevelop.SD.FileService.OpenFile(fileName, switchToOpenedView: documentSnapshot.IsActive);
            }
            catch (Exception ex)
            {
                ICSharpCode.Core.LoggingService.Warn($"Could not reopen document '{documentSnapshot.FileName}' from the restored layout.", ex);
            }
        }
    }

    static LayoutNodeSnapshot ConvertToSnapshot(ILayoutPanelElement element)
    {
        switch (element)
        {
            case LayoutAnchorablePane anchorablePane:
                return new LayoutAnchorablePaneSnapshot
                {
                    Name = anchorablePane.Name,
                    DockWidth = anchorablePane.DockWidth.IsAbsolute ? anchorablePane.DockWidth.Value : (double?)null,
                    DockHeight = anchorablePane.DockHeight.IsAbsolute ? anchorablePane.DockHeight.Value : (double?)null,
                    Anchorables = anchorablePane.Children.Select(a => new AnchorableSnapshot
                    {
                        ContentId = a.ContentId,
                        IsSelected = a.IsSelected,
                        IsVisible = a.IsVisible,
                    }).ToList(),
                };

            case LayoutDocumentPane:
            case LayoutDocumentPaneGroup:
                return new LayoutDocumentAreaSnapshot();

            case ILayoutOrientableGroup orientable when element is ILayoutContainer container:
                return new LayoutSplitSnapshot
                {
                    Orientation = orientable.Orientation,
                    Children = container.Children.OfType<ILayoutPanelElement>().Select(ConvertToSnapshot).ToList(),
                };

            default:
                // Anything else (a lone anchorable pane group variant, etc.) isn't part of this
                // slice's modeled shape - preserved as an opaque "document area" placeholder
                // rather than silently dropped, so Capture never throws on an unexpected node.
                return new LayoutDocumentAreaSnapshot();
        }
    }

    public static void Apply(LayoutRoot root, LayoutSnapshot snapshot)
    {
        if (root == null)
            throw new ArgumentNullException(nameof(root));
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.SchemaVersion != LayoutSnapshot.CurrentSchemaVersion)
            throw new NotSupportedException($"Unsupported layout snapshot schema version {snapshot.SchemaVersion} (expected {LayoutSnapshot.CurrentSchemaVersion}).");

        var existingAnchorables = root.Descendents().OfType<LayoutAnchorable>()
            .ToDictionary(a => a.ContentId, a => a, StringComparer.Ordinal);
        // The live tree's document area (pane or pane group) is carried over as-is wherever the
        // snapshot has a LayoutDocumentAreaSnapshot placeholder - there is exactly one document
        // area in every layout this converter deals with (see file header: content isn't modeled).
        var existingDocumentArea = root.Descendents().OfType<ILayoutPanelElement>()
            .FirstOrDefault(e => e is LayoutDocumentPane or LayoutDocumentPaneGroup);

        var pendingState = new List<(LayoutAnchorable Anchorable, AnchorableSnapshot Snapshot)>();
        var rebuilt = Rebuild(snapshot.Root, existingAnchorables, existingDocumentArea, pendingState);
        root.RootPanel = rebuilt as LayoutPanel ?? new LayoutPanel(rebuilt);

        // Applied only after RootPanel is assigned: Hide()/Show() reparent the anchorable (moving
        // it into/out of LayoutRoot's own Hidden collection), which only produces the intended
        // final state once each anchorable's Parent is the real, now-live pane from the rebuilt
        // tree above, not whatever it was reparented to mid-rebuild.
        foreach (var (anchorable, anchorableSnapshot) in pendingState)
        {
            anchorable.CanDockAsTabbedDocument = false;
            anchorable.IsSelected = anchorableSnapshot.IsSelected;
            if (anchorableSnapshot.IsVisible && anchorable.IsHidden)
                anchorable.Show();
            else if (!anchorableSnapshot.IsVisible && !anchorable.IsHidden)
                anchorable.Hide();
        }
    }

    static ILayoutPanelElement Rebuild(LayoutNodeSnapshot node, Dictionary<string, LayoutAnchorable> existingAnchorables,
        ILayoutPanelElement existingDocumentArea, List<(LayoutAnchorable Anchorable, AnchorableSnapshot Snapshot)> pendingState)
    {
        switch (node)
        {
            case LayoutAnchorablePaneSnapshot paneSnapshot:
                var pane = new LayoutAnchorablePane { Name = paneSnapshot.Name };
                if (paneSnapshot.DockWidth is double width)
                    pane.DockWidth = new System.Windows.GridLength(width);
                if (paneSnapshot.DockHeight is double height)
                    pane.DockHeight = new System.Windows.GridLength(height);
                foreach (var anchorableSnapshot in paneSnapshot.Anchorables)
                {
                    if (!existingAnchorables.TryGetValue(anchorableSnapshot.ContentId, out var anchorable))
                        continue; // Not currently registered (e.g. an AddIn pane not yet activated) - skip, don't fabricate.
                    pane.Children.Add(anchorable);
                    pendingState.Add((anchorable, anchorableSnapshot));
                }
                return pane;

            case LayoutDocumentAreaSnapshot:
                return existingDocumentArea ?? new LayoutDocumentPane();

            case LayoutSplitSnapshot splitSnapshot:
                var panel = new LayoutPanel { Orientation = splitSnapshot.Orientation };
                foreach (var child in splitSnapshot.Children)
                    panel.Children.Add(Rebuild(child, existingAnchorables, existingDocumentArea, pendingState));
                // One-time compatibility for the former JSON persistence format: it stored a
                // fixed size on a pane nested inside a single-child split, but not on the split
                // that the parent grid actually sizes. Carry that value outward while importing.
                if (splitSnapshot.Children.Count == 1 && splitSnapshot.Children[0] is LayoutAnchorablePaneSnapshot onlyPane)
                {
                    if (onlyPane.DockWidth is double wrapperWidth)
                        panel.DockWidth = new System.Windows.GridLength(wrapperWidth);
                    if (onlyPane.DockHeight is double wrapperHeight)
                        panel.DockHeight = new System.Windows.GridLength(wrapperHeight);
                }
                return panel;

            default:
                throw new NotSupportedException($"Unknown layout snapshot node type '{node?.GetType().Name}'.");
        }
    }
}
