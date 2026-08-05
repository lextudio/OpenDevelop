using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Toolbar items for the Task List pad. Resolve <see cref="TaskListViewModel"/> directly via MEF
/// (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03) rather than
/// through <c>TaskListPad.Instance</c> (a legacy singleton set only when that shim class actually
/// gets constructed, which no longer happens on the common MEF-first path) - the model itself is
/// the `[Shared]` singleton now, and these items are created from inside its own
/// `InitializePadContent`, so it's always already resolvable by then.
/// </summary>
public class SelectScopeComboBox : ComboBox
{
    static readonly string[] viewTypes = {"${res:MainWindow.Windows.TaskList.Solution}", "${res:MainWindow.Windows.TaskList.Project}", "${res:MainWindow.Windows.TaskList.AllOpenedFiles}", "${res:MainWindow.Windows.TaskList.CurrentFile}", "${res:MainWindow.Windows.TaskList.Namespace}", "${res:MainWindow.Windows.TaskList.CurrentClass}"};

    public SelectScopeComboBox()
    {
        // LibreWPF's implicit-style lookup only queries the exact element type and does
        // not walk BaseType like WPF does (see FrameworkElement.FindImplicitStyleResource),
        // so ComboBox subclasses never pick up the implicit ComboBox style from the
        // semantic theme dictionary (Themes/Theme.Light.xaml / Theme.Dark.xaml) and fall
        // back to the Aero2 chrome with its hardcoded light body. Resolve the style by its
        // type key instead and pin it once the element is realized; the DynamicResource
        // token colors inside the style keep following theme switches, so this is one-time.
        Loaded += (_, _) => {
            if (Style == null && Application.Current != null)
                Style = Application.Current.TryFindResource(typeof(ComboBox)) as Style;
        };
        this.ItemsSource = viewTypes.Select(s => StringParser.Parse(s));
        this.SelectedIndex = 0;
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        var model = OpenDevelopMefHost.ExportProvider.GetExportedValue<TaskListViewModel>();
        if (this.SelectedIndex != model.SelectedScopeIndex) {
            model.SelectedScopeIndex = this.SelectedIndex;
        }
    }
}

sealed class TaskListTokensToolbarCheckBox : CheckBox, ICheckableMenuCommand
{
    event EventHandler ICheckableMenuCommand.IsCheckedChanged { add {} remove {} }
    event EventHandler System.Windows.Input.ICommand.CanExecuteChanged { add {} remove {} }
    readonly string token;

    public TaskListTokensToolbarCheckBox(string token)
    {
        this.token = token;
        this.Content = token;
        this.Command = this;
        var model = OpenDevelopMefHost.ExportProvider.GetExportedValue<TaskListViewModel>();
        this.IsChecked = model.DisplayedTokens[token];
        SetResourceReference(FrameworkElement.StyleProperty, ToolBar.CheckBoxStyleKey);
    }

    bool ICheckableMenuCommand.IsChecked(object parameter)
    {
        return OpenDevelopMefHost.ExportProvider.GetExportedValue<TaskListViewModel>().DisplayedTokens[token];
    }

    public bool CanExecute(object parameter)
    {
        return true;
    }

    public void Execute(object parameter)
    {
        var model = OpenDevelopMefHost.ExportProvider.GetExportedValue<TaskListViewModel>();
        model.DisplayedTokens[token] = IsChecked == true;
        if (model.IsInitialized)
            model.UpdateItems();
    }
}
