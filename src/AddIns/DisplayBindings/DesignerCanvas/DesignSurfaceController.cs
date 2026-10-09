using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Threading;

using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Surface;

/// <summary>What a designer's elements are selected by.</summary>
public enum DesignSurfaceKeying
{
	/// <summary>By x:Name: only elements the document names are selectable by key (the rest by
	/// tree path). The WinUI designers, whose shell edits the XAML source by name.</summary>
	Name,
	/// <summary>By the backend's element <see cref="DesignerElementNode.Id"/>: every designable
	/// element is selectable, named or not. Designers whose host owns the model (MAUI, ...).</summary>
	Id,
}

/// <summary>
/// The backend-neutral half of a designer: drives a <see cref="DesignSurface"/> from the design
/// state a backend produces (a <see cref="DesignerSessionState"/>: frame + element tree in design
/// units) and turns the user's gestures on it into intents - selection, single and group drags with
/// snapping, handle resizes, double-clicks, inline text, Grid guides, nudges, undo/redo, context
/// commands. It knows no UI framework. Selection and hit testing are derived from the snapshot
/// locally; a pointer press must never wait for a child-process RPC. A designer owns one controller
/// per document and forwards its own view contract to it.
/// </summary>
public sealed class DesignSurfaceController : IDisposable
{
	readonly DesignSurfaceKeying keying;
	readonly Dispatcher dispatcher;
	readonly HashSet<string> selectableNames = new(StringComparer.Ordinal);
	readonly List<string> multiSelectionNames = new();
	Dictionary<string, DesignerElementNode> nodesByName = new(StringComparer.Ordinal);
	bool showTabOrder;
	bool snapToGrid;
	bool snapToGuides = true;
	double gridCellSize = 20;
	bool disposed;

	/// <param name="keying">What selection, drags and events are keyed by: an element's x:Name
	/// (the default) or its backend Id. Every "name" this class reports is that key.</param>
	public DesignSurfaceController(DesignSurface surface, DesignSurfaceKeying keying = DesignSurfaceKeying.Name)
	{
		this.keying = keying;
		Surface = surface ?? throw new ArgumentNullException(nameof(surface));
		dispatcher = surface.Dispatcher;
		surface.SurfacePointerPressed += OnSurfacePointerPressed;
		// What a drag picks up: the element under the pointer, but never the design root - dragging
		// the page itself means nothing, so a drag there draws a selection marquee instead.
		surface.ElementResolver = point => ResolveNameAt(point) is { } key && !IsRootKey(key) ? key : null;
		surface.MarqueeSelectionCommitted += OnMarqueeSelectionCommitted;
		surface.SurfaceElementDragStarted += OnSurfaceElementDragStarted;
		surface.SurfaceElementDragDelta += OnSurfaceElementDragDelta;
		surface.SurfaceElementDragCommitted += OnSurfaceElementDragCommitted;
		surface.SurfaceLayoutInsetDragCommitted += OnSurfaceLayoutInsetDragCommitted;
		surface.SurfaceElementDoubleClicked += OnSurfaceElementDoubleClicked;
		surface.TextEditCommitted += OnSurfaceTextEditCommitted;
		surface.GridGuideDragCommitted += OnSurfaceGridGuideDragCommitted;
		surface.GridTrackSplitRequested += OnSurfaceGridTrackSplitRequested;
		surface.ContextCommandRequested += OnSurfaceContextCommandRequested;
		surface.NudgeRequested += OnSurfaceNudgeRequested;
		surface.UndoRedoRequested += OnSurfaceUndoRedoRequested;
	}

	public DesignSurface Surface { get; }

	/// <summary>The last state applied, or null before the first render.</summary>
	public DesignerSessionState? LastSnapshot { get; private set; }

	/// <summary>How many named elements the last tree indexed, for DevFlow.</summary>
	public int IndexedNameCount => nodesByName.Count;

	/// <summary>Why the last click selected what it did (or nothing), for DevFlow.</summary>
	public string LastPickDiagnostic { get; private set; } = "no click yet";

	#region Events

	/// <summary>A click or a drag start picked this source element: select it.</summary>
	public event EventHandler<string>? ElementPicked;
	/// <summary>A click picked an element the document never named, by tree path.</summary>
	public event EventHandler<string>? ElementPathPicked;
	/// <summary>The (possibly multiple) selection changed, primary first.</summary>
	public event EventHandler<IReadOnlyList<string>>? SelectionChanged;
	/// <summary>A single-element move or resize committed.</summary>
	public event EventHandler<ElementDragInfo>? ElementDragCommitted;
	/// <summary>A primary-selection Margin/Canvas inset drag ready for a backend-specific mutation.</summary>
	public event EventHandler<LayoutInsetEditInfo>? LayoutInsetEditCommitted;
	/// <summary>A multi-selection group move committed, with each element's delta.</summary>
	public event EventHandler<IReadOnlyList<(string Name, double DX, double DY)>>? ElementGroupDragCommitted;
	/// <summary>A double-click on an element (null: empty space).</summary>
	public event EventHandler<ElementDoubleClickInfo?>? ElementDoubleClicked;
	/// <summary>The inline text editor committed this text.</summary>
	public event EventHandler<string>? TextEditCommitted;
	/// <summary>A Grid row/column divider drag committed (name, isRow, index, design position).</summary>
	public event EventHandler<(string Name, bool IsRow, int Index, double Position)>? GridGuideDragCommitted;
	/// <summary>A requested atomic split inside a selected Grid (name, row/column, local position).</summary>
	public event EventHandler<(string Name, bool IsRow, double Position)>? GridTrackSplitRequested;
	/// <summary>Arrow-key nudge of the selection (design units).</summary>
	public event EventHandler<(double DX, double DY)>? NudgeRequested;
	/// <summary>Ctrl+Z (true) / Ctrl+Y (false) on the surface.</summary>
	public event EventHandler<bool>? UndoRedoRequested;
	/// <summary>A context-menu command and the primary selection.</summary>
	public event EventHandler<(string Command, string Name)>? ContextCommandRequested;

	void OnSurfaceContextCommandRequested(object? sender, (string Command, string Name) args) => ContextCommandRequested?.Invoke(this, args);
	void OnSurfaceGridGuideDragCommitted(object? sender, (string Name, bool IsRow, int Index, double Position) args) => GridGuideDragCommitted?.Invoke(this, args);
	void OnSurfaceGridTrackSplitRequested(object? sender, (string Name, bool IsRow, double Position) args) => GridTrackSplitRequested?.Invoke(this, args);
	void OnSurfaceNudgeRequested(object? sender, (double DX, double DY) delta) => NudgeRequested?.Invoke(this, delta);
	void OnSurfaceUndoRedoRequested(object? sender, bool undo) => UndoRedoRequested?.Invoke(this, undo);
	void OnSurfaceLayoutInsetDragCommitted(object? sender, (string Edge, double Value) edit)
	{
		if (SelectedElementName is not { } name || !nodesByName.TryGetValue(name, out var node)
			|| node.LayoutInsets is not { Kind.Length: > 0 } insets)
			return;
		LayoutInsetEditCommitted?.Invoke(this, new LayoutInsetEditInfo {
			Name = name, Kind = insets.Kind, Edge = edit.Edge, Value = edit.Value
		});
	}
	#endregion

	#region State

	/// <summary>
	/// Presents a backend's state: frame, element tree (the index every selection and drag works
	/// from), visual-state groups and component tray. Must run on the UI thread.
	/// </summary>
	public void ApplySnapshot(DesignerSessionState snapshot)
	{
		if (disposed || snapshot == null)
			return;
		LastSnapshot = snapshot;
		nodesByName = IndexTree(snapshot.Tree, keying);
		// Only snapshots that actually describe the document carry the groups. An early-return
		// error snapshot (no Tree) has an empty list that means "nothing to say", not "this
		// document has no states" - repopulating from one of those blanked the whole states panel
		// the moment any state failed to apply.
		if (snapshot.Tree != null || snapshot.VisualStateGroups.Count > 0)
			Surface.SetVisualStateGroups(snapshot.VisualStateGroups);
		Surface.SetComponentTray(snapshot.TrayComponents.Select(item => (item.Id, item.Name, item.Type)));
		if (showTabOrder)
			RefreshTabOrderBadges();
		if (snapshot.Render != null)
			Surface.SetRender(snapshot.Render);
	}

	/// <summary>The x:Names the source document declares: only these are selectable by name
	/// (<see cref="DesignSurfaceKeying.Name"/>; ignored when keyed by Id).</summary>
	public void SetSelectableNames(IEnumerable<string> names)
	{
		selectableNames.Clear();
		foreach (var name in names ?? Array.Empty<string>())
			selectableNames.Add(name);
	}

	public (double X, double Y, double Width, double Height)? QueryElementBounds(string name)
	{
		if (string.IsNullOrEmpty(name) || !nodesByName.TryGetValue(name, out var node))
			return null;
		return (node.X, node.Y, node.Width, node.Height);
	}

	/// <summary>Finds the topmost source-backed node at a design-space point in the latest
	/// snapshot. Non-pointer gestures such as a toolbox drop use this rather than opening a second
	/// child-process hit-test round-trip.</summary>
	public DesignerElementNode? FindNodeAtDesignPoint(double x, double y) => DesignerSnapshotHitTester.FindNodeAt(LastSnapshot?.Tree, x, y);

	public string DescribeElementState(string name)
	{
		if (string.IsNullOrEmpty(name) || !nodesByName.TryGetValue(name, out var node))
			return "not found";
		return $"type={node.Type} bounds=({node.X:F0},{node.Y:F0}) {node.Width:F0}x{node.Height:F0} children={node.Children.Count}";
	}

	/// <summary>Whether <paramref name="key"/> can be selected: a declared x:Name, or the Id of a
	/// designable element (never a template part).</summary>
	bool IsSelectable(string key) => keying == DesignSurfaceKeying.Id
		? nodesByName.TryGetValue(key, out var node) && node.IsDesignable
		: selectableNames.Contains(key);

	static string? KeyOf(DesignerElementNode node, DesignSurfaceKeying keying) =>
		keying == DesignSurfaceKeying.Id ? node.Id : node.Name;

	/// <summary>Whether <paramref name="key"/> names an element at all. An Id may be the empty
	/// string (a backend that ids elements by tree path gives its root ""); a name may not.</summary>
	bool HasKey(string? key) => keying == DesignSurfaceKeying.Id ? key != null : !string.IsNullOrEmpty(key);

	/// <summary>The caption for an element's selection outline: its key, or for an Id-keyed
	/// designer the element's x:Name, else its type.</summary>
	string? LabelOf(string? key)
	{
		if (keying == DesignSurfaceKeying.Name || key == null || !nodesByName.TryGetValue(key, out var node))
			return key;
		return node.Name ?? node.Type;
	}

	static Dictionary<string, DesignerElementNode> IndexTree(DesignerElementNode? node, DesignSurfaceKeying keying)
	{
		var index = new Dictionary<string, DesignerElementNode>(StringComparer.Ordinal);
		if (node == null)
			return index;
		void Walk(DesignerElementNode current)
		{
			var key = KeyOf(current, keying);
			if (keying == DesignSurfaceKeying.Id ? key != null : !string.IsNullOrEmpty(key))
				index[key!] = current;
			foreach (var child in current.Children)
				Walk(child);
		}
		Walk(node);
		return index;
	}

	#endregion

	#region Picking

	/// <summary>Answers a click with the innermost source-backed snapshot node.</summary>
	public string? ResolveNameAt(Vector2 point) => ResolveNameAtWithPath(point).Name;

	/// <summary>The source element under a point relative to the surface: by name when it has one
	/// the document declares, else by tree path.</summary>
	public (string? Name, string? PickPath) ResolveNameAtWithPath(Vector2 point)
	{
		var design = Surface.ToDesignPoint(new Point(point.X, point.Y));
		var picked = DesignerSnapshotHitTester.FindNodeAt(LastSnapshot?.Tree, design.X, design.Y);
		if (picked == null)
		{
			LastPickDiagnostic = $"point={design.X:F0},{design.Y:F0} local snapshot: empty";
			return (null, null);
		}
		LastPickDiagnostic = $"point={design.X:F0},{design.Y:F0} local snapshot path={picked.Path}";
		// Prefer the element's own name when it has one: selection, the Properties pad, the
		// outline and multi-select are all keyed by name.
		return KeyOf(picked, keying) is { } pickedKey && HasKey(pickedKey) && IsSelectable(pickedKey)
			? (pickedKey, null)
			: (null, picked.Path);
	}

	/// <summary>The node whose own <see cref="DesignerElementNode.Path"/> equals
	/// <paramref name="path"/>, or null. Matches on the stored Path rather than re-deriving child
	/// indices, because a backend may number a path by VISUAL child index while a node's Children
	/// holds only the designable ones. The document root's path is the empty string.</summary>
	public DesignerElementNode? FindNodeByPath(string? path)
	{
		if (LastSnapshot?.Tree is not { } root || path == null)
			return null;
		return Find(root);

		DesignerElementNode? Find(DesignerElementNode node)
		{
			if (string.Equals(node.Path, path, StringComparison.Ordinal))
				return node;
			// Paths are built by appending to the parent's, so only a prefix can contain it.
			if (path.Length != 0 && node.Path.Length != 0 && !path.StartsWith(node.Path + ",", StringComparison.Ordinal))
				return null;
			foreach (var child in node.Children)
			{
				if (Find(child) is { } found)
					return found;
			}
			return null;
		}
	}

	/// <summary>
	/// The element at <paramref name="path"/> and its ancestors, ROOT FIRST, each as its tag name
	/// plus that tag's occurrence index among designable elements - the pair a designer maps back
	/// to its source document. Template parts (non-designable nodes) are left out entirely, so the
	/// occurrence index counts the same elements the document does.
	/// </summary>
	public IReadOnlyList<(string Type, int TypeIndex, string Path)> GetPickChain(string path)
	{
		var result = new List<(string, int, string)>();
		if (LastSnapshot?.Tree is not { } root || path == null)
			return result;
		// Pre-order == document order, and every ancestor is visited before the target, so the
		// counts are complete for the whole chain by the time the target is reached.
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		var ancestors = new List<(string Type, int TypeIndex, string Path)>();
		Walk(root);
		return result;

		bool Walk(DesignerElementNode node)
		{
			var counted = false;
			if (node.IsDesignable && !string.IsNullOrEmpty(node.Type))
			{
				counts.TryGetValue(node.Type, out var seen);
				counts[node.Type] = seen + 1;
				ancestors.Add((node.Type, seen, node.Path));
				counted = true;
			}
			if (string.Equals(node.Path, path, StringComparison.Ordinal))
			{
				result.AddRange(ancestors);
				return true;
			}
			foreach (var child in node.Children)
			{
				if (Walk(child))
					return true;
			}
			if (counted)
				ancestors.RemoveAt(ancestors.Count - 1);
			return false;
		}
	}

	void OnSurfacePointerPressed(object? sender, (Vector2 Point, bool Ctrl) args)
	{
		var (name, pickPath) = ResolveNameAtWithPath(args.Point);
		if (name != null)
			ApplyPickSelection(name, args.Ctrl);
		else if (!string.IsNullOrEmpty(pickPath))
			ElementPathPicked?.Invoke(this, pickPath);
		else if (!args.Ctrl && ClearsSelectionOnEmptyClick)
			ClearSelectionInternal();
	}

	#endregion

	#region Selection

	/// <summary>Whether a plain click on empty canvas clears the selection. Off by default (the
	/// WinUI designers keep it, selecting from the outline instead).</summary>
	public bool ClearsSelectionOnEmptyClick { get; set; }

	/// <summary>The primary (single) selection's element name, kept in sync with the surface.</summary>
	public string? SelectedElementName { get; private set; }

	/// <summary>The currently selected element names, primary first.</summary>
	public IReadOnlyList<string> SelectedNames => multiSelectionNames.Count == 0 && SelectedElementName != null
		? new[] { SelectedElementName }
		: multiSelectionNames;

	/// <summary>Sets the multi-selection programmatically; the first name becomes the primary.</summary>
	public void SelectElements(IReadOnlyList<string> names)
	{
		if (names == null)
			return;
		multiSelectionNames.Clear();
		foreach (var name in names)
		{
			if (IsSelectable(name) && !multiSelectionNames.Contains(name))
				multiSelectionNames.Add(name);
		}
		if (multiSelectionNames.Count == 0)
			return;
		SelectElementInternal(multiSelectionNames[0]);
	}

	/// <summary>Selects a single element (from outline/properties/actions), resetting any multi-selection.</summary>
	public void SelectElement(string name)
	{
		if (!HasKey(name) || !IsSelectable(name))
			return;
		multiSelectionNames.Clear();
		multiSelectionNames.Add(name);
		SelectedElementName = name;
		RefreshSelectionOverlay();
		ShowSelection(name);
		SelectionChanged?.Invoke(this, new[] { name });
	}

	/// <summary>Draws the selection outline over the named element's design bounds.</summary>
	public void ShowSelection(string name)
	{
		if (!HasKey(name) || !nodesByName.TryGetValue(name, out var node))
		{
			Surface.ClearSelection();
			return;
		}
		Surface.ShowSelection(node.X, node.Y, node.Width, node.Height, name, LabelOf(name));
		Surface.SetLayoutInsets(node.LayoutInsets, new Rect(node.X, node.Y, node.Width, node.Height), node.Bindings);
	}

	/// <summary>
	/// Sets the selection to <paramref name="keys"/> (primary first; null keeps the current one)
	/// and redraws it from the current tree, WITHOUT raising <see cref="SelectionChanged"/>: for a
	/// designer that owns the selection and re-shows it after a re-render or an outline pick. Keys
	/// the tree no longer has are dropped.
	/// </summary>
	public void RestoreSelection(IReadOnlyList<string>? keys = null)
	{
		var wanted = (keys ?? SelectedNames).Where(key => HasKey(key) && nodesByName.ContainsKey(key)).Distinct().ToList();
		multiSelectionNames.Clear();
		multiSelectionNames.AddRange(wanted);
		if (wanted.Count == 0)
		{
			SelectedElementName = null;
			Surface.ClearSelection();
			Surface.SetSecondarySelection(Array.Empty<(string, double, double, double, double)>());
			return;
		}
		SelectedElementName = wanted[0];
		RefreshSelectionOverlay();
		ShowSelection(wanted[0]);
	}

	/// <summary>Draws the selection outline over the element at <paramref name="path"/> - the only
	/// way to show a selection for an element the document never named.</summary>
	public void ShowSelectionAtPath(string path, string? label)
	{
		if (FindNodeByPath(path) is not { } node)
		{
			Surface.ClearSelection();
			return;
		}
		SelectedElementName = null;
		multiSelectionNames.Clear();
		Surface.ShowSelection(node.X, node.Y, node.Width, node.Height, label ?? node.Type ?? "");
	}

	public void ClearSelection() => Surface.ClearSelection();

	void ApplyPickSelection(string name, bool ctrl)
	{
		if (!IsSelectable(name))
			return;
		if (ctrl)
		{
			if (!multiSelectionNames.Remove(name))
				multiSelectionNames.Add(name);
			if (multiSelectionNames.Count == 0)
			{
				// Ctrl-clicked the last one away: nothing selected.
				ClearSelectionInternal();
				return;
			}
		}
		else
		{
			multiSelectionNames.Clear();
			multiSelectionNames.Add(name);
		}
		SelectElementInternal(name);
	}

	void ClearSelectionInternal()
	{
		multiSelectionNames.Clear();
		Surface.ClearSelection();
		Surface.SetSecondarySelection(Array.Empty<(string, double, double, double, double)>());
		SelectedElementName = null;
		SelectionChanged?.Invoke(this, Array.Empty<string>());
	}

	void SelectElementInternal(string name)
	{
		SelectedElementName = name;
		RefreshSelectionOverlay();
		SelectionChanged?.Invoke(this, multiSelectionNames.Count > 0 ? multiSelectionNames.ToArray() : new[] { name });
	}

	void RefreshSelectionOverlay()
	{
		var secondary = new List<(string, double, double, double, double)>();
		foreach (var name in multiSelectionNames)
		{
			if (name != SelectedElementName && nodesByName.TryGetValue(name, out var node))
				secondary.Add((name, node.X, node.Y, node.Width, node.Height));
		}
		Surface.SetSecondarySelection(secondary);
	}

	#endregion

	bool IsRootKey(string key) =>
		LastSnapshot?.Tree is { } root && string.Equals(KeyOf(root, keying), key, StringComparison.Ordinal);

	/// <summary>
	/// A rubber-band selection: every selectable element (the root excepted) whose bounds intersect
	/// the marquee, in document order, the first becoming the primary. Resolved against the tree the
	/// canvas already holds - no backend round trip. An empty marquee clears the selection.
	/// </summary>
	void OnMarqueeSelectionCommitted(object? sender, Rect marquee)
	{
		var matches = new List<string>();
		if (LastSnapshot?.Tree is { } root)
		{
			void Walk(DesignerElementNode node)
			{
				if (!ReferenceEquals(node, root) && node.IsVisible && KeyOf(node, keying) is { Length: > 0 } key
					&& IsSelectable(key) && !matches.Contains(key)
					&& marquee.IntersectsWith(new Rect(node.X, node.Y, Math.Max(0, node.Width), Math.Max(0, node.Height))))
					matches.Add(key);
				foreach (var child in node.Children)
					Walk(child);
			}
			Walk(root);
		}
		multiSelectionNames.Clear();
		if (matches.Count == 0)
		{
			ClearSelectionInternal();
			return;
		}
		multiSelectionNames.AddRange(matches);
		SelectElementInternal(matches[0]);
		ShowSelection(matches[0]);
	}

	#region Drag (move, resize, group move, snapping)

	string? dragName;
	string? dragHandle;
	(double X, double Y, double Width, double Height) dragStartRect;
	// Multi-selection drag: the elements being dragged as a group (primary + secondaries).
	List<string> dragGroup = new();
	Dictionary<string, (double X, double Y, double Width, double Height)> dragGroupStart = new();
	double dragDeltaX;
	double dragDeltaY;

	void OnSurfaceElementDragStarted(object? sender, (string Name, string Handle) info)
	{
		dragName = info.Name;
		dragHandle = info.Handle;
		dragDeltaX = 0;
		dragDeltaY = 0;
		dragStartRect = nodesByName.TryGetValue(info.Name, out var node)
			? (node.X, node.Y, node.Width, node.Height)
			: Surface.CurrentSelection;
		// Dragging a multi-selected element moves the whole group; otherwise it is a plain
		// single-element drag. Handle resizes stay single-element.
		dragGroup = string.IsNullOrEmpty(dragHandle) && multiSelectionNames.Contains(info.Name)
			? new List<string>(multiSelectionNames)
			: new List<string> { info.Name };
		dragGroupStart = new Dictionary<string, (double, double, double, double)>(StringComparer.Ordinal);
		foreach (var name in dragGroup)
		{
			dragGroupStart[name] = nodesByName.TryGetValue(name, out var n)
				? (n.X, n.Y, n.Width, n.Height)
				: Surface.CurrentSelection;
		}
		// Selecting the dragged element keeps the Properties pad and outline in sync.
		ElementPicked?.Invoke(this, info.Name);
	}

	void OnSurfaceElementDragDelta(object? sender, (double DX, double DY) delta)
	{
		if (dragName == null)
			return;
		var scale = Surface.ViewportScale;
		dragDeltaX = delta.DX / scale;
		dragDeltaY = delta.DY / scale;
		// Snap the primary element's edges/centre to nearby elements and show alignment guides.
		var guides = (IReadOnlyList<(bool, double)>)Array.Empty<(bool, double)>();
		if (string.IsNullOrEmpty(dragHandle))
		{
			if (snapToGrid)
				(dragDeltaX, dragDeltaY) = RasterGridCalculator.SnapMove(
					dragStartRect.X, dragStartRect.Y, dragDeltaX, dragDeltaY, gridCellSize);
			if (snapToGuides)
				(dragDeltaX, dragDeltaY, guides) = ApplySnap(dragDeltaX, dragDeltaY);
		}
		else
			(dragDeltaX, dragDeltaY, guides) = snapToGuides
				? ApplyResizeSnap(dragDeltaX, dragDeltaY)
				: (dragDeltaX, dragDeltaY, guides);
		Surface.SetSnapGuides(guides);
		var rect = ApplyHandle(dragStartRect, dragDeltaX, dragDeltaY);
		Surface.ShowSelection(rect.X, rect.Y, rect.Width, rect.Height, dragName, LabelOf(dragName));
		if (dragGroup.Count > 1)
		{
			// Move the secondary outlines with the group so the whole selection tracks.
			var secondary = new List<(string, double, double, double, double)>();
			foreach (var name in dragGroup)
			{
				if (name == dragName || !dragGroupStart.TryGetValue(name, out var start))
					continue;
				secondary.Add((name, start.X + dragDeltaX, start.Y + dragDeltaY, start.Width, start.Height));
			}
			Surface.SetSecondarySelection(secondary);
		}
	}

	(double DX, double DY, IReadOnlyList<(bool IsVertical, double Position)> Guides) ApplySnap(double deltaX, double deltaY)
	{
		if (dragName == null || !dragGroupStart.TryGetValue(dragName, out var start))
			return (deltaX, deltaY, Array.Empty<(bool, double)>());
		var siblingBounds = nodesByName
			.Where(entry => entry.Key != dragName)
			.Select(entry => (entry.Value.X, entry.Value.Y, entry.Value.Width, entry.Value.Height));
		// Alignment guides should describe a relationship visible to the user: do not snap an
		// element to an unrelated row/column that happens to share an edge coordinate.
		return SnapGuideCalculator.ApplySnap(start, deltaX, deltaY, siblingBounds, requireOverlap: true);
	}

	(double DX, double DY, IReadOnlyList<(bool IsVertical, double Position)> Guides) ApplyResizeSnap(double deltaX, double deltaY)
	{
		if (dragName == null || !dragGroupStart.TryGetValue(dragName, out var start) || string.IsNullOrEmpty(dragHandle))
			return (deltaX, deltaY, Array.Empty<(bool, double)>());
		var siblings = nodesByName.Where(entry => entry.Key != dragName)
			.Select(entry => (entry.Value.X, entry.Value.Y, entry.Value.Width, entry.Value.Height)).ToArray();
		var raw = ApplyHandle(start, deltaX, deltaY);
		var guides = new List<(bool, double)>();
		if (dragHandle.Contains('e') || dragHandle.Contains('w'))
		{
			var edge = dragHandle.Contains('e') ? raw.X + raw.Width : raw.X;
			var snapped = SnapGuideCalculator.SnapEdge(true, edge, raw.Y, raw.Y + raw.Height, siblings);
			deltaX += snapped.Correction;
			if (snapped.Guide is double guide) guides.Add((true, guide));
		}
		if (dragHandle.Contains('n') || dragHandle.Contains('s'))
		{
			var edge = dragHandle.Contains('s') ? raw.Y + raw.Height : raw.Y;
			var snapped = SnapGuideCalculator.SnapEdge(false, edge, raw.X, raw.X + raw.Width, siblings);
			deltaY += snapped.Correction;
			if (snapped.Guide is double guide) guides.Add((false, guide));
		}
		return (deltaX, deltaY, guides);
	}

	void OnSurfaceElementDragCommitted(object? sender, (double DX, double DY) delta)
	{
		if (dragName == null)
			return;
		// dragDeltaX/dragDeltaY were already updated by the last drag delta, including any snap
		// correction - do NOT recompute from the raw delta here, or the snap would be lost.
		if (dragGroup.Count > 1)
		{
			var committed = new List<(string, double, double)>(dragGroup.Count);
			foreach (var name in dragGroup)
				committed.Add((name, dragDeltaX, dragDeltaY));
			Surface.SetSnapGuides(Array.Empty<(bool, double)>());
			ElementGroupDragCommitted?.Invoke(this, committed);
			dragName = null;
			dragGroup = new();
			dragGroupStart.Clear();
			return;
		}
		var end = ApplyHandle(dragStartRect, dragDeltaX, dragDeltaY);
		Surface.SetSnapGuides(Array.Empty<(bool, double)>());
		ElementDragCommitted?.Invoke(this, new ElementDragInfo {
			Name = dragName,
			StartX = dragStartRect.X,
			StartY = dragStartRect.Y,
			StartWidth = dragStartRect.Width,
			StartHeight = dragStartRect.Height,
			EndX = end.X,
			EndY = end.Y,
			EndWidth = end.Width,
			EndHeight = end.Height
		});
		dragName = null;
	}

	/// <summary>Applies a move/resize delta to a design rect for the drag's handle, keeping the
	/// result at least 1 unit wide/tall so a shrink-past-zero drag cannot crash the outline.</summary>
	(double X, double Y, double Width, double Height) ApplyHandle(
		(double X, double Y, double Width, double Height) rect, double dx, double dy)
	{
		double width;
		double height;
		double rx;
		double ry;
		switch (dragHandle)
		{
			case "e": rx = rect.X; ry = rect.Y; width = rect.Width + dx; height = rect.Height; break;
			case "s": rx = rect.X; ry = rect.Y; width = rect.Width; height = rect.Height + dy; break;
			case "se": rx = rect.X; ry = rect.Y; width = rect.Width + dx; height = rect.Height + dy; break;
			case "w": rx = rect.X + dx; ry = rect.Y; width = rect.Width - dx; height = rect.Height; break;
			case "n": rx = rect.X; ry = rect.Y + dy; width = rect.Width; height = rect.Height - dy; break;
			case "nw": rx = rect.X + dx; ry = rect.Y + dy; width = rect.Width - dx; height = rect.Height - dy; break;
			case "sw": rx = rect.X + dx; ry = rect.Y; width = rect.Width - dx; height = rect.Height + dy; break;
			case "ne": rx = rect.X; ry = rect.Y + dy; width = rect.Width + dx; height = rect.Height - dy; break;
			default: return (rect.X + dx, rect.Y + dy, rect.Width, rect.Height);
		}
		if (width < 1) width = 1;
		if (height < 1) height = 1;
		return (rx, ry, width, height);
	}

	#endregion

	#region Text editing

	void OnSurfaceElementDoubleClicked(object? sender, Vector2 point)
	{
		var name = ResolveNameAt(point);
		if (name == null || !nodesByName.TryGetValue(name, out var node))
		{
			ElementDoubleClicked?.Invoke(this, null);
			return;
		}
		ElementDoubleClicked?.Invoke(this, new ElementDoubleClickInfo {
			Name = name,
			X = node.X,
			Y = node.Y,
			Width = node.Width,
			Height = node.Height
		});
	}

	public void BeginTextEdit(double x, double y, double width, double height, string text)
		=> dispatcher.Invoke(() => Surface.BeginTextEdit(x, y, width, height, text));

	void OnSurfaceTextEditCommitted(object? sender, string text) => TextEditCommitted?.Invoke(this, text);

	#endregion

	#region Overlays

	/// <summary>Whether the design-space gridlines overlay is shown.</summary>
	public bool Gridlines => dispatcher.Invoke(() => Surface.Gridlines);

	public void SetGridlines(bool show) => dispatcher.BeginInvoke(() => Surface.SetGridlines(show));

	/// <summary>Whether free-form move drags snap to the design raster. This is deliberately
	/// independent of <see cref="Gridlines"/>: showing a visual aid must not alter placement until
	/// the owning designer explicitly opts in.</summary>
	public bool SnapToGrid
	{
		get => snapToGrid;
		set => snapToGrid = value;
	}

	/// <summary>Whether free-form drags use sibling alignment guides. It remains on by default so
	/// existing designers retain their current behavior; a backend with an established preference
	/// can explicitly turn it off without also disabling raster snapping.</summary>
	public bool SnapToGuides
	{
		get => snapToGuides;
		set => snapToGuides = value;
	}

	/// <summary>Raster cell size in design units. Invalid/non-positive values disable raster
	/// correction even when <see cref="SnapToGrid"/> is enabled.</summary>
	public double GridCellSize
	{
		get => gridCellSize;
		set => gridCellSize = value;
	}

	/// <summary>Shows the row/column divider guides over a Grid (design rect plus divider offsets).</summary>
	public void SetGridGuides(string name, double x, double y, double width, double height, double[] rowOffsets, double[] colOffsets)
		=> dispatcher.BeginInvoke(() => Surface.SetGridGuides(name, x, y, width, height, rowOffsets, colOffsets));

	public void ClearGridGuides()
		=> dispatcher.BeginInvoke(() => Surface.SetGridGuides(null, 0, 0, 0, 0, Array.Empty<double>(), Array.Empty<double>()));

	/// <summary>Whether the tab-order badge overlay is shown.</summary>
	public bool ShowTabOrder => showTabOrder;

	/// <summary>Toggles the tab-order badges: a numbered badge near every visible element that
	/// reports a TabIndex property.</summary>
	public void SetTabOrderMode(bool show)
	{
		showTabOrder = show;
		RefreshTabOrderBadges();
	}

	void RefreshTabOrderBadges()
	{
		if (!showTabOrder)
		{
			dispatcher.BeginInvoke(() => Surface.SetTabOrderBadges(Array.Empty<(string, double, double, string)>()));
			return;
		}
		var badges = nodesByName
			// IsVisible: a hidden element still reports the X/Y it WOULD sit at, and every tab of a
			// tab control occupies the same rect - badging one stacks it on the visible tab's badge.
			.Where(entry => entry.Value.IsVisible)
			.Select(entry => (Name: entry.Key, entry.Value.X, entry.Value.Y,
				TabIndex: entry.Value.Properties?.FirstOrDefault(p => p.Name == "TabIndex")?.Value))
			.Where(item => !string.IsNullOrEmpty(item.TabIndex))
			.Select(item => (item.Name, item.X, item.Y, item.TabIndex!))
			.ToArray();
		dispatcher.BeginInvoke(() => Surface.SetTabOrderBadges(badges));
	}

	#endregion

	#region Viewport

	public (double Zoom, double PanX, double PanY) GetViewport() => Surface.Viewport;

	public double GetViewportScale() => Surface.ViewportScale;

	public void SetViewport(double zoom, double panX, double panY) => dispatcher.Invoke(() => Surface.SetViewport(zoom, panX, panY));

	public void FitView() => dispatcher.Invoke(() => Surface.FitView());

	public (double X, double Y) DesignToSurfacePoint(double x, double y)
	{
		var point = dispatcher.Invoke(() => Surface.DesignToSurfacePoint(x, y));
		return (point.X, point.Y);
	}

	public (double X, double Y) DesignToScreenPoint(double x, double y)
	{
		var point = dispatcher.Invoke(() => Surface.SurfacePointToScreen(x, y));
		return (point.X, point.Y);
	}

	public DesignerSurfaceGeometry SurfaceGeometry() => Surface.SurfaceGeometry();

	public string DiagnoseScreenAnchors() => dispatcher.Invoke(() => Surface.DiagnoseScreenAnchors());

	#endregion

	public void Dispose()
	{
		if (disposed)
			return;
		disposed = true;
		Surface.SurfacePointerPressed -= OnSurfacePointerPressed;
		Surface.MarqueeSelectionCommitted -= OnMarqueeSelectionCommitted;
		Surface.SurfaceElementDragStarted -= OnSurfaceElementDragStarted;
		Surface.SurfaceElementDragDelta -= OnSurfaceElementDragDelta;
		Surface.SurfaceElementDragCommitted -= OnSurfaceElementDragCommitted;
		Surface.SurfaceLayoutInsetDragCommitted -= OnSurfaceLayoutInsetDragCommitted;
		Surface.SurfaceElementDoubleClicked -= OnSurfaceElementDoubleClicked;
		Surface.TextEditCommitted -= OnSurfaceTextEditCommitted;
		Surface.GridGuideDragCommitted -= OnSurfaceGridGuideDragCommitted;
		Surface.GridTrackSplitRequested -= OnSurfaceGridTrackSplitRequested;
		Surface.ContextCommandRequested -= OnSurfaceContextCommandRequested;
		Surface.NudgeRequested -= OnSurfaceNudgeRequested;
		Surface.UndoRedoRequested -= OnSurfaceUndoRedoRequested;
		nodesByName.Clear();
		LastSnapshot = null;
	}
}
