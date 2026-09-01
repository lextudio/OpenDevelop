using System;
using System.Composition;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Runtime.CompilerServices;

using ICSharpCode.Core;
using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Modern (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03)
/// replacement for the legacy AddInTree-registered <see cref="ToolsPad"/> (AddInTree pad id
/// "SideBar", historically the FormsDesigner toolbox host): shows whatever
/// <see cref="IToolsHost.ToolsContent"/> the active view content exposes, same behavior as before,
/// just as a <see cref="ToolPaneModel"/>.
/// </summary>
[Export(typeof(ToolsPadViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class ToolsPadViewModel : ToolPaneModel, IToolsPadHost
{
    readonly ContentPresenter contentControl = new ContentPresenter();
    object hostedContent;
    TextBox searchBox;
    readonly ConditionalWeakTable<FrameworkElement, ToolboxWrapper> toolboxWrappers = new();

    sealed class ToolboxWrapper
    {
        public FrameworkElement Control { get; init; }
        public TextBox Search { get; init; }
    }
    bool subscribed;

    public ToolsPadViewModel()
    {
        Title = "Tools";
        ContentId = "ToolsPad";
        IsVisible = true; // Matches the legacy Pad's `defaultPosition = "Left"`.
        IsCloseable = true;
        PreferredDockSide = ICSharpCode.ILSpy.ViewModels.PreferredDockSide.Left;
        LegacyPadClass = typeof(ToolsPad).FullName;
        Content = contentControl;
        SD.Services.AddService(typeof(IToolsPadHost), this);
        // The workbench service is installed before its layout composes ToolPane exports. Calling
        // this here covers the normal, already-visible startup path where Show() is never invoked;
        // the guarded calls below remain for unusual early composition and legacy activation.
        EnsureSubscribed();
    }

    public object HostedContent {
        get {
            EnsureSubscribed();
            return hostedContent;
        }
    }

    public bool HasToolboxSearch => searchBox != null;
    public string ToolboxSearchText => searchBox?.Text ?? "";

    /// <summary>
    /// Subscribes to <c>SD.Workbench.ActiveViewContentChanged</c> on first real use rather than in
    /// the constructor - same early-startup hazard already found and fixed for the other migrated
    /// pads (<see cref="ErrorListViewModel"/> et al). Deferred to every externally-reachable entry
    /// point, not only <see cref="Show"/>, since this pad defaults visible - nothing calls Show()
    /// on an already-visible MEF-composed pane.
    /// </summary>
    internal void EnsureSubscribed()
    {
        if (subscribed || SD.Services.GetService(typeof(IWorkbench)) == null)
            return;
        subscribed = true;

        SD.Workbench.ActiveViewContentChanged += WorkbenchActiveContentChanged;
        WorkbenchActiveContentChanged(null, null);
    }

    public override void Show()
    {
        EnsureSubscribed();
        base.Show();
    }

    void WorkbenchActiveContentChanged(object sender, EventArgs e)
    {
        IToolsHost th = SD.GetActiveViewContentService<IToolsHost>();
        hostedContent = th?.ToolsContent;
        if (hostedContent is FrameworkElement element && element.Tag is IFilterableToolbox filterable)
            contentControl.Content = CreateSearchableToolbox(element, filterable);
        else {
            searchBox = null;
            contentControl.Content = hostedContent
                ?? StringParser.Parse("${res:SharpDevelop.SideBar.NoToolsAvailableForCurrentDocument}");
        }
    }

    FrameworkElement CreateSearchableToolbox(FrameworkElement toolbox, IFilterableToolbox filterable)
    {
        if (toolboxWrappers.TryGetValue(toolbox, out var existing)) {
            searchBox = existing.Search;
            return existing.Control;
        }
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Margin = new Thickness(6) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchBox = new TextBox { Text = filterable.FilterText, ToolTip = "Filter controls", MinHeight = 24 };
        var clear = new Button { Content = "×", ToolTip = "Clear Toolbox filter", Margin = new Thickness(4, 0, 0, 0), MinWidth = 24 };
        var body = new Grid();
        var empty = new TextBlock { Text = "No matching controls", Margin = new Thickness(10), Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        void ApplyFilter() { filterable.Filter(searchBox.Text); empty.Visibility = filterable.VisibleItemCount == 0 ? Visibility.Visible : Visibility.Collapsed; }
        searchBox.TextChanged += (_, _) => ApplyFilter();
        searchBox.KeyDown += (_, e) => {
            if (e.Key == System.Windows.Input.Key.Escape) { searchBox.Clear(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Down && toolbox is Control control) { control.Focus(); e.Handled = true; }
        };
        clear.Click += (_, _) => { searchBox.Clear(); searchBox.Focus(); };
        header.Children.Add(searchBox); Grid.SetColumn(clear, 1); header.Children.Add(clear);
        body.Children.Add(toolbox); body.Children.Add(empty); ApplyFilter();
        grid.Children.Add(header); Grid.SetRow(body, 1); grid.Children.Add(body);
        toolboxWrappers.Add(toolbox, new ToolboxWrapper { Control = grid, Search = searchBox });
        return grid;
    }
}
