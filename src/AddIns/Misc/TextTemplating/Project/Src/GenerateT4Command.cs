using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.TextTemplating
{
	public sealed class GenerateT4Command : AbstractMenuCommand
	{
		public override void Run()
		{
			var project = SD.ProjectService.CurrentProject;

			if (project is null)
				return;

			foreach (var file in GetT4Files(project).ToList())
			{
				T4TemplateRunner.RunIfApplicable(file, project);
			}
		}

		static IEnumerable<FileProjectItem> GetT4Files(IProject project)
		{
			foreach (var item in project.Items.CreateSnapshot())
			{
				if (item is FileProjectItem file)
				{
					var path = file.FileName.ToString();
					if (path.EndsWith(".tt", StringComparison.OrdinalIgnoreCase) ||
						path.EndsWith(".t4", StringComparison.OrdinalIgnoreCase))
					{
						yield return file;
					}
				}
			}
		}
	}

	public static class T4TemplateRunner
	{
		public static bool IsT4File(string fileName) =>
			fileName.EndsWith(".tt", StringComparison.OrdinalIgnoreCase)
			|| fileName.EndsWith(".t4", StringComparison.OrdinalIgnoreCase);

		public static void RunIfApplicable(FileProjectItem file, IProject project)
		{
			var customToolName = file.CustomTool;
			if (string.IsNullOrEmpty(customToolName) && IsT4File(file.FileName.ToString()))
			{
				customToolName = "TextTemplatingFileGenerator";
			}

			if (string.IsNullOrEmpty(customToolName))
				return;

			var customTool = CustomToolsService.GetCustomTool(customToolName);
			if (customTool is not null)
				CustomToolsService.RunCustomTool(file, customTool, showMessageBoxOnErrors: false);
		}
	}

	public sealed class TextTemplatingFileGeneratorCustomTool : ICustomTool
	{
		public void GenerateCode(FileProjectItem item, CustomToolContext context) =>
			new TextTemplatingFileGenerator().Generate(item, context.Project);
	}

	public sealed class TextTemplatingFilePreprocessorCustomTool : ICustomTool
	{
		public void GenerateCode(FileProjectItem item, CustomToolContext context) =>
			new TextTemplatingFilePreprocessor().Preprocess(item, context.Project);
	}
}
