// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// One-time, process-wide bootstrap of Stride's real editor stack (doc/technotes/
// stride-game-studio.md "Real-content integration plan", gap 1: real .sdpkg content loading,
// reusing SessionViewModel/EditorViewModel/AssetsPlugin/PluginService/StrideEditorPlugin as-is
// rather than re-implementing asset loading). SessionViewModel/EditorViewModel enforce a
// singleton `Instance` (matching real GameStudio's one-session-per-process model), so this host
// itself is a singleton and only ever opens one session for the process's lifetime.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.Settings;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.MostRecentlyUsedFiles;
using Stride.Core.Presentation.View;
using Stride.Core.Presentation.ViewModels;
using Stride.Assets.Presentation;
using Stride.GameStudio.Plugin;
using Stride.GameStudio.Services;

namespace ICSharpCode.StrideGameStudio
{
	static class StrideEditorHost
	{
		static OpenDevelopEditorViewModel editor;
		static string openSessionPath;
		static readonly object gate = new();

		// These two fixes MUST land before anything touches Stride.Assets/Stride.Assets.Presentation
		// (their module initializers cascade into loading Stride.Video.dll and initializing SDL), so
		// they're registered in a static constructor rather than at the top of Initialize() - a
		// static cctor is guaranteed to run before any static member of this class is used.
		//
		// - Managed: Stride's per-graphics-API DLL layout (StrideMultiGraphicsApiHost=true) puts
		//   API-specific assemblies like Stride.Video.dll in a "Vulkan/" subfolder next to this
		//   addin's own assembly, which default probing (AppContext.BaseDirectory only) never
		//   checks. Search the addin folder and its known API subfolders by simple name.
		// - Native: Silk.NET.SDL resolves its native library through its own loader rather than a
		//   fixed DllImport string, so it can't be redirected by placing a dylib on a fixed path via
		//   DYLD_LIBRARY_PATH - and that variable is stripped by macOS SIP from spawned processes
		//   anyway (confirmed empirically: absent from `ps eww` output for this process even when
		//   set on the exact launch command). Intercept via SetDllImportResolver instead.
		static StrideEditorHost()
		{
			var addinDir = Path.GetDirectoryName(typeof(StrideEditorHost).Assembly.Location);

			// StrideDefaultTemplates.Load (in the fork) needs the source-tree location of
			// Stride.Assets.Presentation/Stride.SpriteStudio.Offline's .sdpkg files: this addin
			// consumes Stride via raw file references rather than a NuGet-restored install, so
			// PackageStore.GetPackageFileName always misses. $(StrideCheckoutRoot) is baked in at
			// build time as assembly metadata (see the csproj) since it can be overridden per build.
			var checkoutRoot = typeof(StrideEditorHost).Assembly
				.GetCustomAttributes<AssemblyMetadataAttribute>()
				.FirstOrDefault(a => a.Key == "StrideCheckoutRoot")?.Value;
			if (!string.IsNullOrEmpty(checkoutRoot))
				Environment.SetEnvironmentVariable("STRIDE_SOURCE_ROOT", checkoutRoot);

			// Route Stride's own diagnostic logging (GlobalLogger - process-wide, independent of
			// any particular LoggerResult instance) into OpenDevelop's log, so failures that Stride
			// only reports through a swallowed LoggerResult (e.g. EditorViewModel.OpenSession
			// returning a bare `false` on failure) are still visible instead of a generic
			// "Failed to open Stride session" with no detail.
			GlobalLogger.GlobalMessageLogged += message =>
			{
				if (message.IsError())
					ICSharpCode.Core.LoggingService.Error("[Stride] " + message);
				else if (message.Type == LogMessageType.Warning)
					ICSharpCode.Core.LoggingService.Warn("[Stride] " + message);
			};

			// Stride does substantial work on threads it owns (asset build, shader compilation). An
			// exception escaping one of those is fatal to the whole host - .NET terminates the
			// process - and the runtime's own stderr message can be lost to the abort before it is
			// flushed, leaving a log that simply stops mid-line with no indication of why. Log it
			// ourselves, synchronously, so the cause is on record even though the process still dies.
			AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			{
				try
				{
					ICSharpCode.Core.LoggingService.Fatal("[Stride] unhandled exception on a background thread (host will terminate): " + e.ExceptionObject);
					Console.Error.WriteLine("[Stride] UNHANDLED: " + e.ExceptionObject);
					Console.Error.Flush();
				}
				catch
				{
					// Nothing useful to do while the process is already going down.
				}
			};

			AssemblyLoadContext.Default.Resolving += (context, name) =>
			{
				foreach (var subdir in new[] { "", "Vulkan", "DirectX" })
				{
					var candidate = Path.Combine(addinDir, subdir, name.Name + ".dll");
					if (!File.Exists(candidate))
						continue;

					// Match on version, not just simple name. This handler sits on the process-wide
					// Default context, so it also sees requests that have nothing to do with Stride -
					// notably the .NET SDK's own in-proc MSBuild/NuGet targets, which ship their own
					// (newer) NuGet.* assemblies alongside the SDK. Answering those with whatever
					// same-named file happens to sit in this addin's folder hands back a version the
					// caller cannot use and turns a resolution that would have succeeded on its own
					// into a hard failure (measured: MSBuild asking for NuGet.LibraryModel 7.6.0.0
					// got Stride's 7.3.1.1, breaking project.assets.json reads). Returning null on a
					// mismatch lets the default machinery find the right one.
					if (name.Version != null && AssemblyName.GetAssemblyName(candidate).Version != name.Version)
						continue;

					return context.LoadFromAssemblyPath(candidate);
				}
				return null;
			};

			NativeLibrary.SetDllImportResolver(typeof(Silk.NET.SDL.Sdl).Assembly, (name, assembly, searchPath) =>
			{
				if (name.Contains("SDL", StringComparison.OrdinalIgnoreCase))
				{
					var candidate = Path.Combine(addinDir, "libSDL2-2.0.dylib");
					if (File.Exists(candidate))
						return NativeLibrary.Load(candidate);
				}
				return IntPtr.Zero;
			});

			// SetDllImportResolver only intercepts implicit P/Invoke marshaling - Silk.NET's own
			// SDL loader (SdlLibraryNameContainer) instead calls NativeLibrary.Load/TryLoad itself
			// with a fixed list of candidate names (e.g. "SDL2", "libSDL2-2.0.so.0"), none of which
			// match our dylib's filename, so the resolver above never even gets a chance to run.
			// Force-loading the dylib by absolute path here means it's already mapped into the
			// process by the time Silk's loader runs its own by-name lookup.
			try
			{
				var sdlPath = Path.Combine(addinDir, "libSDL2-2.0.dylib");
				if (File.Exists(sdlPath))
					NativeLibrary.Load(sdlPath);
			}
			catch (Exception ex)
			{
				ICSharpCode.Core.LoggingService.Warn("[StrideEditorHost] failed to preload libSDL2-2.0.dylib: " + ex);
			}
		}

		/// <summary>
		/// Opens (or reuses, if the same file is already open) the one Stride session this
		/// process hosts. Throws <see cref="NotSupportedException"/> if a DIFFERENT session is
		/// already open - Stride's SessionViewModel is a process-wide singleton, matching real
		/// GameStudio's one-project-at-a-time model.
		/// </summary>
		public static async Task<SessionViewModel> OpenSessionAsync(string sdpkgPath)
		{
			lock (gate)
			{
				if (editor == null)
					Initialize();

				if (editor.Session != null)
				{
					if (string.Equals(openSessionPath, sdpkgPath, StringComparison.OrdinalIgnoreCase))
						return editor.Session;
					throw new NotSupportedException(
						$"A different Stride session is already open in this process ('{openSessionPath}'). " +
						"Only one Stride package can be open at a time (matches real Game Studio's one-project-per-process model).");
				}
			}

			var result = await editor.OpenSession(sdpkgPath);
			if (result != true || editor.Session == null)
				throw new InvalidOperationException($"Failed to open Stride session for '{sdpkgPath}'.");

			// Per-session plugin initialization. In real Game Studio this lives in GameStudioWindow's
			// load handler, not in the session-opening code - and this addin replaces that window, so
			// nothing would otherwise run it. It is what registers the session-scoped services the
			// asset editors need: StrideEditorPlugin.InitializeSession creates and registers
			// GameSettingsProviderService and GameStudioBuilderService, both of which
			// EditorGameController's constructor resolves (without them, opening a scene fails with
			// "No service matches the given type").
			foreach (var plugin in editor.Session.ServiceProvider.Get<IAssetsPluginService>().Plugins)
				plugin.InitializeSession(editor.Session);

			openSessionPath = sdpkgPath;
			return editor.Session;
		}

		static void Initialize()
		{
			// Same call Stride.GameStudio's own Program.cs.Startup makes before touching any
			// session. This addin replaces that Program.cs rather than running it, so nothing else
			// performs Stride's MSBuild setup for us - without it, PackageSession's dependency
			// resolution has no usable MSBuild: the referenced .csproj's "Restore" target comes back
			// as non-existent, no obj/project.assets.json is produced, the package's Stride.*
			// dependencies resolve to nothing, and loading a scene then dies on a missing base asset
			// (e.g. DefaultGraphicsCompositorLevelN, which lives in Stride.Engine's asset package).
			PackageSessionPublicHelper.FindAndSetMSBuildVersion();

			// Reused as-is: same registration sequence as Stride.GameStudio's own Program.cs.
			// StrideEditorPlugin was `internal` in upstream Stride - flipped to `public` in this
			// fork specifically so it can be registered from here (see the technote's "fork
			// patch landed" entry) instead of re-implementing its InitializeSession logic.
			AssetsPlugin.RegisterPlugin(typeof(StrideDefaultAssetsPlugin));
			var strideEditorPlugin = (StrideEditorPlugin)AssetsPlugin.RegisterPlugin(typeof(StrideEditorPlugin));
			strideEditorPlugin.EnableThumbnailService = false;
			// OpenDevelop surfaces no asset preview pane, and the preview service is not merely idle
			// without one: it runs a second Game on a thread it creates itself, and on macOS the
			// engine needs the real process main thread for windowing, so that thread throws in
			// Game.PrepareContext and the unhandled exception takes the whole host process down.
			strideEditorPlugin.EnablePreviewService = false;

			var dispatcher = DispatcherService.Create();
			var dialogService = new AddinDialogService();
			var pluginService = new PluginService();
			var provider = new ViewModelServiceProvider([dispatcher, dialogService, pluginService]);

			var mru = new MostRecentlyUsedFileCollection(InternalSettings.LoadProfileCopy, InternalSettings.MostRecentlyUsedSessions, InternalSettings.WriteFile);
			mru.LoadFromSettings();

			editor = new OpenDevelopEditorViewModel(provider, mru);
		}
	}
}
