namespace ICSharpCode.MewUIDesigner;

/// <summary>MewUI-specific helpers for the shared context commands (the alignment, Grid-unit, move and wrap vocabulary lives in
/// <c>DesignerStandardCommands</c>).</summary>
public static class MewUIContextCommands
{
	/// <summary>An element the document can address: unnamed elements get "#path" ids and cannot be edited by name.</summary>
	public static bool IsNamedId(string id) => !string.IsNullOrEmpty(id) && id[0] != '#';
}
