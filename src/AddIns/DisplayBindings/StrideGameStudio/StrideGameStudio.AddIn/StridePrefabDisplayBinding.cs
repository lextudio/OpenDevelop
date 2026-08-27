// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Prefab-asset display binding for .sprefab files: opens a Stride prefab in the fused prefab
// editor, reusing the real EditorGameController path that StridePackageView already drives.
// Double-clicking a prefab asset in the Projects pad's .sdpkg Assets subtree routes here through
// the normal workbench open path (the contributor's file nodes carry the asset's real on-disk path).
//
// One-session/one-overlay constraint carried from StrideEditorHost: only one Stride package can be
// open at a time (matching real Game Studio's one-project-per-process model), and the prefab editor
// owns a single native overlay window. Opening a .sprefab from a different package than the one
// already open reports that explicitly instead of breaking the already-visible editor.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using Stride.Assets.Presentation.AssetEditors.PrefabEditor.ViewModels;
using Stride.Assets.Presentation.ViewModel;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.StrideGameStudio
{
	public sealed class StridePrefabDisplayBinding : IDisplayBinding
	{
		// Force StrideEditorHost's bootstrap (assembly/native resolvers, STRIDE_SOURCE_ROOT, preloaded
		// libSDL2) to run BEFORE the JIT resolves any Stride type this binding references.
		static StridePrefabDisplayBinding()
			=> System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(StrideEditorHost).TypeHandle);

		public bool IsPreferredBindingForFile(FileName fileName) => CanCreateContentForFile(fileName);

		public bool CanCreateContentForFile(FileName fileName)
			=> string.Equals(Path.GetExtension(fileName), ".sprefab", StringComparison.OrdinalIgnoreCase);

		public double AutoDetectFileContent(FileName fileName, Stream fileContent, string detectedMimeType) => 0;

		public IViewContent CreateContentForFile(OpenedFile file) => new StridePrefabAssetView(file);
	}

	/// <summary>
	/// A Stride prefab asset in the fused prefab editor. The heavy work (find the owning .sdpkg,
	/// open the session, resolve the PrefabViewModel) is off the UI thread; the view shows a status
	/// label until the editor is ready, then swaps in <see cref="StrideSceneEditorViewport"/> with
	/// a prefab editor view model factory.
	/// </summary>
	public sealed class StridePrefabAssetView : AbstractViewContent
	{
		readonly TextBox text = new()
		{
			Margin = new Thickness(12),
			TextWrapping = TextWrapping.Wrap,
			IsReadOnly = true,
			IsReadOnlyCaretVisible = true,
			BorderThickness = new Thickness(0),
			Background = System.Windows.Media.Brushes.Transparent,
			IsInactiveSelectionHighlightEnabled = true
		};
		readonly Grid root = new();
		StrideSceneEditorViewport editor;
		FrameworkElement activeContent;

		public StridePrefabAssetView(OpenedFile file)
		{
			Files.Add(file);
			TabPageText = Path.GetFileName(file.FileName) ?? "Stride prefab";
			root.Children.Add(text);
			activeContent = text;
			text.Text = $"Stride prefab: {PrimaryFileName}\n\nLoading prefab editor...";
		}

		public override object Control => root;

		public override void Load(OpenedFile file, Stream stream)
		{
			var dispatcher = Dispatcher.CurrentDispatcher;
			var path = file.FileName.ToString();
			_ = LoadPrefabAsync(path, dispatcher);
		}

		async Task LoadPrefabAsync(string prefabPath, Dispatcher dispatcher)
		{
			try
			{
				var sdpkgPath = FindOwningPackage(prefabPath);
				if (sdpkgPath == null)
				{
					dispatcher.Invoke(() => text.Text = $"No owning .sdpkg package found for '{prefabPath}'.\n\n" +
						"A Stride asset must live in a package to be edited.");
					return;
				}

				var session = await StrideEditorHost.OpenSessionAsync(sdpkgPath);
				var prefab = session.AllAssets.OfType<PrefabViewModel>().FirstOrDefault(a =>
					a.AssetItem != null
					&& Path.GetFullPath(a.AssetItem.FullPath).Equals(Path.GetFullPath(prefabPath), StringComparison.OrdinalIgnoreCase));

				dispatcher.Invoke(() =>
				{
					if (prefab == null)
					{
						text.Text = $"Could not find prefab asset '{prefabPath}' in session '{sdpkgPath}'.\n\n" +
							"The session may not have loaded this package's prefab assets.";
						return;
					}

					// PrefabEditorViewModel is a GameEditorViewModel with an EditorGameController,
					// same as SceneEditorViewModel - the generic StrideSceneEditorViewport accepts a
					// factory that creates any GameEditorViewModel.
					SwapToEditor(new StrideSceneEditorViewport(() => new PrefabEditorViewModel(prefab)));
				});
			}
			catch (Exception ex)
			{
				var message = $"Stride prefab opened, but the editor failed to start:\n{ex}";
				dispatcher.Invoke(() => text.Text = message);
				LoggingService.Error("[StrideGameStudio] prefab asset load failed: " + ex);
			}
		}

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

		static bool AssetUnderPackage(string assetFull, string sdpkgPath)
		{
			var packageDir = Path.GetDirectoryName(sdpkgPath);
			if (string.IsNullOrEmpty(packageDir))
				return false;
			foreach (var folder in ParseAssetFolders(sdpkgPath))
			{
				var resolved = Path.IsPathRooted(folder)
					? folder
					: Path.GetFullPath(Path.Combine(packageDir, folder));
				if (assetFull.StartsWith(Path.GetFullPath(resolved) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		static string[] ParseAssetFolders(string sdpkgPath)
		{
			var folders = new System.Collections.Generic.List<string>();
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
			// Read-only host; the prefab editor owns saving through its own path later.
		}
	}
}
