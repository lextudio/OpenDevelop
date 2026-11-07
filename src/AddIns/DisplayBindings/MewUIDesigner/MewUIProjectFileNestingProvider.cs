using System;
using System.Collections.Generic;
using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.MewUIDesigner;

/// <summary>Owns MewUI's project-tree convention without teaching the shell about MXAML.</summary>
sealed class MewUIProjectFileNestingProvider : IProjectFileNestingProvider
{
	public bool AppliesToProject(string projectFilePath) => true;

	public bool IsSupportedProjectItem(string projectFilePath, string physicalPath)
		=> string.Equals(Path.GetExtension(physicalPath), ".mxaml", StringComparison.OrdinalIgnoreCase);

	public bool IsSupportedProjectItemType(string projectFilePath, string itemType)
		=> string.Equals(itemType, "MewUIPage", StringComparison.OrdinalIgnoreCase);

	public string? GetParentDisplayPath(string projectFilePath, string displayPath, IReadOnlySet<string> projectDisplayPaths)
	{
		// The .mxaml document is the design unit. Its behavior companion follows the same
		// nesting rule as WPF's .xaml.cs, but only when the matching layout is displayed.
		if (!displayPath.EndsWith(".mxaml.cs", StringComparison.OrdinalIgnoreCase)
			&& !displayPath.EndsWith(".mxaml.vb", StringComparison.OrdinalIgnoreCase))
			return null;
		var markup = displayPath[..displayPath.LastIndexOf('.')];
		return projectDisplayPaths.Contains(markup) ? markup : null;
	}
}

/// <summary>Registers MewUI's file support and nesting when the add-in loads.</summary>
public sealed class RegisterMewUIProjectFileNestingCommand : AbstractCommand, IDisposable
{
	IDisposable? registration;

	public override void Run() => registration ??= ProjectFileNestingProviderRegistry.Register(new MewUIProjectFileNestingProvider());

	public void Dispose() => registration?.Dispose();
}
