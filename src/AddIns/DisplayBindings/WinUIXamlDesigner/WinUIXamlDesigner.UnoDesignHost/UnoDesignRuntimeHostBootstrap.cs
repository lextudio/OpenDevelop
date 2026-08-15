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
