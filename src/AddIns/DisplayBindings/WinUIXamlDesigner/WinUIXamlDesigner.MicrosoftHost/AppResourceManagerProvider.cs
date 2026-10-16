using Microsoft.UI.Xaml;

namespace ICSharpCode.WinUIXamlDesigner.MicrosoftHost;

/// <summary>
/// Serves the DESIGNED app's resources.pri to the XAML framework, so the app's own
/// XAML-declared controls can actually be constructed.
///
/// A control declared in XAML gets a generated <c>InitializeComponent</c> that calls
/// <c>Application.LoadComponent(this, new Uri("ms-appx:///Controls/Example.xaml"))</c>. ms-appx
/// resolves through MRT against the RUNNING process's app resources, and this host's are not the
/// designed app's - so constructing one threw "Cannot locate resource from 'ms-appx:///...'",
/// failing the whole page (WinUI-Gallery wraps every sample in such a control).
///
/// WinUI supports exactly this scenario. ModernResourceProvider asks the application for a
/// replacement app ResourceManager when it initializes ("Give the app a chance to provide its own
/// ResourceManager to handle app resources" - microsoft-ui-xaml,
/// src/dxaml/xcp/components/mrt/ModernResourceProvider.cpp), which surfaces as the
/// <see cref="Application.ResourceManagerRequested"/> event. The replacement manager serves the
/// app's resources wholesale, and WinUI loads ITS OWN XBFs (for example
/// <c>Files/Microsoft.UI.Xaml/Themes/themeresources.xbf</c>) through it too, so it is only safe to
/// hand over a manager that carries those: the .pri of a self-contained app (WinUI-Gallery) was
/// merged with the framework's and does, while the .pri of a framework-dependent app lists only
/// its own files and makes the host fail fast (STATUS_STOWED_EXCEPTION, 0xC000027B). For the
/// latter nothing is served (see <c>ServesFrameworkResources</c>), so such an app's own
/// <c>ms-appx:///</c> XAML controls cannot be constructed - the lesser evil compared with a host
/// that dies before it connects.
///
/// Two details are load-bearing:
///  * The event is raised ONCE, while the resource provider initializes, so the handler has to be
///    attached before anything touches Resources - see the call in Program.cs.
///  * The MRT Core ResourceManager is constructed from a FILE PATH. Windows.Storage cannot open
///    these files in an unpackaged process (StorageFile.GetFileFromPathAsync fails with
///    0x80070002 for a path File.Exists confirms), which is what rules out the otherwise obvious
///    ResourceManager.Current.LoadPriFiles route.
/// </summary>
static class AppResourceManagerProvider
{
    static string? appPriPath;
    static Microsoft.Windows.ApplicationModel.Resources.ResourceManager? appResourceManager;

    /// <summary>True once a handler is attached and the app's .pri was located, meaning the app's
    /// XAML-declared controls are expected to construct normally.</summary>
    public static bool Serving { get; private set; }

    /// <summary>Attaches the handler for <paramref name="appBin"/>'s app resources. Must run before
    /// any Resources access; safe to call with no app directory (then nothing is served).</summary>
    public static void Attach(Application application, string? appBin)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("OD_DESIGNHOST_NO_APP_PRI"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("design-host: app PRI provider disabled by OD_DESIGNHOST_NO_APP_PRI.");
            return;
        }
        appPriPath = LocateAppPri(appBin);
        if (appPriPath is null)
        {
            return;
        }
        try
        {
            // ResourceManagerRequested is raised while WinUI initializes its native resource
            // provider. Constructing an MRT ResourceManager from inside that callback re-enters
            // the provider while its initialization lock is held and can deadlock before
            // OnLaunched. Build it now; the callback below must only hand this instance over.
            appResourceManager = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(appPriPath);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"design-host: could not load {Path.GetFileName(appPriPath)}:"
                + $" {e.GetBaseException().Message}");
            return;
        }
        // The custom manager REPLACES the provider's app ResourceManager, and WinUI loads its own XBFs
        // (Files/Microsoft.UI.Xaml/Themes/themeresources.xbf) through it. A .pri that was merged with the
        // framework's (a self-contained app such as WinUI-Gallery) has them; the .pri of a framework-dependent
        // app lists only its own files (App.xbf, MainPage.xbf), and handing that one over made the host fail
        // fast about 0.4 s after start (STATUS_STOWED_EXCEPTION, 0xC000027B), so the designer only saw the
        // child exit before it connected ("A task was canceled"). Leave the default manager in place then.
        if (!ServesFrameworkResources(appResourceManager))
        {
            Console.Error.WriteLine($"design-host: {Path.GetFileName(appPriPath)} carries no framework resources"
                + " (a framework-dependent app); not serving it as the app resource manager.");
            appResourceManager = null;
            return;
        }
        application.ResourceManagerRequested += OnResourceManagerRequested;
        Serving = true;
    }

    static bool ServesFrameworkResources(Microsoft.Windows.ApplicationModel.Resources.ResourceManager manager)
    {
        try
        {
            return manager.MainResourceMap.TryGetValue("Files/Microsoft.UI.Xaml/Themes/themeresources.xbf") is not null;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"design-host: could not inspect the app .pri: {e.GetBaseException().Message}");
            return false;
        }
    }

    static void OnResourceManagerRequested(object sender, ResourceManagerRequestedEventArgs args)
    {
        // This callback is on the resource provider's initialization path. Do not create or
        // otherwise initialize MRT objects here (see Attach); merely assign the prepared value.
        if (appResourceManager is null) return;
        args.CustomResourceManager = appResourceManager;
        Console.Error.WriteLine($"design-host: serving app resources from {Path.GetFileName(appPriPath)}.");
    }

    /// <summary>
    /// The app's own .pri among the several an output directory holds. Identified through the
    /// runtimeconfig.json that every .NET app output carries, because the rest
    /// (Microsoft.UI.pri, Microsoft.WindowsAppRuntime.pri, ...) are framework packages that the
    /// framework's own ResourceManager already serves.
    ///
    /// A packaged (MSIX) output's merged <c>resources.pri</c> is deliberately NOT a candidate:
    /// serving it hangs the child before the handshake (the designer then reports "A task was
    /// canceled").
    /// </summary>
    static string? LocateAppPri(string? appBin)
    {
        if (string.IsNullOrEmpty(appBin) || !Directory.Exists(appBin))
        {
            return null;
        }
        foreach (var runtimeConfig in Directory.GetFiles(appBin, "*.runtimeconfig.json"))
        {
            var name = Path.GetFileName(runtimeConfig);
            name = name.Substring(0, name.Length - ".runtimeconfig.json".Length);
            var candidate = Path.Combine(appBin, name + ".pri");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        Console.Error.WriteLine($"design-host: no app .pri found in {appBin};"
            + " the app's XAML-declared controls cannot be constructed.");
        return null;
    }
}
