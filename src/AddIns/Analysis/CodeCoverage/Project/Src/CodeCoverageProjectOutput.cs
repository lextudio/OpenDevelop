using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.CodeCoverage
{
	static class CodeCoverageProjectOutput
	{
		public static string GetAssembly(IProject project)
		{
			string output = project.OutputAssemblyFullPath;
			if (!string.IsNullOrEmpty(output) && File.Exists(output))
				return output;

			string projectPath = project.FileName?.ToString();
			if (string.IsNullOrEmpty(projectPath))
				throw new FileNotFoundException("Project file was not available for " + project.Name);

			string projectName = Path.GetFileNameWithoutExtension(projectPath);
			string projectDirectory = Path.GetDirectoryName(projectPath);
			if (string.IsNullOrEmpty(projectDirectory))
				throw new DirectoryNotFoundException("Project directory was not available for " + project.Name);

			string binDirectory = Path.Combine(projectDirectory, "bin");
			string assembly = Directory.Exists(binDirectory)
				? Directory.EnumerateFiles(binDirectory, projectName + ".dll", SearchOption.AllDirectories)
					.OrderByDescending(File.GetLastWriteTimeUtc)
					.FirstOrDefault()
				: null;
			if (!string.IsNullOrEmpty(assembly))
				return assembly;

			throw new FileNotFoundException("Could not locate output assembly for " + project.Name, Path.Combine(binDirectory, projectName + ".dll"));
		}

		public static ProcessStartInfo CreateRunStartInfo(IProject project)
		{
			string assembly = GetAssembly(project);
			var startInfo = new ProcessStartInfo {
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(assembly)
			};

			if (IsExecutable(assembly)) {
				startInfo.FileName = assembly;
			} else {
				startInfo.FileName = CodeCoverageDotNetHost.Resolve();
				startInfo.ArgumentList.Add("exec");
				startInfo.ArgumentList.Add(assembly);
			}

			return startInfo;
		}

		static bool IsExecutable(string fileName)
		{
			string extension = Path.GetExtension(fileName);
			if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
				return true;

			return string.IsNullOrEmpty(extension) && !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
		}
	}
}
