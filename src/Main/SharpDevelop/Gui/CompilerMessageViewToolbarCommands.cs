using System;
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;

namespace ICSharpCode.SharpDevelop.Gui;

public class ShowOutputFromComboBox : ComboBox
{
    // Resolved via the already-registered IOutputPadHost service, not
    // OpenDevelopMefHost.ExportProvider.GetExportedValue<CompilerMessageViewViewModel>(): this
    // toolbar is built *inside* CompilerMessageViewViewModel's own constructor
    // (ToolBarService.CreateToolBar), so resolving the plain-type MEF contract here - a different
    // contract than the "ToolPane" one DockWorkspace.ToolPanes already constructed this instance
    // under - built a second, distinct instance whose constructor then crashed on its own
    // SD.Services.AddService(typeof(IOutputPad), ...) call ("duplicate key", confirmed live).
    // IOutputPadHost, already registered by the one real instance's constructor before the
    // toolbar is built, exposes everything these commands need.
    static IOutputPadHost Host => SD.Services.GetService(typeof(IOutputPadHost)) as IOutputPadHost;

    public ShowOutputFromComboBox()
    {
        SetItems();
        Host.MessageCategoryAdded += CompilerMessageViewMessageCategoryAdded;
        Host.SelectedCategoryIndexChanged += CompilerMessageViewSelectedCategoryIndexChanged;
        this.SelectedIndex = 0;
    }

    void CompilerMessageViewSelectedCategoryIndexChanged(object sender, EventArgs e)
    {
        if (this.SelectedIndex != Host.SelectedCategoryIndex) {
            this.SelectedIndex = Host.SelectedCategoryIndex;
        }
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (this.SelectedIndex != Host.SelectedCategoryIndex) {
            Host.SelectedCategoryIndex = this.SelectedIndex;
        }
    }

    void CompilerMessageViewMessageCategoryAdded(object sender, EventArgs e)
    {
        SetItems();
    }

    void SetItems()
    {
        this.Items.Clear();
        foreach (MessageViewCategory category in Host.MessageCategories) {
            this.Items.Add(StringParser.Parse(category.DisplayCategory));
        }
        this.SelectedIndex = 0;
    }
}

public class ClearOutputWindow : AbstractCommand
{
    public override void Run()
    {
        MessageViewCategory selectedMessageViewCategory = (SD.Services.GetService(typeof(IOutputPadHost)) as IOutputPadHost)?.SelectedMessageViewCategory;
        if (selectedMessageViewCategory != null) {
            selectedMessageViewCategory.ClearText();
        }
    }
}

public class ToggleMessageViewWordWrap : AbstractCheckableMenuCommand
{
    static IOutputPadHost Host => SD.Services.GetService(typeof(IOutputPadHost)) as IOutputPadHost;

    public override bool IsChecked {
        get {
            return Host.WordWrap;
        }
        set {
            Host.WordWrap = value;
        }
    }
}
