namespace ICSharpCode.SharpDevelop.Designer.Surface;

/// <summary>
/// A committed design-surface drag: the named element and its start/end rects in design
/// coordinates. The designer turns the rects into source edits (Margin/Width/Height, Location...).
/// </summary>
public sealed class ElementDragInfo
{
	public string Name { get; set; } = "";
	public double StartX { get; set; }
	public double StartY { get; set; }
	public double StartWidth { get; set; }
	public double StartHeight { get; set; }
	public double EndX { get; set; }
	public double EndY { get; set; }
	public double EndWidth { get; set; }
	public double EndHeight { get; set; }
}

/// <summary>A committed drag of one displayed layout inset. The values are in design units;
/// the backend, not the common surface, decides whether that means Margin, Canvas.Left, or an
/// opposite-side anchor such as Canvas.Right.</summary>
public sealed class LayoutInsetEditInfo
{
	public string Name { get; set; } = "";
	public string Kind { get; set; } = "";
	/// <summary>One of <c>Left</c>, <c>Top</c>, <c>Right</c>, or <c>Bottom</c>.</summary>
	public string Edge { get; set; } = "";
	public double Value { get; set; }
}

/// <summary>A click (rather than drag) on a Grid Margin inset label. The WPF backend switches
/// the requested edge between an explicit alignment anchor and Stretch while preserving bounds.</summary>
public sealed class LayoutInsetAnchorToggleInfo
{
	public string Name { get; set; } = "";
	public string Edge { get; set; } = "";
}

/// <summary>
/// A double-click on a design element: its name and design rect. A null value means the
/// double-click hit empty space.
/// </summary>
public sealed class ElementDoubleClickInfo
{
	public string Name { get; set; } = "";
	public double X { get; set; }
	public double Y { get; set; }
	public double Width { get; set; }
	public double Height { get; set; }
}
