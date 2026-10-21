using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using OpenDevelop.Profiler.AddIn.Services;
using OpenDevelop.Profiler.Sessions;

namespace OpenDevelop.Profiler.AddIn.Dialogs;

/// <summary>One line of the sessions list.</summary>
internal sealed record SessionRow(SessionHandle Handle, string Kind, string Name, string Started, string Status, long Size)
{
    public string SizeText => Size <= 0 ? "-" : $"{Size / 1024.0:N0} KB";
}

/// <summary>
/// The captures recorded for this solution: open one, show it in the file manager, or delete it. A single
/// window is reused, so choosing the command again brings it back instead of stacking copies.
/// </summary>
public partial class SessionsWindow : Window
{
    static SessionsWindow? current;

    SessionsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => current = null;
        Reload();
    }

    /// <summary>Shows the window, or activates it if it is already open.</summary>
    public static SessionsWindow ShowWindow()
    {
        if (current is null)
        {
            current = new SessionsWindow { Owner = SD.Workbench.MainWindow };
            current.Show();
        }
        else
        {
            current.Reload();
            current.Activate();
        }
        return current;
    }

    SessionRow? Selected => sessionList.SelectedItem as SessionRow;

    internal void Reload()
    {
        var rows = ProfilerWorkspace.OpenStore().List()
            .Select(handle => new SessionRow(
                handle,
                StringParser.Parse("${res:AddIns.Profiler.Sessions.Kind" + handle.Info.Kind + "}"),
                handle.Info.DisplayName,
                handle.Info.StartedUtc.ToLocalTime().ToString("g"),
                handle.Info.Status.ToString(),
                File.Exists(handle.ArtifactPath) ? new FileInfo(handle.ArtifactPath).Length : 0))
            .ToList();
        sessionList.ItemsSource = rows;
        emptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The rows shown, for the integration-test probes.</summary>
    internal IReadOnlyList<object> Describe() =>
        (sessionList.ItemsSource as IEnumerable<SessionRow> ?? [])
            .Select(row => (object)new { id = row.Handle.Info.Id, kind = row.Kind, name = row.Name, status = row.Status, size = row.Size })
            .ToList();

    /// <summary>Deletes a session by id without asking, for the integration-test probes.</summary>
    internal bool DeleteWithoutConfirmation(string id)
    {
        var row = (sessionList.ItemsSource as IEnumerable<SessionRow> ?? []).FirstOrDefault(r => r.Handle.Info.Id == id);
        if (row is null)
            return false;
        ProfilerWorkspace.OpenStore().Delete(row.Handle);
        Reload();
        return true;
    }

    void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = Selected is not null;
        openButton.IsEnabled = hasSelection && File.Exists(Selected!.Handle.ArtifactPath);
        revealButton.IsEnabled = hasSelection;
        deleteButton.IsEnabled = hasSelection;
    }

    void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    void OpenSelected()
    {
        if (Selected is { } row && File.Exists(row.Handle.ArtifactPath))
            CaptureCoordinator.OpenReport(row.Handle.ArtifactPath);
    }

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        Process.Start(new ProcessStartInfo(row.Handle.Directory) { UseShellExecute = true });
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
            return;
        if (!MessageService.AskQuestion("${res:AddIns.Profiler.Sessions.DeleteConfirm}"))
            return;
        ProfilerWorkspace.OpenStore().Delete(row.Handle);
        Reload();
    }
}
