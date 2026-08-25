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
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

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
	readonly TextBlock text = new()
	{
		Margin = new System.Windows.Thickness(12),
		TextWrapping = TextWrapping.Wrap
	};
	readonly StrideSdlViewport viewport = new();
	readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
	readonly System.Windows.Controls.Grid root = new();

	public StridePackageView(OpenedFile file)
	{
		Files.Add(file);
		TabPageText = Path.GetFileName(file.FileName) ?? "Stride package";
		root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
		root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
		scroll.Content = text;
		System.Windows.Controls.Grid.SetRow(scroll, 0);
		System.Windows.Controls.Grid.SetRow(viewport, 1);
		root.Children.Add(scroll);
		root.Children.Add(viewport);
		text.Text = $"Stride package: {PrimaryFileName}\n\nLoading package details...";
	}

	public override object Control => root;

	public override void Load(OpenedFile file, Stream stream)
	{
		try
		{
			text.Text = Describe(stream);
		}
		catch (Exception ex)
		{
			// Never leave the view blank - surface the failure inline.
			text.Text = $"Stride package opened, but details failed:\n{ex}";
		}
	}

		public override void Save(OpenedFile file, Stream stream)
		{
			// Read-only skeleton slice; the real editors own saving later.
		}

		string Describe(Stream stream)
		{
			// Lightweight, non-blocking package identification. The full PackageSession.Load
			// (the real editor's entry) is heavy and can block on this host (it walks the
			// solution and can handshake build/preview services), so it is deliberately NOT run
			// inside a view's Load - that wedged the DevFlow action. This parses the .sdpkg YAML
			// front matter directly; session loading joins when the scene editor is hosted off
			// the UI thread with cancellation.
			using var reader = new StreamReader(stream, leaveOpen: true);
			var yaml = reader.ReadToEnd();
			stream.Position = 0;

			var name = Regex.Match(yaml, @"^\s*Name:\s*(.+?)\s*$", RegexOptions.Multiline).Groups[1].Value;
			var version = Regex.Match(yaml, @"^\s*Version:\s*(.+?)\s*$", RegexOptions.Multiline).Groups[1].Value;
			var folders = Regex.Matches(yaml, @"Path:\s*!dir\s+(.+)$", RegexOptions.Multiline)
				.Select(m => m.Groups[1].Value).ToList();

			var sb = new System.Text.StringBuilder();
			sb.AppendLine("Stride package opened by the OpenDevelop fusion addin.");
			sb.AppendLine($"  File: {PrimaryFileName}");
			sb.AppendLine($"  Name: {name}");
			sb.AppendLine($"  Version: {version}");
			sb.AppendLine($"  Asset folders ({folders.Count}):");
			foreach (var f in folders.Take(20))
				sb.AppendLine($"    - {f}");
			sb.AppendLine();
			sb.AppendLine("Note: full PackageSession.Load is deferred to the scene-editor slice (not safe on the UI thread).");
			sb.AppendLine();
			sb.AppendLine($"Stride asset core present: {typeof(Stride.Core.Assets.Package).Assembly.GetName().Name} "
				+ $"{typeof(Stride.Core.Assets.Package).Assembly.GetName().Version}");

			var summary = sb.ToString();
			ICSharpCode.Core.LoggingService.Info("[StrideGameStudio] package view:\n" + summary);
			return summary;
		}
	}
}
