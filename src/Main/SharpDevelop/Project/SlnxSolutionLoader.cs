using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using ICSharpCode.Core;
using Microsoft.Build.Exceptions;

namespace ICSharpCode.SharpDevelop.Project
{
	sealed class SlnxSolutionLoader : IDisposable
	{
		readonly FileName fileName;
		readonly XmlReader reader;
		// <BuildType>/<Platform>/<Deploy> elements under a <Project> use wildcarded
		// "Config|Platform" patterns (e.g. "Debug-Unpackaged|*") rather than the fully expanded
		// per-cell grid a classic .sln uses, so they can't be applied until every actual solution
		// configuration/platform name is known - collected below as Configurations/Platform/
		// SolutionConfiguration elements are read, which can appear interleaved with <Project>
		// elements. Recorded here and expanded once the full name sets are known.
		readonly List<(ProjectLoadInformation Project, char Kind, string SolutionPattern, string ProjectValue)> configRules
			= new List<(ProjectLoadInformation, char, string, string)>();
		
		public SlnxSolutionLoader(FileName fileName)
		{
			this.fileName = fileName;
			var settings = new XmlReaderSettings {
				CloseInput = true,
				IgnoreComments = true,
				IgnoreWhitespace = true,
				IgnoreProcessingInstructions = true
			};
			this.reader = XmlReader.Create(fileName, settings);
		}
		
		public void Dispose()
		{
			reader.Dispose();
		}
		
		ProjectLoadException Error(string message, params object[] formatItems)
		{
			if (formatItems.Length > 0)
				message = StringParser.Format(message, formatItems);
			else
				message = StringParser.Parse(message);
			IXmlLineInfo lineInfo = reader as IXmlLineInfo;
			int line = (lineInfo != null && lineInfo.HasLineInfo()) ? lineInfo.LineNumber : 0;
			return new ProjectLoadException("Error reading from " + fileName + " at line " + line + ":" + Environment.NewLine + message);
		}
		
		public void ReadSolution(Solution solution, IProgressMonitor progress)
		{
			reader.MoveToContent();
			if (reader.IsEmptyElement || reader.NodeType != XmlNodeType.Element || reader.Name != "Solution")
				throw Error("The file is not a valid solution file");
			
			var solutionConfigNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var solutionPlatformNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var projectInfos = new List<ProjectLoadInformation>();
			var projectToParentFolder = new Dictionary<ProjectLoadInformation, SolutionFolder>();
			
			int depth = reader.Depth;
			reader.Read();
			
			while (reader.Depth > depth)
			{
				if (reader.NodeType != XmlNodeType.Element)
				{
					reader.Read();
					continue;
				}
				
				switch (reader.Name)
				{
					case "Configurations":
						ReadConfigurations(reader, solutionConfigNames, solutionPlatformNames);
						reader.Skip();
						break;
					case "SolutionConfiguration":
					{
						string name = reader.GetAttribute("Name");
						if (!string.IsNullOrEmpty(name))
							solutionConfigNames.Add(name);
						reader.Skip();
						break;
					}
					case "Platform":
					{
						string name = reader.GetAttribute("Name");
						if (!string.IsNullOrEmpty(name))
							solutionPlatformNames.Add(name);
						reader.Skip();
						break;
					}
					case "Folder":
					{
						ReadFolderContents(solution, reader, null, projectInfos, projectToParentFolder);
						break;
					}
					case "Project":
					{
						var info = PopulateProject(solution, reader);
						if (info != null)
							projectInfos.Add(info);
						break;
					}
					default:
						reader.Skip();
						break;
				}
			}
			
			if (solutionConfigNames.Count == 0)
				solutionConfigNames.Add("Debug");
			if (solutionPlatformNames.Count == 0)
				solutionPlatformNames.Add("Any CPU");

			ApplyConfigurationMappings(solutionConfigNames, solutionPlatformNames);

			foreach (var name in solutionConfigNames)
				solution.ConfigurationNames.Add(name, null);
			foreach (var name in solutionPlatformNames)
				solution.PlatformNames.Add(name, null);
			
			solution.LoadPreferences();
			
			int projectCount = projectInfos.Count;
			int projectsLoaded = 0;
			foreach (var projectInfo in projectInfos)
			{
				projectInfo.ActiveProjectConfiguration = projectInfo.ConfigurationMapping.GetProjectConfiguration(solution.ActiveConfiguration);
				progress.TaskName = "Loading " + projectInfo.ProjectName;
				using (projectInfo.ProgressMonitor = progress.CreateSubTask(1.0 / Math.Max(projectCount, 1)))
				{
					var solutionItem = LoadProjectWithErrorHandling(projectInfo);
					if (solutionItem != null)
					{
						if (projectToParentFolder.TryGetValue(projectInfo, out var parentFolder) && parentFolder != null)
							parentFolder.Items.Add(solutionItem);
						else
							solution.Items.Add(solutionItem);
					}
				}
				projectsLoaded++;
				progress.Progress = (double)projectsLoaded / projectCount;
			}
		}
		
		void ReadConfigurations(XmlReader reader, HashSet<string> configNames, HashSet<string> platformNames)
		{
			int depth = reader.Depth;
			if (!reader.IsEmptyElement)
			{
				reader.Read();
				while (reader.Depth > depth)
				{
					if (reader.NodeType != XmlNodeType.Element)
					{
						reader.Read();
						continue;
					}
					switch (reader.Name)
					{
						case "SolutionConfiguration":
						{
							string name = reader.GetAttribute("Name");
							if (!string.IsNullOrEmpty(name))
								configNames.Add(name);
							reader.Skip();
							break;
						}
						case "Platform":
						{
							string name = reader.GetAttribute("Name");
							if (!string.IsNullOrEmpty(name))
								platformNames.Add(name);
							reader.Skip();
							break;
						}
						case "BuildType":
						{
							string name = reader.GetAttribute("Name");
							if (!string.IsNullOrEmpty(name))
								configNames.Add(name);
							reader.Skip();
							break;
						}
						default:
							reader.Skip();
							break;
					}
				}
			}
		}
		
		// Expands the wildcarded <BuildType>/<Platform>/<Deploy> rules recorded while reading
		// <Project> elements into concrete per-(solution config, solution platform) entries in
		// each project's ConfigurationMapping. Without this, every project silently inherits the
		// solution's exact active configuration and platform - wrong whenever a project (e.g. a
		// netstandard2.0 analyzer/source-generator with no ARM64 platform of its own) is mapped to
		// a different project configuration, or has no matching platform at all and must fall back
		// to its own default (Any CPU) instead of a platform it was never built for.
		void ApplyConfigurationMappings(IEnumerable<string> solutionConfigNames, IEnumerable<string> solutionPlatformNames)
		{
			if (configRules.Count == 0)
				return;
			var configNames = solutionConfigNames.ToList();
			var platformNames = solutionPlatformNames.ToList();
			foreach (var project in configRules.Select(r => r.Project).Distinct())
			{
				var rules = configRules.Where(r => r.Project == project).ToList();
				// A project that declares no <Platform> mapping at all does not follow the solution's
				// platform - it builds as whatever it declares itself, which for anything that never
				// opted into architecture-specific builds (a netstandard2.0 analyzer or source
				// generator, say) is Any CPU. Inheriting the solution platform instead sends its
				// output to bin\<Platform>\<Config>\ while every consumer's ProjectReference
				// resolution still looks in bin\<Config>\, and the build fails with
				// "CS0006: Metadata file ... could not be found" naming a path that was never written.
				bool declaresPlatforms = rules.Any(r => r.Kind == 'P');
				foreach (var config in configNames)
				{
					foreach (var platform in platformNames)
					{
						string mappedConfig = config;
						string mappedPlatform = declaresPlatforms ? platform : "Any CPU";
						int bestBuildTypeScore = -1;
						int bestPlatformScore = -1;
						bool deployEnabled = false;
						foreach (var rule in rules)
						{
							if (!TryMatchConfigurationPattern(rule.SolutionPattern, config, platform, out int score))
								continue;
							switch (rule.Kind)
							{
								case 'B':
									if (score >= bestBuildTypeScore) { bestBuildTypeScore = score; mappedConfig = rule.ProjectValue; }
									break;
								case 'P':
									if (score >= bestPlatformScore) { bestPlatformScore = score; mappedPlatform = rule.ProjectValue; }
									break;
								case 'D':
									deployEnabled = true;
									break;
							}
						}
						var solutionConfig = new ConfigurationAndPlatform(config, platform);
						if (bestBuildTypeScore >= 0 || bestPlatformScore >= 0)
							project.ConfigurationMapping.SetProjectConfiguration(solutionConfig, new ConfigurationAndPlatform(mappedConfig, mappedPlatform));
						if (deployEnabled)
							project.ConfigurationMapping.SetDeployEnabled(solutionConfig, true);
					}
				}
			}
		}

		// pattern is "Config|Platform" with either half allowed to be "*". score rewards exact
		// matches over wildcards so a specific rule (e.g. "Debug-Unpackaged|ARM64") wins over a
		// broader one (e.g. "Debug-Unpackaged|*") regardless of declaration order.
		static bool TryMatchConfigurationPattern(string pattern, string config, string platform, out int score)
		{
			score = 0;
			int bar = pattern.IndexOf('|');
			string patConfig = bar >= 0 ? pattern.Substring(0, bar) : pattern;
			string patPlatform = bar >= 0 ? pattern.Substring(bar + 1) : "*";
			bool configMatch = patConfig == "*" || string.Equals(patConfig, config, StringComparison.OrdinalIgnoreCase);
			bool platformMatch = patPlatform == "*" || string.Equals(patPlatform, platform, StringComparison.OrdinalIgnoreCase);
			if (!configMatch || !platformMatch)
				return false;
			score = (patConfig != "*" ? 1 : 0) + (patPlatform != "*" ? 1 : 0);
			return true;
		}

		void ReadFolderContents(Solution solution, XmlReader reader, SolutionFolder parentFolder,
			List<ProjectLoadInformation> projectInfos, Dictionary<ProjectLoadInformation, SolutionFolder> projectToParentFolder)
		{
			string folderName = reader.GetAttribute("Name");
			if (string.IsNullOrEmpty(folderName))
			{
				reader.Skip();
				return;
			}
			
			var folder = new SolutionFolder(solution, Guid.NewGuid());
			folder.Name = folderName.Trim('/');
			if (string.IsNullOrEmpty(folder.Name))
				folder.Name = "/";
			
			if (parentFolder != null)
				parentFolder.Items.Add(folder);
			else
				solution.Items.Add(folder);
			
			if (!reader.IsEmptyElement)
			{
				int depth = reader.Depth;
				reader.Read();
				while (reader.Depth > depth)
				{
					if (reader.NodeType != XmlNodeType.Element)
					{
						reader.Read();
						continue;
					}
					switch (reader.Name)
					{
						case "Project":
						{
							var info = PopulateProject(solution, reader);
							if (info != null)
							{
								projectInfos.Add(info);
								projectToParentFolder[info] = folder;
							}
							break;
						}
						case "File":
						{
							string filePath = reader.GetAttribute("Path");
							if (!string.IsNullOrEmpty(filePath))
							{
								var fileItem = new SolutionFileItem(solution);
								fileItem.FileName = FileName.Create(Path.Combine(solution.Directory, filePath));
								folder.Items.Add(fileItem);
							}
							reader.Skip();
							break;
						}
						case "Folder":
						{
							ReadFolderContents(solution, reader, folder, projectInfos, projectToParentFolder);
							break;
						}
						default:
							reader.Skip();
							break;
					}
				}
			}
			else
			{
				// Self-closing <Folder ... /> (e.g. an empty ".nuget" placeholder folder):
				// the reader must still be advanced past this element, otherwise the caller's
				// while loop re-reads the same node forever, allocating a new SolutionFolder
				// on every iteration until memory is exhausted.
				reader.Read();
			}
		}
		
		ProjectLoadInformation PopulateProject(Solution solution, XmlReader reader)
		{
			string path = reader.GetAttribute("Path");
			ProjectLoadInformation info = null;
			if (!string.IsNullOrEmpty(path))
			{
				FileName projectFileName = FileName.Create(Path.Combine(solution.Directory, path));
				string title = projectFileName.GetFileNameWithoutExtension();
				info = new ProjectLoadInformation(solution, projectFileName, title);
				info.IdGuid = Guid.NewGuid();
				if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
					info.TypeGuid = ProjectTypeGuids.CSharp;
				else if (path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
					info.TypeGuid = ProjectTypeGuids.VB;
				else if (path.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
					info.TypeGuid = ProjectTypeGuids.CPlusPlus;
			}

			if (!reader.IsEmptyElement)
			{
				int depth = reader.Depth;
				reader.Read();
				while (reader.Depth > depth)
				{
					if (reader.NodeType != XmlNodeType.Element)
					{
						reader.Read();
						continue;
					}
					switch (reader.Name)
					{
						case "BuildType":
						case "Platform":
						{
							string solutionPattern = reader.GetAttribute("Solution");
							string projectValue = reader.GetAttribute("Project");
							if (info != null && !string.IsNullOrEmpty(solutionPattern) && !string.IsNullOrEmpty(projectValue))
								configRules.Add((info, reader.Name[0], solutionPattern, projectValue));
							reader.Skip();
							break;
						}
						case "Deploy":
						{
							string solutionPattern = reader.GetAttribute("Solution");
							if (info != null && !string.IsNullOrEmpty(solutionPattern))
								configRules.Add((info, 'D', solutionPattern, null));
							reader.Skip();
							break;
						}
						default:
							reader.Skip();
							break;
					}
				}
			}
			else
			{
				reader.Read();
			}

			return info;
		}
		
		static IProject LoadProjectWithErrorHandling(ProjectLoadInformation projectInfo)
		{
			Exception exception;
			try {
				return SD.ProjectService.LoadProject(projectInfo);
			} catch (FileNotFoundException) {
				return new MissingProject(projectInfo);
			} catch (ProjectLoadException ex) {
				exception = ex;
			} catch (IOException ex) {
				exception = ex;
			} catch (UnauthorizedAccessException ex) {
				exception = ex;
			}
			LoggingService.Warn("Project load error", exception);
			return new ErrorProject(projectInfo, exception);
		}
	}
}
