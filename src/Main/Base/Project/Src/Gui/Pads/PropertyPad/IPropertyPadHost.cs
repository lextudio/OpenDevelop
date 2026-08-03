using XceedPropertyGrid = Xceed.Wpf.Toolkit.PropertyGrid.PropertyGrid;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// The subset of the Properties pad's real behavior <see cref="PropertyContainer"/> (this project,
/// Base) needs from it (doc/technotes/ilspy.md "Docking and layout replacement" item 1/item 4
/// consolidation, 2026-08-03) - split out, same shape as <c>IPaneModelHost</c>, so
/// <c>PropertyContainer</c> and any AddIn (e.g. WpfDesign.AddIn, which only references this Base
/// project) can reach the live Properties pad without a compile-time reference to
/// <c>PropertyPadViewModel</c>, whose real implementation now lives in the App project alongside
/// every other migrated pad's <c>ToolPaneModel</c>. Registered via
/// <c>SD.Services.AddService(typeof(IPropertyPadHost), this)</c> in <c>PropertyPadViewModel</c>'s
/// constructor.
/// </summary>
public interface IPropertyPadHost
{
    XceedPropertyGrid Grid { get; }

    PropertyContainer ActiveContainer { get; }

    void UpdateSelectedObjectIfActive(PropertyContainer container);
}
