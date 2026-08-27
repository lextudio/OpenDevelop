// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Scene-asset display binding (doc/technotes/stride-game-studio.md "Asset editor views"): opens a
// Stride .sdscene asset in the fused scene editor, reusing the real EditorGameController path that
// StridePackageView already drives. Double-clicking a scene asset in the Projects pad's .sdpkg
// Assets subtree routes here through the normal workbench open path (the contributor's file nodes
// carry the asset's real on-disk path).
//
// One-session/one-overlay constraint carried from StrideEditorHost: only one Stride package can be
// open at a time (matching real Game Studio's one-project-per-process model), and the scene editor
// owns a single native overlay window. Opening a .sdscene from a different package than the one
// already open reports that explicitly instead of breaking the already-visible editor.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Stride.Assets.Presentation.ViewModel;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.StrideGameStudio
{
	public sealed class StrideSceneAssetDisplayBinding : IDisplayBinding
	{
		// Force StrideEditorHost's bootstrap (assembly/native resolvers, STRIDE_SOURCE_ROOT, preloaded
		// libSDL2) to run BEFORE the JIT resolves any Stride type this binding references. Otherwise
		// the first .sdscene open hits a Stride assembly's module initializer with no resolvers in
		// place and throws TypeInitializationException (measured, first open). NOTE: a bare
		// `typeof(StrideEditorHost)` does NOT run the static cctor - it must be forced via
		// RunClassConstructor, or the first open still fails.
		static StrideSceneAssetDisplayBinding()
			=> System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(StrideEditorHost).TypeHandle);

		public bool IsPreferredBindingForFile(FileName fileName) => CanCreateContentForFile(fileName);

		public bool CanCreateContentForFile(FileName fileName)
			=> string.Equals(Path.GetExtension(fileName), ".sdscene", StringComparison.OrdinalIgnoreCase);

		public double AutoDetectFileContent(FileName fileName, Stream fileContent, string detectedMimeType) => 0;

		public IViewContent CreateContentForFile(OpenedFile file) => new StrideSceneAssetView(file);
	}

	/// <summary>
	/// A Stride scene asset in the fused scene editor. The heavy work (find the owning .sdpkg, open
	/// the session, resolve the SceneViewModel) is off the UI thread; the view shows a status label
	/// until the editor is ready, then swaps in <see cref="StrideSceneEditorViewport"/>.
	/// </summary>
	public sealed class StrideSceneAssetView : AbstractViewContent
	{
		readonly System.Windows.Controls.TextBox text = new()
		{
			Margin = new Thickness(12),
			TextWrapping = TextWrapping.Wrap,
			IsReadOnly = true,
			IsReadOnlyCaretVisible = true,
			BorderThickness = new Thickness(0),
			Background = System.Windows.Media.Brushes.Transparent,
			IsInactiveSelectionHighlightEnabled = true
		};
		readonly System.Windows.Controls.Grid root = new();
		StrideSceneEditorViewport editor;
		FrameworkElement activeContent;

		public StrideSceneAssetView(OpenedFile file)
		{
			Files.Add(file);
			TabPageText = Path.GetFileName(file.FileName) ?? "Stride scene";
			root.Children.Add(text);
			activeContent = text;
			text.Text = $"Stride scene: {PrimaryFileName}\n\nLoading scene editor...";
		}

		public override object Control => root;

		public override void Load(OpenedFile file, Stream stream)
		{
			var dispatcher = Dispatcher.CurrentDispatcher;
			var path = file.FileName.ToString();
			_ = LoadSceneAsync(path, dispatcher);
		}

		async Task LoadSceneAsync(string scenePath, Dispatcher dispatcher)
		{
			try
			{
				var sdpkgPath = FindOwningPackage(scenePath);
				if (sdpkgPath == null)
				{
					dispatcher.Invoke(() => text.Text = $"No owning .sdpkg package found for '{scenePath}'.\n\n" +
						"A Stride asset must live in a package to be edited.");
					return;
				}

				var session = await StrideEditorHost.OpenSessionAsync(sdpkgPath);
				var scene = session.AllAssets.OfType<SceneViewModel>().FirstOrDefault(a =>
					a.AssetItem != null
					&& Path.GetFullPath(a.AssetItem.FullPath).Equals(Path.GetFullPath(scenePath), StringComparison.OrdinalIgnoreCase));

				dispatcher.Invoke(() =>
				{
					if (scene == null)
					{
						text.Text = $"Could not find scene asset '{scenePath}' in session '{sdpkgPath}'.\n\n" +
							"The session may not have loaded this package's scene assets.";
						return;
					}

					SwapToEditor(new StrideSceneEditorViewport(scene));
				});
			}
			catch (Exception ex)
			{
				// Never leave the view blank - surface the failure inline so it is diagnosable.
				var message = $"Stride scene opened, but the editor failed to start:\n{ex}";
				dispatcher.Invoke(() => text.Text = message);
				LoggingService.Error("[StrideGameStudio] scene asset load failed: " + ex);
			}
		}

		/// <summary>Walks up from the asset file (and into sibling directories) for the owning
		/// .sdpkg. Stride asset packages commonly sit in a SUBDIRECTORY while the assets live in a
		/// parent (the .sdpkg's <c>AssetFolders: <c>../Assets</c></c>), so a plain climb-for-*.sdpkg
		/// from the asset misses it - FindOwningPackage checks each level's own .sdpkg AND its
		/// subdirectory packages, then verifies the asset is under a resolved AssetFolder.</summary>
		static string FindOwningPackage(string assetPath)
		{
			var assetFull = Path.GetFullPath(assetPath);
			var dir = new DirectoryInfo(Path.GetDirectoryName(assetFull));
			for (int level = 0; dir != null && level < 6; level++, dir = dir.Parent)
			{
				foreach (var candidate in dir.EnumerateFiles("*.sdpkg", SearchOption.TopDirectoryOnly)
					.Concat(dir.EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
						.SelectMany(d => d.EnumerateFiles("*.sdpkg", SearchOption.TopDirectoryOnly))))
				{
					if (AssetUnderPackage(assetFull, candidate.FullName))
						return candidate.FullName;
				}
			}
			return null;
		}

		/// <summary>True if <paramref name="assetFull"/> is inside any AssetFolder resolved from
		/// <paramref name="sdpkgPath"/> (parse the .sdpkg's AssetFolders, resolve each relative to
		/// the package dir, and test containment).</summary>
		static bool AssetUnderPackage(string assetFull, string sdpkgPath)
		{
			var packageDir = Path.GetDirectoryName(sdpkgPath);
			if (string.IsNullOrEmpty(packageDir))
				return false;
			foreach (var folder in ParseAssetFolders(sdpkgPath))
			{
				// Resolve ".." and "!dir " tags the same way Stride does for package asset folders.
				var resolved = Path.IsPathRooted(folder)
					? folder
					: Path.GetFullPath(Path.Combine(packageDir, folder));
				if (assetFull.StartsWith(Path.GetFullPath(resolved) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		/// <summary>Lightweight .sdpkg AssetFolders parse (mirrors StrideProjectTreeContributor's -
		/// only the block we need, no session load).</summary>
		static string[] ParseAssetFolders(string sdpkgPath)
		{
			var folders = new List<string>();
			var inAssetFolders = false;
			foreach (var rawLine in File.ReadLines(sdpkgPath))
			{
				var line = rawLine.Trim();
				if (line == "AssetFolders:")
				{
					inAssetFolders = true;
					continue;
				}
				if (inAssetFolders)
				{
					if (line.Length > 0 && !char.IsWhiteSpace(rawLine[0]) && !line.StartsWith("-", StringComparison.Ordinal))
						break;
					const string marker = "Path:";
					var idx = line.IndexOf(marker, StringComparison.Ordinal);
					if (idx < 0)
						continue;
					var value = line[(idx + marker.Length)..].Trim();
					if (value.StartsWith("!dir", StringComparison.Ordinal))
						value = value["!dir".Length..].Trim();
					value = value.Trim('\'', '"');
					if (value.Length > 0)
						folders.Add(value);
				}
			}
			return folders.ToArray();
		}

		void SwapToEditor(StrideSceneEditorViewport viewport)
		{
			root.Children.Remove(activeContent);
			(activeContent as IDisposable)?.Dispose();
			editor = viewport;
			activeContent = viewport;
			root.Children.Add(viewport);
		}

		public override void Save(OpenedFile file, Stream stream)
		{
			// Read-only host; the scene editor owns saving through its own path later.
		}
	}
}