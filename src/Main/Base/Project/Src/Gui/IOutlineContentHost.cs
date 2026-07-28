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
