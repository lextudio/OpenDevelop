using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.Translations;

/// <summary>The IDE end of <see cref="TranslationProject"/>: the current project's files, its name and folder.</summary>
public static class TranslationsService
{
	/// <summary>The open project whose project file is <paramref name="projectFile"/>, or null (also for an empty path).</summary>
	public static IProject? FindProject(string projectFile)
		=> string.IsNullOrEmpty(projectFile) ? null
			: SD.ProjectService.AllProjects.FirstOrDefault(p => string.Equals(p.FileName.ToString(), projectFile, StringComparison.OrdinalIgnoreCase));

	/// <summary>Updates the .pot and every .po of <paramref name="project"/> (the current project when null);
	/// null when there is no project or it has no folder.</summary>
	public static TranslationProject.UpdateResult? Update(IProject? project = null)
	{
		project ??= SD.ProjectService.CurrentProject;
		var directory = project?.Directory?.ToString();
		if (project == null || string.IsNullOrEmpty(directory)) return null;
		var files = project.Items.OfType<FileProjectItem>().Select(item => item.FileName.ToString()).Where(File.Exists).ToList();
		return TranslationProject.UpdateProject(project.Name, directory, files, TranslatableStringSources.All);
	}
}

/// <summary>Tools &gt; Update Translations: regenerates the current project's .pot and merges its .po files.</summary>
public sealed class UpdateTranslationsCommand : ICSharpCode.Core.AbstractMenuCommand
{
	public override void Run()
	{
		var result = TranslationsService.Update();
		if (result == null) { MessageService.ShowMessage("Select a project to update its translations."); return; }
		MessageService.ShowMessage(result.StringCount == 0
			? "No translatable strings were found in the project."
			: $"{result.StringCount} translatable strings in {Path.GetFileName(result.PotPath)}; {result.UpdatedCatalogs.Count} .po file(s) updated.");
	}
}
