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
						reader.Skip();
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
							reader.Skip();
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
		}
		
		static ProjectLoadInformation PopulateProject(Solution solution, XmlReader reader)
		{
			string path = reader.GetAttribute("Path");
			if (string.IsNullOrEmpty(path))
				return null;
			
			FileName projectFileName = FileName.Create(Path.Combine(solution.Directory, path));
			string title = projectFileName.GetFileNameWithoutExtension();
			var info = new ProjectLoadInformation(solution, projectFileName, title);
			info.IdGuid = Guid.NewGuid();
			if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
				info.TypeGuid = ProjectTypeGuids.CSharp;
			else if (path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
				info.TypeGuid = ProjectTypeGuids.VB;
			else if (path.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase))
				info.TypeGuid = ProjectTypeGuids.CPlusPlus;
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
