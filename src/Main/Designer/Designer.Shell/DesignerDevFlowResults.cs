using System.Text.Json;

namespace ICSharpCode.SharpDevelop.Designer.Shell;

/// <summary>Stable JSON result shapes shared by designer DevFlow adapters.</summary>
public static class DesignerDevFlowResults
{
	public static string Success(object? result = null) => JsonSerializer.Serialize(new { success = true, result });
	public static string Failure(string error) => JsonSerializer.Serialize(new { success = false, error });
	public static string ToolboxFilter(bool available, string? filterText, int itemCount, string? selectedItem = null) =>
		JsonSerializer.Serialize(new { success = available, filterText, itemCount, selectedItem });
	public static string HostRestart(bool available, int oldProcessId, int processId) =>
		JsonSerializer.Serialize(new { success = available, oldHostProcessId = oldProcessId, hostProcessId = processId });
}
