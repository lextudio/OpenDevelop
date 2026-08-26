// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Fusion skeleton slice for the Stride Game Studio integration (see
// doc/technotes/stride-game-studio.md "Fusion with OpenDevelop"). This first binding proves
// the mechanism end to end: an OpenDevelop addin loading Stride assemblies under LibreWPF and
// opening a .sdpkg package into a workbench tab. The scene-editor hosting layers
// (EditorGameController etc., per the keep/discard table) join through this same binding in
// later slices.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.StrideGameStudio
{
	public sealed class StridePackageDisplayBinding : IDisplayBinding
	{
		public bool IsPreferredBindingForFile(FileName fileName) => CanCreateContentForFile(fileName);

		public bool CanCreateContentForFile(FileName fileName)
			=> string.Equals(Path.GetExtension(fileName), ".sdpkg", StringComparison.OrdinalIgnoreCase);

		public double AutoDetectFileContent(FileName fileName, Stream fileContent, string detectedMimeType) => 0;

		public IViewContent CreateContentForFile(OpenedFile file) => new StridePackageView(file);
	}

	/// <summary>
	/// Fused Stride package/scene view: hosts the LIVE windowed viewport (GPU presents directly
	/// to a native overlay window, no CPU readback) with a small info bar reporting what was
	/// parsed from the .sdpkg.
	/// </summary>
	public sealed class StridePackageView : AbstractViewContent
	{
	// A read-only TextBox rather than a TextBlock: this panel is where session-load failures and
	// their stack traces surface, and a TextBlock's text cannot be selected, so there is no way to
	// get a trace out of the tab and into a bug report. IsReadOnly keeps it non-editable while
	// leaving selection and Ctrl+C working; the transparent chrome keeps it looking like a label.
	readonly System.Windows.Controls.TextBox text = new()
	{
		Margin = new System.Windows.Thickness(12),
		TextWrapping = TextWrapping.Wrap,
		IsReadOnly = true,
		IsReadOnlyCaretVisible = true,
		BorderThickness = new System.Windows.Thickness(0),
		Background = System.Windows.Media.Brushes.Transparent,
		// Keep the selection visible after focus moves to the Copy button.
		IsInactiveSelectionHighlightEnabled = true
	};

	readonly System.Windows.Controls.Button copyButton = new()
	{
		Content = "Copy",
		Margin = new System.Windows.Thickness(0, 8, 12, 0),
		Padding = new System.Windows.Thickness(10, 2, 10, 2),
		HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
		VerticalAlignment = System.Windows.VerticalAlignment.Top,
		ToolTip = "Copy this panel's full text (including any stack trace) to the clipboard"
	};
	StrideSdlViewport markerViewport = new();
	FrameworkElement activeViewport;
	// Capped so a long stack trace scrolls inside the info bar instead of pushing the viewport
	// (row 1) off the tab entirely.
	readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 260 };
	readonly System.Windows.Controls.Grid root = new();

	public StridePackageView(OpenedFile file)
	{
		Files.Add(file);
		TabPageText = Path.GetFileName(file.FileName) ?? "Stride package";
		root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
		root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
		scroll.Content = text;
		System.Windows.Controls.Grid.SetRow(scroll, 0);
		activeViewport = markerViewport;
		System.Windows.Controls.Grid.SetRow(activeViewport, 1);
		root.Children.Add(scroll);
		root.Children.Add(activeViewport);

		// Overlaid on the same row as the text so it needs no extra layout row.
		System.Windows.Controls.Grid.SetRow(copyButton, 0);
		root.Children.Add(copyButton);
		copyButton.Click += (_, _) =>
		{
			try
			{
				System.Windows.Clipboard.SetText(text.Text ?? string.Empty);
			}
			catch (Exception ex)
			{
				// Clipboard access can fail transiently (another process holding it). Never let the
				// copy affordance itself throw out of a click handler and take the workbench down.
				ICSharpCode.Core.LoggingService.Warn("[StrideGameStudio] copying panel text failed: " + ex);
			}
		};
		text.Text = $"Stride package: {PrimaryFileName}\n\nLoading package details...";
	}

	public override object Control => root;

	public override void Load(OpenedFile file, Stream stream)
	{
		// Real PackageSession.Load runs on a background thread inside SessionViewModel.
		// OpenSession (see StrideEditorHost) - kick it off here and hop back to the UI thread
		// only to update the label, so this view's Load never blocks the UI thread (the lesson
		// from the earlier "PackageSession.Load hangs on the UI thread" finding).
		var dispatcher = Dispatcher.CurrentDispatcher;
		var path = file.FileName.ToString();
		_ = LoadSessionAsync(path, dispatcher);
	}

	async Task LoadSessionAsync(string path, Dispatcher dispatcher)
	{
		try
		{
			var session = await StrideEditorHost.OpenSessionAsync(path);
			var summary = Describe(session, path);
			var sceneAsset = FindFirstScene(session);
			var entities = SceneAssetReader.ReadFirstScene(session);
			dispatcher.Invoke(() =>
			{
				text.Text = summary;
				if (sceneAsset != null)
				{
					// Big step (gap 2): try the REAL SceneEditorController/EditorGameController
					// first - real meshes/materials/render pipeline, not marker placeholders.
					// Falls back to the marker viewport if it fails to start for any reason
					// (never leave the view blank/broken - same rule as the text panel above).
					try
					{
						SwapViewport(new StrideSceneEditorViewport(sceneAsset));
						return;
					}
					catch (Exception ex)
					{
						ICSharpCode.Core.LoggingService.Error("[StrideGameStudio] real scene editor failed to start, falling back to markers: " + ex);
					}
				}
				markerViewport.SetEntities(entities);
			});
		}
		catch (Exception ex)
		{
			// Never leave the view blank - surface the failure inline.
			var message = $"Stride package opened, but loading the session failed:\n{ex}";
			dispatcher.Invoke(() => text.Text = message);
			ICSharpCode.Core.LoggingService.Error("[StrideGameStudio] session load failed: " + ex);
		}
	}

		public override void Save(OpenedFile file, Stream stream)
		{
			// Read-only skeleton slice; the real editors own saving later.
		}

		void SwapViewport(FrameworkElement newViewport)
		{
			root.Children.Remove(activeViewport);
			(activeViewport as IDisposable)?.Dispose();
			activeViewport = newViewport;
			System.Windows.Controls.Grid.SetRow(activeViewport, 1);
			root.Children.Add(activeViewport);
		}

		static Stride.Assets.Presentation.ViewModel.SceneViewModel FindFirstScene(Stride.Core.Assets.Editor.ViewModel.SessionViewModel session)
		{
			foreach (var pkg in session.LocalPackages)
				foreach (var asset in pkg.Assets)
					if (asset is Stride.Assets.Presentation.ViewModel.SceneViewModel scene)
						return scene;

			// No SceneViewModel despite the package listing a SceneAsset means the asset got a generic
			// view model - i.e. the Stride assets plugin's view-model type mapping never registered.
			// Report what the scene assets actually came back as, so that is diagnosable from the log.
			foreach (var pkg in session.LocalPackages)
				foreach (var asset in pkg.Assets)
					if (asset.AssetType?.Name == "SceneAsset")
						ICSharpCode.Core.LoggingService.Warn(
							$"[StrideGameStudio] scene asset '{asset.Url}' has view model type {asset.GetType().FullName}, not SceneViewModel");
			return null;
		}

		string Describe(Stride.Core.Assets.Editor.ViewModel.SessionViewModel session, string path)
		{
			var sb = new System.Text.StringBuilder();
			sb.AppendLine("Stride package opened by the OpenDevelop fusion addin (real session, not a YAML sniff).");
			sb.AppendLine($"  File: {path}");
			sb.AppendLine($"  Packages: {session.AllPackages.Count()}, local: {session.LocalPackages.Count()}");
			foreach (var pkg in session.LocalPackages)
			{
				sb.AppendLine($"  Package '{pkg.Package.Meta.Name}' {pkg.Package.Meta.Version}: {pkg.Assets.Count()} assets");
				foreach (var asset in pkg.Assets.Take(20))
					sb.AppendLine($"    - {asset.Url} ({asset.AssetType.Name})");
				if (pkg.Assets.Count() > 20)
					sb.AppendLine($"    ... and {pkg.Assets.Count() - 20} more");
			}
			sb.AppendLine();
			sb.AppendLine("Note: this is real asset-session data (Stride.Core.Assets.Editor.SessionViewModel). " +
				"The viewport below now renders real entity positions from the first scene asset found " +
				"(markers, not meshes/materials - full interactive scene editing via SceneEditorController/" +
				"EditorGameController is deferred, see doc/technotes/stride-game-studio.md).");

			var summary = sb.ToString();
			ICSharpCode.Core.LoggingService.Info("[StrideGameStudio] package view:\n" + summary);
			return summary;
		}
	}
}
