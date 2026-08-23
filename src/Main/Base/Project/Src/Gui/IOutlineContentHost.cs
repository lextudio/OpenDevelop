using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Supplies document-specific content for the shared Outline pad.
/// </summary>
[ViewContentService]
public interface IOutlineContentHost
{
	object OutlineContent { get; }
}

/// <summary>Read-only bridge used by add-ins and integration diagnostics to verify that the
/// shared Outline pad is presenting the active document's actual outline control.</summary>
public interface IOutlinePadHost
{
	object HostedContent { get; }
}
