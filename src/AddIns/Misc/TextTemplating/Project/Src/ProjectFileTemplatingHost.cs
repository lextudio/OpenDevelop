using System;
using System.Collections.Generic;
using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.TextTemplating
{
	class ProjectFileTemplatingHost : UnoTextTemplatingHost
	{
		readonly FileProjectItem file;
		readonly IProject project;

		public ProjectFileTemplatingHost(FileProjectItem file, IProject project)
		{
			this.file = file;
			this.project = project;
		}

		public string GetFileNamespace(string defaultOutputName)
		{
			var customToolNamespace = file.GetEvaluatedMetadata("CustomToolNamespace");
			if (!string.IsNullOrEmpty(customToolNamespace))
				return customToolNamespace;

			var rootNamespace = project.RootNamespace;
			if (string.IsNullOrEmpty(rootNamespace))
				return null;

			var relative = FileUtility.GetRelativePath(
				project.Directory.ToString(),
				file.FileName.ToString());
			var dir = Path.GetDirectoryName(relative);
			if (string.IsNullOrEmpty(dir))
				return rootNamespace;

			return rootNamespace + "." + dir.Replace(Path.DirectorySeparatorChar, '.');
		}

		protected override string SubstitutePlaceholders(string s)
		{
			return SubstituteProjectPlaceholders(base.SubstitutePlaceholders(s));
		}

		protected override string ResolveAssemblyReference(string assemblyReference)
		{
			assemblyReference = SubstituteProjectPlaceholders(assemblyReference);
			return base.ResolveAssemblyReference(assemblyReference);
		}

		string SubstituteProjectPlaceholders(string input)
		{
			if (input is null)
				return null;

			var projectDir = project.Directory.ToString();
			var projectFile = project.FileName.ToString();
			var projectName = project.Name;
			var solutionDir = project.ParentSolution?.Directory.ToString() ?? projectDir;
			var solutionFile = project.ParentSolution?.FileName.ToString() ?? projectFile;
			var solutionName = project.ParentSolution?.Name ?? projectName;

			var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["ProjectDir"] = projectDir,
				["ProjectFileName"] = projectFile,
				["ProjectName"] = projectName,
				["TargetDir"] = projectDir,
				["TargetPath"] = Path.Combine(projectDir, projectName + ".dll"),
				["SolutionDir"] = solutionDir,
				["SolutionFileName"] = solutionFile,
				["SolutionName"] = solutionName,
				["SolutionExt"] = ".sln",
			};

			var result = input;
			foreach (var kvp in tags)
			{
				if (string.IsNullOrEmpty(kvp.Value))
					continue;
				result = result.Replace("$(" + kvp.Key + ")", kvp.Value, StringComparison.OrdinalIgnoreCase);
			}

			return result;
		}
	}
}
