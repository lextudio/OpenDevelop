using System.Linq;
using System.Text.Json;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.MewUIDesigner;

[DevFlowUIThread]
public static class MewUIDesignerDevFlowActions
{
	[DevFlowAction("od.mewui-designer.status", Description = "Inspect the active MewUI C# visual designer")]
	public static string Status()
	{
		var view = Activate();
		var grid = PropertyGrid;
		return view == null ? JsonSerializer.Serialize(new { active = false }) : JsonSerializer.Serialize(new {
			active = true, status = view.Status, windowClassName = view.WindowClassName, elementCount = view.ElementCount,
			selectedName = view.SelectedName, hostProcessId = view.HostProcessId, hostPoolKey = view.HostPoolKey, hostSessionId = view.HostSessionId, hostDocumentId = view.HostDocumentId, activeHostLeases = view.ActiveHostLeases, hostRecoveryCount = view.HostRecoveryCount, canUndo = view.EnableUndo, canRedo = view.EnableRedo,
			toolboxItemCount = view.ToolboxItemCount, toolboxHosted = view.IsToolboxHosted, outlineHosted = view.IsOutlineHosted, outlineItemCount = view.OutlineItemCount,
			propertyPadSelectedType = grid?.SelectedObject?.GetType().FullName, propertyPadPropertyCount = grid?.Properties?.Count ?? 0,
			toolbarItemCount = view.ToolbarItemCount, toolbarItems = view.ToolbarItems, toolbarCapabilities = view.ToolbarCapabilities, zoom = view.Zoom, fitMeasured = view.FitMeasured, gridlines = view.Gridlines,
			isDirty = view.IsDesignerDirty, hostLogTail = view.HostLogTail
		});
	}
	[DevFlowAction("od.mewui-designer.select", Description = "Select a MewUI element by generated field name")]
	public static string Select(string name) { var v = Activate(); var ok = v?.SelectByName(name) == true; return JsonSerializer.Serialize(new { success = ok, selectedName = v?.SelectedName, propertyPadSelectedType = PropertyGrid?.SelectedObject?.GetType().FullName }); }
	[DevFlowAction("od.mewui-designer.toolbox.insert", Description = "Insert a MewUI control into the selected container")]
	public static string Insert(string controlName) { var v = Activate(); return JsonSerializer.Serialize(new { success = v?.Add(controlName) == true, elementCount = v?.ElementCount ?? 0 }); }
	[DevFlowAction("od.mewui-designer.set-property", Description = "Set a source-backed property on the selected MewUI element")]
	public static string SetProperty(string name, string value) { var v = Activate(); return JsonSerializer.Serialize(new { success = v?.SetSelectedProperty(name, value) == true }); }
	[DevFlowAction("od.mewui-designer.delete", Description = "Delete the selected MewUI element")]
	public static string Delete() { var v = Activate(); return JsonSerializer.Serialize(new { success = v?.DeleteSelected() == true, elementCount = v?.ElementCount ?? 0 }); }
	[DevFlowAction("od.mewui-designer.reorder", Description = "Move the selected MewUI child within its generated Children relationship")]
	public static string Reorder(int delta) { var v = Activate(); return JsonSerializer.Serialize(new { success = v?.ReorderSelected(delta) == true, selectedName = v?.SelectedName }); }
	[DevFlowAction("od.mewui-designer.undo", Description = "Undo the last MewUI designer source edit")]
	public static string Undo() { var v = Activate(); v?.Undo(); return Status(); }
	[DevFlowAction("od.mewui-designer.redo", Description = "Redo the last MewUI designer source edit")]
	public static string Redo() { var v = Activate(); v?.Redo(); return Status(); }
	[DevFlowAction("od.mewui-designer.refresh", Description = "Reload the MewUI design from generated source")]
	public static string Refresh() { var v = Activate(); v?.RefreshDesign(); return JsonSerializer.Serialize(new { success = v != null, hostProcessId = v?.HostProcessId ?? 0 }); }
	[DevFlowAction("od.mewui-designer.restart-host", Description = "Restart the isolated MewUI designer host")]
	public static string RestartHost() { var v = Activate(); var oldPid = v?.HostProcessId ?? 0; v?.RestartDesignHost(); return JsonSerializer.Serialize(new { success = v != null, oldHostProcessId = oldPid, hostProcessId = v?.HostProcessId ?? 0 }); }
	[DevFlowAction("od.mewui-designer.terminate-host", Description = "Terminate the shared MewUI host to verify automatic recovery")]
	public static string TerminateHost() { var v = Activate(); var oldPid = v?.HostProcessId ?? 0; v?.TerminateDesignHost(); return JsonSerializer.Serialize(new { success = v != null, oldHostProcessId = oldPid }); }
	[DevFlowAction("od.mewui-designer.show-source", Description = "Switch from the MewUI designer to its user-owned source document")]
	public static string ShowSource() { var v = Activate(); v?.ShowSource(); return JsonSerializer.Serialize(new { success = v != null }); }
	[DevFlowAction("od.mewui-designer.zoom", Description = "Set the common designer toolbar zoom")]
	public static string Zoom(double value) { var v = Activate(); if (v != null) v.Zoom = value; return JsonSerializer.Serialize(new { success = v != null, zoom = v?.Zoom ?? 0 }); }
	[DevFlowAction("od.mewui-designer.fit", Description = "Fit the MewUI design using the common canvas toolbar behavior")]
	public static string Fit() { var v = Activate(); v?.FitDesign(); return JsonSerializer.Serialize(new { success = v != null, zoom = v?.Zoom ?? 0, measured = v?.FitMeasured ?? false }); }
	[DevFlowAction("od.mewui-designer.gridlines", Description = "Toggle MewUI design-space gridlines")]
	public static string Gridlines(bool enabled) { var v = Activate(); v?.ShowGridlines(enabled); return JsonSerializer.Serialize(new { success = v != null, gridlines = v?.Gridlines ?? false }); }

	static MewUIDesignerViewContent Activate()
	{
		if (SD.Workbench.ActiveViewContent is MewUIDesignerViewContent active) return active;
		var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow;
		if (window == null) return null;
		for (var i = 0; i < window.ViewContents.Count; i++) if (window.ViewContents[i] is MewUIDesignerViewContent view) { window.SwitchView(i); return view; }
		return null;
	}
	static PropertyGrid? PropertyGrid => (SD.Services.GetService(typeof(IPropertyPadHost)) as IPropertyPadHost)?.Grid;
}
