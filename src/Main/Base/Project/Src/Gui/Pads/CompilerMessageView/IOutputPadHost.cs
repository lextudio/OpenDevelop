using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// The subset of the Output pad's real behavior that <see cref="MessageViewCategory"/> and
/// external AddIns (PackageManagement) need beyond what the already-registered
/// <c>SD.OutputPad</c> (<c>Workbench.IOutputPad</c>) service exposes - namely direct
/// <see cref="MessageViewCategory"/>-typed access and the toolbar-facing members
/// (doc/technotes/ilspy.md "Docking and layout replacement" item 1/item 4 consolidation,
/// 2026-08-04), same shape as <c>IPropertyPadHost</c>/<c>ISearchResultsHost</c>. Registered via
/// <c>SD.Services.AddService(typeof(IOutputPadHost), this)</c> in
/// <c>CompilerMessageViewViewModel</c>'s constructor.
/// </summary>
public interface IOutputPadHost
{
    void AddCategory(MessageViewCategory category);

    MessageViewCategory GetCategory(string categoryName);

    MessageViewCategory SelectedMessageViewCategory { get; }

    List<MessageViewCategory> MessageCategories { get; }

    /// <summary>
    /// The pad's real WPF content (a <c>ToolPaneModel.Content</c>, typed <c>object</c> here since
    /// this is the Base project and WPF-typed callers - e.g. DevFlow's `od.debug.output` action -
    /// cast it themselves).
    /// </summary>
    object Content { get; }

    int SelectedCategoryIndex { get; set; }

    bool WordWrap { get; set; }

    event EventHandler MessageCategoryAdded;

    event EventHandler SelectedCategoryIndexChanged;
}
