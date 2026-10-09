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
