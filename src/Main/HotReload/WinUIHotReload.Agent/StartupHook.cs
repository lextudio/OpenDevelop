using WinUIHotReload;

// DOTNET_STARTUP_HOOKS entry point: the runtime looks for a type literally named "StartupHook" in
// the global namespace with a static Initialize(), and calls it before the application's Main().
internal class StartupHook
{
	internal static void Initialize()
	{
		try {
			WinUIHotReloadAgent.Start();
		} catch {
			// Hot Reload is a convenience; it must never take the application down.
		}
	}
}
