using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.UnitTesting.Mtp
{
	sealed class MtpGoToDefinitionCommand : ICommand
	{
		readonly MtpTestNode node;
		readonly IProject project;

		public MtpGoToDefinitionCommand(MtpTestNode node, IProject project)
		{
			this.node = node;
			this.project = project;
		}

		public event EventHandler CanExecuteChanged { add { } remove { } }

		public bool CanExecute(object parameter)
		{
			return ResolveFile() != null;
		}

		public void Execute(object parameter)
		{
			var resolved = ResolveFile();
			if (resolved == null)
				return;

			SD.FileService.JumpToFilePosition(FileName.Create(resolved.FileName), resolved.Line, 1);
		}

		FilePosition ResolveFile()
		{
			var methodName = GetMethodName();
			if (!string.IsNullOrEmpty(node.LocationFile) && File.Exists(node.LocationFile)) {
				return new FilePosition(node.LocationFile, FindLine(node.LocationFile, methodName, GetTypeName()));
			}

			return FindInProjectFiles(methodName, GetTypeName());
		}

		FilePosition FindInProjectFiles(string methodName, string typeName)
		{
			if (string.IsNullOrEmpty(methodName))
				return null;

			foreach (var fileName in GetProjectSourceFiles()) {
				var line = FindLine(fileName, methodName, typeName);
				if (line > 0)
					return new FilePosition(fileName, line);
			}

			return null;
		}

		IEnumerable<string> GetProjectSourceFiles()
		{
			var files = project.Items
				.OfType<FileProjectItem>()
				.Select(item => item.FileName?.ToString())
				.Where(IsCSharpSourceFile);

			var projectDirectory = project.Directory?.ToString();
			if (!string.IsNullOrEmpty(projectDirectory) && Directory.Exists(projectDirectory)) {
				files = files.Concat(Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
					.Where(fileName => !IsUnderBuildOutputDirectory(projectDirectory, fileName)));
			}

			return files.Distinct(StringComparer.OrdinalIgnoreCase);
		}

		static int FindLine(string fileName, string methodName, string typeName)
		{
			try {
				var lines = File.ReadAllLines(fileName);
				if (!string.IsNullOrEmpty(typeName) && !FileContainsType(lines, typeName))
					return 0;
				for (int i = 0; i < lines.Length; i++) {
					if (lines[i].IndexOf(methodName, StringComparison.Ordinal) >= 0)
						return i + 1;
				}
			} catch {
			}

			return 0;
		}

		static bool FileContainsType(string[] lines, string typeName)
		{
			var shortTypeName = typeName.Split('.').LastOrDefault();
			if (string.IsNullOrEmpty(shortTypeName))
				return true;
			return lines.Any(line => line.IndexOf(shortTypeName, StringComparison.Ordinal) >= 0);
		}

		string GetMethodName()
		{
			if (!string.IsNullOrEmpty(node.LocationMethodName))
				return node.LocationMethodName;

			var displayName = node.DisplayName;
			var dotIndex = displayName.LastIndexOf('.');
			return dotIndex >= 0 ? displayName.Substring(dotIndex + 1) : displayName;
		}

		string GetTypeName()
		{
			if (!string.IsNullOrEmpty(node.LocationType))
				return node.LocationType;

			var displayName = node.DisplayName;
			var dotIndex = displayName.LastIndexOf('.');
			return dotIndex > 0 ? displayName.Substring(0, dotIndex) : null;
		}

		static bool IsCSharpSourceFile(string fileName)
		{
			return !string.IsNullOrEmpty(fileName) && string.Equals(Path.GetExtension(fileName), ".cs", StringComparison.OrdinalIgnoreCase);
		}

		static bool IsUnderBuildOutputDirectory(string projectDirectory, string fileName)
		{
			var relativePath = FileUtility.GetRelativePath(projectDirectory, fileName);
			return relativePath.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				|| relativePath.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}

		sealed class FilePosition
		{
			public FilePosition(string fileName, int line)
			{
				FileName = fileName;
				Line = line > 0 ? line : 1;
			}

			public string FileName { get; }
			public int Line { get; }
		}
	}
}
