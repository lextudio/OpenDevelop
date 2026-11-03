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
		// The .ui markup is the design unit's root: the designer opens from it, and its behavior
		// class, generated wiring companion and same-stem stylesheet are all derived from or
		// attached to it. This only describes the pad; it never changes the project's
		// DependentUpon metadata.
		string? stem = null;
		if (displayPath.EndsWith(".ui.cs", StringComparison.OrdinalIgnoreCase))
			stem = displayPath[..^".ui.cs".Length];
		else if (displayPath.EndsWith(".ui.vb", StringComparison.OrdinalIgnoreCase))
			stem = displayPath[..^".ui.vb".Length];
		else if (displayPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			|| displayPath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
			|| displayPath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
			stem = displayPath[..displayPath.LastIndexOf('.')];
		if (stem is null)
			return null;
		var markup = stem + ".ui";
		return projectDisplayPaths.Contains(markup) ? markup : null;
	}
}

public sealed class RegisterGtkProjectFileNestingCommand : AbstractCommand, IDisposable
{
	IDisposable? registration;

	public override void Run() => registration ??= ProjectFileNestingProviderRegistry.Register(new GtkProjectFileNestingProvider());

	public void Dispose() => registration?.Dispose();
}
