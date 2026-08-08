using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Xml;

using AvalonDock;
using AvalonDock.Core;
using AvalonDock.Core.Serialization;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.ViewModels;

using TomsToolbox.Composition;

namespace ICSharpCode.SharpDevelop.Workbench;

internal sealed class DockWorkspace : ObservableObjectBase, ILayoutUpdateStrategy, IPaneModelHost
{
    // Bumped whenever the persisted layout format changes in a way that's not just "more
    // ToolPaneModel ContentIds" (e.g. if documents start being persisted, or IsVisible semantics
    // change again - see LayoutSerializationCallback below). Stamped onto every layout file we
    // write (SaveLayout) and checked on every restore (RestoreLayout) so a version mismatch is a
    // deliberate, loggable decision rather than however XmlLayoutSerializer happens to react to
    // unrecognized XML (previously: silently caught as FileFormatException, see
    // AvalonDockLayout.TryLoadConfiguration). The shipped data/layouts/*.xml templates carry this
    // same attribute (see doc/technotes/ilspy.md).

    private readonly DockingManager dockingManager;
    private readonly ObservableCollection<AvalonWorkbenchWindow> documents = new ObservableCollection<AvalonWorkbenchWindow>();
    private readonly ObservableCollection<ToolPaneModel> toolPanes = new ObservableCollection<ToolPaneModel>();
    private ReadOnlyObservableCollection<ToolPaneModel> toolPanesView;

    public DockWorkspace(DockingManager dockingManager)
    {
        this.dockingManager = dockingManager;
        Current = this;
        // PaneModel.CloseCommand resolves this to call back into Remove() below - see
        // IPaneModelHost's doc comment (ViewModels/PaneModel.cs) for why this indirection exists
        // (PaneModel now lives in the Base project, reachable from every AddIn; DockWorkspace
        // stays App-project-internal).
        SD.Services.AddService(typeof(IPaneModelHost), this);
    }

    public static DockWorkspace Current { get; private set; }

    /// <summary>
    /// The live AvalonDock layout tree, for callers that need to inspect/mutate it directly (e.g.
    /// <see cref="LayoutSnapshotConverter"/> and its DevFlow test actions) - <c>dockingManager</c>
    /// itself stays private since nothing outside this class should touch AvalonDock APIs other
    /// than the layout tree.
    /// </summary>
    internal LayoutRoot Layout => dockingManager.Layout;

    /// <summary>
    /// MEF-exported tool panes plus any panes added at runtime via <see cref="AddToolPane"/>
    /// (e.g. hosted addin panes that aren't MEF parts of this assembly, such as ILSpy's).
    /// Backed by an ObservableCollection so additions/removals after the initial MEF scan are
    /// picked up live by the AnchorablesSource binding, the same way <see cref="Documents"/>
    /// already reflects <see cref="AddDocument"/>/<see cref="RemoveDocument"/>.
    /// </summary>
    public ReadOnlyObservableCollection<ToolPaneModel> ToolPanes {
        get {
            if (toolPanesView == null) {
                // Constructed one part at a time via GetExports(...).Value rather than in one
                // GetExportedValues() enumeration (doc/technotes/ilspy.md "Docking and layout
                // replacement" item 4, 2026-08-03). The old one-shot form had three real defects,
                // all of which this getter's own laziness turned into silent state corruption:
                //   1. GetExportedValues() is lazy and OrderBy() buffers it, so ANY single part
                //      whose constructor threw aborted the whole enumeration - every remaining
                //      pane silently vanished from the workbench with no error surfaced anywhere
                //      (measured: adding one new pane whose ctor touched SD.Workbench too early
                //      made the entire MEF-backed pane set disappear, leaving only runtime-added
                //      ILSpy panes, no exception logged).
                //   2. `toolPanesView` was assigned only AFTER the loop, so a throw left it null
                //      while `toolPanes` was already partly filled - the next access re-enumerated
                //      and re-added the same panes, duplicating them.
                //   3. That made the failure timing-dependent and therefore nondeterministic: the
                //      same part constructs fine once the service it touches exists, so whether a
                //      pane appeared depended on which code path happened to touch ToolPanes first.
                // Materializing into a local list first, and guarding each part, means one broken
                // pane costs exactly that pane (logged, by type name) instead of the whole set.
                var loaded = new List<ToolPaneModel>();
                foreach (var export in OpenDevelopMefHost.ExportProvider.GetExports<ToolPaneModel, IMetadata>("ToolPane")) {
                    try {
                        var pane = export.Value;
                        if (pane == null) {
                            LoggingService.Error("A ToolPane MEF export produced a null value - skipping it.");
                            continue;
                        }
                        loaded.Add(pane);
                    } catch (Exception ex) {
                        LoggingService.Error("Failed to create tool pane from MEF export - skipping it.", ex);
                    }
                }
                foreach (var pane in loaded.OrderBy(item => item.Title))
                    toolPanes.Add(pane);
                toolPanesView = new ReadOnlyObservableCollection<ToolPaneModel>(toolPanes);
            }
            return toolPanesView;
        }
    }

    /// <summary>
    /// Adds a tool pane that isn't a MEF part of this assembly (e.g. an adapter wrapping a
    /// hosted addin's own pane view-model) so it shows up alongside the built-in pads.
    /// </summary>
    public void AddToolPane(ToolPaneModel pane)
    {
        _ = ToolPanes; // ensure the MEF-backed panes are loaded first
        if (!toolPanes.Contains(pane))
            toolPanes.Add(pane);
    }

    public void RemoveToolPane(ToolPaneModel pane)
    {
        toolPanes.Remove(pane);
    }

    public ReadOnlyObservableCollection<AvalonWorkbenchWindow> Documents { get; private set; }

    public AvalonWorkbenchWindow ActiveDocument {
        get => dockingManager.ActiveContent as AvalonWorkbenchWindow;
        set => dockingManager.ActiveContent = value;
    }

    public void AddDocument(AvalonWorkbenchWindow document, bool activate)
    {
        documents.Add(document);
        document.IsVisible = true;
        if (activate) {
            document.IsSelected = true;
            document.IsActive = true;
            ActiveDocument = document;
        }
    }

    public void RemoveDocument(AvalonWorkbenchWindow document)
    {
        documents.Remove(document);
    }

    public bool ContainsToolPane(string contentId)
    {
        return ToolPanes.Any(pane => pane.ContentId == contentId);
    }


    public bool ShowToolPane(string contentId)
    {
        var pane = ToolPanes.FirstOrDefault(p => p.ContentId == contentId);
        if (pane == null)
            return false;
        pane.Show();
        // pane.Show() only flips the model's IsVisible. When the pane was re-added on demand (a
        // layout restore keeps exactly the panes a layout file lists, so a re-shown pad's
        // anchorable is freshly docked into an existing group), the anchorable is NOT
        // auto-activated, leaving the requested tab hidden behind whatever shares its group.
        // Select and activate it explicitly - the semantics ActivatePad gives legacy pads.
        if (Layout?.Descendents().OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId) is LayoutAnchorable anchorable)
        {
            anchorable.IsSelected = true;
            anchorable.IsActive = true;
        }
        return true;
    }

    public void Remove(PaneModel model)
    {
        if (model is AvalonWorkbenchWindow document) {
            document.CloseWindow(false);
        } else if (model is ToolPaneModel tool) {
            tool.IsVisible = false;
        }
    }

    public void Add(ToolPaneModel model) => AddToolPane(model);

    public void InitializeLayout()
    {
        Documents = new ReadOnlyObservableCollection<AvalonWorkbenchWindow>(documents);
        dockingManager.DataContext = this;
        dockingManager.LayoutUpdateStrategy = this;
    }

    public void BindSources()
    {
        dockingManager.SetBinding(DockingManager.AnchorablesSourceProperty, new Binding(nameof(ToolPanes)) { Source = this });
        dockingManager.SetBinding(DockingManager.DocumentsSourceProperty, new Binding(nameof(Documents)) { Source = this });
    }

    /// <summary>
    /// Restores AvalonDock XML layouts. The former OpenDevelop-owned JSON snapshot format remains
    /// readable only as a one-time migration path and is rewritten to XML immediately after import.
    /// </summary>
    public void RestoreLayout(string fileName)
    {
        if (!File.Exists(fileName))
            return;

        string content = File.ReadAllText(fileName).TrimStart();
        if (content.StartsWith("{", StringComparison.Ordinal)) {
            RestoreLayoutFromSnapshot(fileName, content);
            SaveLayout(fileName);
            return;
        }

        var serializer = new XmlLayoutSerializer(dockingManager);
        serializer.LayoutSerializationCallback += LayoutSerializationCallback;
        try {
            serializer.Deserialize(fileName);
        } finally {
            serializer.LayoutSerializationCallback -= LayoutSerializationCallback;
        }
    }

    void RestoreLayoutFromSnapshot(string fileName, string json)
    {
        LayoutSnapshot snapshot;
        try {
            snapshot = JsonSerializer.Deserialize<LayoutSnapshot>(json);
        } catch (JsonException ex) {
            LoggingService.Warn($"Layout file '{fileName}' looked like JSON but failed to parse as a layout snapshot - falling back to template.", ex);
            throw new FileFormatException(new Uri(fileName, UriKind.RelativeOrAbsolute));
        }
        if (snapshot == null || snapshot.Root == null || snapshot.SchemaVersion != LayoutSnapshot.CurrentSchemaVersion) {
            LoggingService.Warn($"Layout file '{fileName}' has layout-snapshot schema version " +
                $"{snapshot?.SchemaVersion} (expected {LayoutSnapshot.CurrentSchemaVersion}) - falling back to template.");
            throw new FileFormatException(new Uri(fileName, UriKind.RelativeOrAbsolute));
        }
        LayoutSnapshotConverter.Apply(dockingManager.Layout, snapshot);
        // Step 3 (doc/technotes/ilspy.md, "Real versioned layout DTO"): reopen whichever real or
        // virtual documents this snapshot recorded, same pipeline as switching layouts brought the
        // panes back - documents are addressed by SD.FileService.OpenFile's own dispatch, not by
        // this converter constructing any view content directly.
        //
        // Only when a solution/project is actually loaded, though - this restore runs off the
        // dockingManager's Loaded event, which fires from app.Run() in WorkbenchStartup.Run, i.e.
        // strictly after that method has already decided (command line / LoadPrevProjectOnStartup)
        // whether to open a solution. Reopening loose files from the last session on a bare
        // "start with nothing loaded" run isn't wanted (they'd have no project context anyway).
        if (SD.ProjectService.CurrentSolution != null)
            LayoutSnapshotConverter.ReopenDocuments(snapshot);
    }

    public void SaveLayout(string fileName)
    {
        new XmlLayoutSerializer(dockingManager).Serialize(fileName);
    }

    private void LayoutSerializationCallback(object sender, LayoutSerializationCallbackEventArgs e)
    {
        if (e.Model is LayoutDocument) {
            e.Cancel = true;
            return;
        }

        if (e.Model is not LayoutAnchorable anchorable) {
            e.Cancel = true;
            return;
        }

        var pane = ToolPanes.FirstOrDefault(p => p.ContentId == anchorable.ContentId);
        if (pane == null) {
            e.Cancel = true;
            return;
        }

        e.Content = pane;
        anchorable.CanDockAsTabbedDocument = false;
        // Preserve whichever visibility was actually saved (doc/technotes/ilspy.md "real
        // versioned layout DTO") - this used to force IsVisible = true unconditionally, so a pane
        // the user had explicitly hidden before closing the IDE silently reappeared on every
        // restore.
        pane.IsVisible = anchorable.IsVisible;
    }

    public bool BeforeInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableToShow, ILayoutContainer destinationContainer)
    {
        anchorableToShow.CanDockAsTabbedDocument = false;

        // Consult ToolPaneModel.PreferredDockSide (previously declared but never read - see its
        // own doc comment) for a pane that isn't already part of the persisted layout: without
        // this, AvalonDock's AttachAnchorablesSource import (DockingManager.cs) falls back to
        // whatever pane currently hosts the active content (or the first pane it finds), so e.g.
        // the Output pad ends up tab-docked behind an unrelated pane instead of its own "Bottom"
        // strip - it's technically shown, just not anywhere the user would look for it, which
        // reads as "the Output panel wasn't shown immediately" when Build first activates it.
        if (anchorableToShow.Content is ToolPaneModel pane && pane.PreferredDockSide is PreferredDockSide side) {
            // ContentId is otherwise unset at this point and lazily derives from Content.Name on
            // first read (LayoutContent.SetContentIdFromContent) - which won't match the model's
            // own ContentId (our Content is the ToolPaneModel itself, not a named visual). Without
            // this, ShowToolPane's later `a.ContentId == contentId` lookup (to select/activate the
            // anchorable) never finds this one, so the pad docks correctly but never becomes the
            // front-most tab in its group.
            anchorableToShow.ContentId = pane.ContentId;
            anchorableToShow.AddToLayout(dockingManager, ToShowStrategy(side));
            // AddToLayout only touches the tree (Parent); LayoutAnchorable.IsSelected is a plain
            // flag, not tree-derived like IsVisible (`Parent != null && Parent is not LayoutRoot`),
            // so a freshly-added anchorable defaults to unselected - i.e. a background tab in
            // whatever pane it landed in, indistinguishable from the original bug this whole method
            // exists to fix. Set both the anchorable and the model directly rather than relying on
            // the style's Model.IsSelected/IsActive TwoWay bindings (those target the generated
            // LayoutItem chrome, not this LayoutAnchorable, and its sync back to the model wasn't
            // reliably observed here) to guarantee the pad is the visible tab immediately.
            anchorableToShow.IsSelected = true;
            anchorableToShow.IsActive = true;
            pane.IsSelected = true;
            pane.IsActive = true;
            return true;
        }
        return false;
    }

    static AnchorableShowStrategy ToShowStrategy(PreferredDockSide side) => side switch {
        PreferredDockSide.Left => AnchorableShowStrategy.Left,
        PreferredDockSide.Right => AnchorableShowStrategy.Right,
        PreferredDockSide.Top => AnchorableShowStrategy.Top,
        PreferredDockSide.Bottom => AnchorableShowStrategy.Bottom,
        _ => AnchorableShowStrategy.Most,
    };

    public void AfterInsertAnchorable(LayoutRoot layout, LayoutAnchorable anchorableShown)
    {
        anchorableShown.IsActive = true;
        anchorableShown.IsSelected = true;

        // Host-neutral pane/workspace contract vertical slice (doc/technotes/ilspy.md "Immediate
        // next actions" #3, 2026-08-02): PreferredDockSize replaces what used to be a single
        // `ContentId == "ProjectBrowser"` special case, so any ToolPaneModel can express a docked
        // size preference instead of only the one pane the old code happened to special-case.
        // The size applies to the pane's axis: a pane in a horizontally-stacked panel (left/right
        // side) gets DockWidth, one in a vertically-stacked panel (top/bottom strip) gets
        // DockHeight - the legacy AvalonPadContent.ShowInDefaultPosition path sizes exactly this
        // way (DockWidth 250/280 for left/right, DockHeight 188 for bottom), and without it a
        // re-docked pane collapses to a tab-row-only strip (0-height content viewport, so
        // virtualized tree/list content never realizes).
        // The anchorable's ContentId is not always the model's ContentId: AvalonDock falls back to
        // the content control's Name when ContentId is unset (LayoutContent.SetContentIdFromContent),
        // which for a migrated pad ends up being the legacy pad class full name (e.g.
        // "ICSharpCode.SharpDevelop.Editor.Search.SearchResultsPad" vs model ContentId
        // "SearchResultsPad") - so match on the LegacyPadClass too, or the size preference is
        // silently never applied.
        var pane = ToolPanes.FirstOrDefault(p => p.ContentId == anchorableShown.ContentId || p.LegacyPadClass == anchorableShown.ContentId);
        if (pane?.PreferredDockSize is double size && anchorableShown.Parent is LayoutAnchorablePane layoutPane) {
            if (IsInVerticallyStackedPanel(layoutPane))
                layoutPane.DockHeight = new GridLength(size);
            else
                layoutPane.DockWidth = new GridLength(size);
        }
    }

    static bool IsInVerticallyStackedPanel(LayoutAnchorablePane pane)
    {
        // The pane's docking axis is the orientation of the first LayoutPanel ancestor: a
        // top/bottom strip is a vertically-stacked panel (its panes' height is the sized axis),
        // a left/right side is horizontally-stacked (width).
        ILayoutContainer parent = pane.Parent;
        while (parent != null) {
            if (parent is LayoutPanel panel)
                return panel.Orientation == Orientation.Vertical;
            parent = parent.Parent;
        }
        return false;
    }

    public bool BeforeInsertDocument(LayoutRoot layout, LayoutDocument anchorableToShow, ILayoutContainer destinationContainer)
    {
        return false;
    }

    public void AfterInsertDocument(LayoutRoot layout, LayoutDocument anchorableShown)
    {
    }
}

/// <summary>
/// Public seam for external addins (hosted panes that aren't MEF parts of this assembly, e.g.
/// ILSpy's) to add/remove pads without exposing the rest of <see cref="DockWorkspace"/>'s
/// internal surface (which references internal types like AvalonWorkbenchWindow).
/// </summary>
public static class DockWorkspaceExtensibility
{
    public static void AddToolPane(ToolPaneModel pane) => DockWorkspace.Current?.AddToolPane(pane);

    public static void RemoveToolPane(ToolPaneModel pane) => DockWorkspace.Current?.RemoveToolPane(pane);
}
