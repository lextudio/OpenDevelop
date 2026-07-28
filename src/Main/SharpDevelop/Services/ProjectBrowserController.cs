using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Templates;

namespace ICSharpCode.SharpDevelop.Services;

internal static class FileDialogService
{
    public static Task<string[]> PickFilesAsync(string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, Multiselect = true };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>());
    }

    public static Task<string?> PickFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FolderName : null);
    }
}

/// <summary>The WPF concrete Project Browser controller - see ProjectBrowserControllerBase for the
/// shared command surface. Only the native dialog/clipboard touchpoints live here.</summary>
internal sealed class ProjectBrowserController : ProjectBrowserControllerBase
{
    public ProjectBrowserController() : base(new SharpDevelopProjectBrowserService())
    {
    }

    protected override async Task<NewItemDialogOutcome?> ShowNewItemDialogAsync(TemplateDiscoveryService service, string targetDirectory)
    {
        var owner = System.Windows.Application.Current.MainWindow;
        var dialog = await NewItemWindow.ShowAsync(service, targetDirectory, owner);
        if (dialog is null || dialog.SelectedTemplate is null)
            return null;

        return new NewItemDialogOutcome(dialog.SelectedTemplate, dialog.ItemName,
            new Dictionary<string, string>(dialog.AdditionalParameters, StringComparer.OrdinalIgnoreCase));
    }

    protected override async Task<NewProjectDialogOutcome?> ShowNewProjectDialogAsync(TemplateDiscoveryService service, string defaultLocation)
    {
        var owner = System.Windows.Application.Current.MainWindow;
        var dialog = await NewProjectWindow.ShowAsync(service, defaultLocation, owner);
        if (dialog is null || dialog.SelectedTemplate is null)
            return null;

        return new NewProjectDialogOutcome(dialog.SelectedTemplate, dialog.ProjectName, dialog.Location,
            new Dictionary<string, string>(dialog.AdditionalParameters, StringComparer.OrdinalIgnoreCase));
    }

    protected override void CopyTextToClipboard(string text)
    {
        System.Windows.Clipboard.SetText(text);
    }
}
