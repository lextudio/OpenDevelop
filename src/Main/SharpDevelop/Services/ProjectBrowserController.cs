#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Templates;

namespace ICSharpCode.SharpDevelop.Services;

internal static class FileDialogService
{
    public static Task<string[]> PickFilesAsync(string filter) => Task.FromResult(PickFiles(filter));

    public static Task<string?> PickFolderAsync() => Task.FromResult(PickFolder());

    /// <summary>Native multi-select open-file dialog. Under OD_TEST_MODE the dialog is never shown:
    /// the answer comes from <see cref="TestDialogAnswers"/>, and nothing queued means cancelled.</summary>
    public static string[] PickFiles(string filter, System.Windows.Window? owner = null)
    {
        if (TestMode.IsActive)
        {
            var files = TestDialogAnswers.TryDequeue(TestDialogAnswers.Files, out var queued) ? queued : Array.Empty<string>();
            ICSharpCode.Core.LoggingService.Info("OD_TEST_MODE: suppressed open-file dialog, auto-answered [" + string.Join(", ", files) + "]");
            return files;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, Multiselect = true };
        var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FileNames : Array.Empty<string>();
    }

    public static string? PickFolder()
    {
        if (TestMode.IsActive)
        {
            var folder = TestDialogAnswers.TryDequeue(TestDialogAnswers.Folder, out var queued) && queued.Length > 0 ? queued[0] : null;
            ICSharpCode.Core.LoggingService.Info("OD_TEST_MODE: suppressed folder dialog, auto-answered " + (folder ?? "null (cancel)"));
            return folder;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog();
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
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
            new Dictionary<string, string?>(dialog.AdditionalParameters, StringComparer.OrdinalIgnoreCase));
    }

    protected override async Task<NewProjectDialogOutcome?> ShowNewProjectDialogAsync(TemplateDiscoveryService service, string defaultLocation)
    {
        var owner = System.Windows.Application.Current.MainWindow;
        var dialog = await NewProjectWindow.ShowAsync(service, defaultLocation, owner);
        if (dialog is null || dialog.SelectedTemplate is null)
            return null;

        return new NewProjectDialogOutcome(dialog.SelectedTemplate, dialog.ProjectName, dialog.Location,
            new Dictionary<string, string?>(dialog.AdditionalParameters, StringComparer.OrdinalIgnoreCase));
    }

    protected override Task<AddReferenceDialogOutcome?> ShowAddReferenceDialogAsync(string projectName, IReadOnlyList<ReferenceCandidate> candidates)
    {
        var owner = System.Windows.Application.Current.MainWindow;
        var dialog = AddReferenceWindow.Show(projectName,
            candidates.Select(c => new AddReferenceWindow.ProjectCandidate(c.Name, c.ProjectPath)).ToArray(), owner);
        return Task.FromResult(dialog is null
            ? null
            : new AddReferenceDialogOutcome(dialog.SelectedProjectPaths, dialog.SelectedAssemblyPaths));
    }

    protected override void CopyTextToClipboard(string text)
    {
        System.Windows.Clipboard.SetText(text);
    }
}
