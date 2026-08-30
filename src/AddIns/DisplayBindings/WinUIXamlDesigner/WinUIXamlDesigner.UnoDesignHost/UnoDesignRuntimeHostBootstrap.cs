using System.IO;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// Registers the out-of-process Uno host as the design surface runtime. Declining (null)
/// when the child binary is not deployed lets the registry fall through to ProGPU.
/// </summary>
public static class UnoDesignRuntimeHostBootstrap
{
	public static void Register() => WinUIXamlRuntimeHostRegistry.Register(Create);

	public static bool ChildAvailable => UnoDesignClient.LocateChildDll() != null;
	public static string ChildPath => UnoDesignClient.LocateChildDll() ?? "";

	static IWinUIXamlRuntimeHost Create(XamlFrameworkContext framework, string documentFileName) =>
		ChildAvailable ? new UnoDesignRuntimeHost(framework, documentFileName) : null;
}

/// <summary>
/// Registers the Microsoft WinUI 3 child for Windows App SDK projects.  Its protocol and WPF
/// presentation shell are shared with the Uno child; selecting it here is what keeps a native
/// WinUI document from silently being rendered by the Uno compatibility host.
/// </summary>
public static class MicrosoftWinUIDesignRuntimeHostBootstrap
{
	public static void Register() => WinUIXamlRuntimeHostRegistry.Register(Create);

	public static string? ChildPath
	{
		get
		{
			var directory = Path.GetDirectoryName(typeof(UnoDesignRuntimeHost).Assembly.Location);
			if (string.IsNullOrEmpty(directory))
				return null;
			var candidate = Path.Combine(directory, "MicrosoftHost", "WinUIXamlDesigner.MicrosoftHost.dll");
			return File.Exists(candidate) ? candidate : null;
		}
	}

	static IWinUIXamlRuntimeHost? Create(XamlFrameworkContext framework, string documentFileName)
	{
		var child = ChildPath;
		return framework?.Kind == XamlFrameworkKind.WinUI && child != null
			? new UnoDesignRuntimeHost(framework, documentFileName, child, "Microsoft WinUI design host")
			: null;
	}
}
