namespace ICSharpCode.WinUIXamlDesigner.MicrosoftHost;

/// <summary>
/// Copies the designed app's compiled XAML (.xbf) next to this host so the framework can find it.
///
/// This is what makes the app's OWN XAML-declared controls load for real instead of being
/// substituted. A generated <c>InitializeComponent</c> calls
/// <c>LoadComponent(ms-appx:///Controls/Example.xaml)</c>, and the framework first probes for the
/// compiled <c>.xbf</c> beside it (CCoreServices::TryLoadXamlResourceHelper, then
/// XamlNodeStreamCacheManager::GetBinaryResourceForXamlUri, which swaps the extension). Only the
/// compiled form is usable here: it carries <c>x:Bind</c> as references into generated code - which
/// lives in the app assembly this host preloads - whereas the .xaml fallback still contains
/// <c>{x:Bind}</c> markup, and a runtime parser has no idea what that is ("The type 'Bind' was not
/// found").
///
/// An unpackaged app keeps those .xbf files LOOSE in its output directory rather than inside its
/// resources.pri, so serving the .pri alone does not reveal them.
///
/// WHERE they have to be is established by experiment, not by reading: copied into this host's own
/// directory the app's controls construct and render; setting the child's working directory to the
/// app's output instead changes nothing. That contradicts the one base this repo could find in the
/// WinUI sources - CommonResourceProvider uses GetModuleFileName(NULL), which for a `dotnet exec`
/// child is the dotnet host's directory - so the path that actually resolves these is some other
/// one, and this mirror is written to the location measured to work rather than to a mechanism
/// fully traced. If that ever needs revisiting, the measurement to repeat is those two cases.
///
/// Known limitation: this directory is shared by every child process, so designing two WinUI
/// projects at once has them overwrite each other's .xbf files. The stale copies are cleared on
/// each start, which keeps a single project always correct.
/// </summary>
static class CompiledXamlMirror
{
    const string Extension = ".xbf";

    /// <summary>Clears any previous mirror and copies <paramref name="appBin"/>'s compiled XAML in,
    /// preserving relative paths (the probe uses the resource's own path). Best-effort: on failure
    /// the app's controls simply get substituted instead, which is the prior behaviour.</summary>
    public static void Refresh(string? appBin)
    {
        var target = AppContext.BaseDirectory;
        try
        {
            Clear(target);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("design-host: could not clear the previous compiled-XAML mirror: "
                + e.GetBaseException().Message);
        }
        if (string.IsNullOrEmpty(appBin) || !Directory.Exists(appBin))
        {
            return;
        }
        var copied = 0;
        try
        {
            foreach (var source in Directory.EnumerateFiles(appBin, "*" + Extension, SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(appBin, source);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                copied++;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"design-host: compiled-XAML mirror incomplete after {copied} file(s): "
                + e.GetBaseException().Message);
            return;
        }
        if (copied > 0)
        {
            Console.Error.WriteLine($"design-host: mirrored {copied} compiled XAML file(s) from {appBin}.");
        }
    }

    /// <summary>Removes the previous mirror. Everything matching is ours to delete: this host
    /// declares no XAML of its own, so any .xbf here came from an earlier Refresh.</summary>
    static void Clear(string target)
    {
        foreach (var stale in Directory.EnumerateFiles(target, "*" + Extension, SearchOption.AllDirectories))
        {
            File.Delete(stale);
        }
        // Directories the mirror created are left behind only if empty, so prune those too - an
        // app's folder names (Controls/, Samples/, ...) should not accumulate here across projects.
        foreach (var directory in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }
}
