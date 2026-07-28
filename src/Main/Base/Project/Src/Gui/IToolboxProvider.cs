using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Supplies document-specific content for the shared Toolbox pad.
/// </summary>
[ViewContentService]
public interface IToolboxProvider
{
	object ToolboxContent { get; }
}
