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
/// <see cref="Application.ResourceManagerRequested"/> event. The framework's OWN resources keep
/// working because they are served by a separate framework-package ResourceManager that this does
/// not touch, and this host declares no XAML of its own, so nothing is lost by handing the app's
/// resources over wholesale.
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

    /// <summary>True once a handler is attached and the app's .pri was located, meaning the app's
    /// XAML-declared controls are expected to construct normally.</summary>
    public static bool Serving { get; private set; }

    /// <summary>Attaches the handler for <paramref name="appBin"/>'s app resources. Must run before
    /// any Resources access; safe to call with no app directory (then nothing is served).</summary>
    public static void Attach(Application application, string? appBin)
    {
        appPriPath = LocateAppPri(appBin);
        if (appPriPath is null)
        {
            return;
        }
        application.ResourceManagerRequested += OnResourceManagerRequested;
        Serving = true;
    }

    static void OnResourceManagerRequested(object sender, ResourceManagerRequestedEventArgs args)
    {
        try
        {
            args.CustomResourceManager =
                new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(appPriPath);
            Console.Error.WriteLine($"design-host: serving app resources from {Path.GetFileName(appPriPath)}.");
        }
        catch (Exception e)
        {
            // Leaving CustomResourceManager null makes the framework fall back to its own default,
            // i.e. the pre-existing behaviour, so a bad .pri costs fidelity and not the render.
            Serving = false;
            Console.Error.WriteLine($"design-host: could not serve {Path.GetFileName(appPriPath)}:"
                + $" {e.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// The app's own .pri among the several an output directory holds. Identified through the
    /// runtimeconfig.json that every .NET app output carries, because the rest
    /// (Microsoft.UI.pri, Microsoft.WindowsAppRuntime.pri, ...) are framework packages that the
    /// framework's own ResourceManager already serves.
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
