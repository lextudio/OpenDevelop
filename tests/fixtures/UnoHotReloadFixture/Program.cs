using System;
using System.IO;
using System.Threading.Tasks;
using Uno.UI.Hosting;
using Uno.UI.RemoteControl.HotReload.MetadataUpdater;

namespace UnoHotReloadFixture;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
		if (Environment.GetEnvironmentVariable("OD_UNO_HOT_RELOAD_STATUS_FILE") is { Length: > 0 } statusFile)
		{
			// Raised only after MetadataUpdater.ApplyUpdate completed in the running process.
			// This gives the macOS fixture a deterministic end-to-end assertion point.
			MetadataUpdaterHelper.MetadataUpdated += (_, _) => File.WriteAllText(statusFile, "applied");
		}

        // The integration harness supplies the absolute source path. This changes a visible
        // XAML property after the app and DevServer have connected, without any IDE-specific
        // protocol or synthetic filesystem notification.
        if (Environment.GetEnvironmentVariable("OD_UNO_HOT_RELOAD_TEST_FILE") is { Length: > 0 } sourceFile)
        {
            _ = Task.Run(async () =>
            {
                // The DevServer creates its MSBuild/Roslyn workspace after the runtime connects.
                // Leave enough time for its recursive source watcher to be active first.
                await Task.Delay(TimeSpan.FromSeconds(20));
                File.WriteAllText(sourceFile, File.ReadAllText(sourceFile).Replace("Before hot reload", "After hot reload"));
            });
        }

        UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseMacOS()
            .Build()
            .Run();
    }
}
