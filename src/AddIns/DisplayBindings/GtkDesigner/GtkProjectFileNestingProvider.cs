using System;
using System.Collections.Generic;
using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.GtkDesigner;

/// <summary>GtkBuilder's source-file layout, kept with the GTK addin rather than the shell.</summary>
sealed class GtkProjectFileNestingProvider : IProjectFileNestingProvider
{
	public bool AppliesToProject(string projectFilePath) => true;

	public bool IsSupportedProjectItem(string projectFilePath, string physicalPath)
	{
		if (string.Equals(Path.GetExtension(physicalPath), ".ui", StringComparison.OrdinalIgnoreCase))
			return true;
		// Only a stylesheet named after a sibling design document belongs to the preview. Any other
		// CSS (a web asset, a themed resource) has nothing to do with GtkBuilder and stays where the
		// project put it.
		return string.Equals(Path.GetExtension(physicalPath), ".css", StringComparison.OrdinalIgnoreCase)
			&& File.Exists(Path.ChangeExtension(physicalPath, ".ui"));
	}

	public string? GetParentDisplayPath(string projectFilePath, string displayPath, IReadOnlySet<string> projectDisplayPaths)
	{
		// The behavior class is the user-owned authority. Markup, its generated wiring companion,
		// and a same-stem stylesheet are one GTK design unit. This only describes the pad; it never
		// changes the project's DependentUpon metadata.
		if (displayPath.EndsWith(".ui.cs", StringComparison.OrdinalIgnoreCase))
			return displayPath[..^".ui.cs".Length] + ".cs";
		if (displayPath.EndsWith(".ui.vb", StringComparison.OrdinalIgnoreCase))
			return displayPath[..^".ui.vb".Length] + ".vb";
		if (displayPath.EndsWith(".ui", StringComparison.OrdinalIgnoreCase)) {
			var stem = displayPath[..^".ui".Length];
			foreach (var companion in new[] { stem + ".cs", stem + ".vb" })
				if (projectDisplayPaths.Contains(companion)) return companion;
			return null;
		}
		if (displayPath.EndsWith(".css", StringComparison.OrdinalIgnoreCase)) {
			var stem = displayPath[..^".css".Length];
			var markup = stem + ".ui";
			if (!projectDisplayPaths.Contains(markup)) return null;
			foreach (var companion in new[] { stem + ".cs", stem + ".vb" })
				if (projectDisplayPaths.Contains(companion)) return companion;
			return markup;
		}
		return null;
	}
}

public sealed class RegisterGtkProjectFileNestingCommand : AbstractCommand, IDisposable
{
	IDisposable? registration;

	public override void Run() => registration ??= ProjectFileNestingProviderRegistry.Register(new GtkProjectFileNestingProvider());

	public void Dispose() => registration?.Dispose();
}
