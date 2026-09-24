using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Saves a <see cref="Solution"/> back to its <c>.slnx</c> file.
	/// </summary>
	/// <remarks>
	/// The in-memory model does not carry everything a <c>.slnx</c> can hold - per-project
	/// BuildType/Platform/Deploy rules, <c>Properties</c>, build dependencies - so the file is not
	/// regenerated from the model. Instead the existing XML is loaded and only its structure is
	/// synchronised with the model: <c>Folder</c>, <c>Project</c> and <c>File</c> elements are added,
	/// moved or removed; every element that survives keeps its attributes and children. The mapping
	/// mirrors <see cref="SlnxSolutionLoader"/>: a folder named "src" is <c>&lt;Folder Name="/src/"&gt;</c>.
	/// </remarks>
	static class SlnxSolutionWriter
	{
		public static void Write(Solution solution)
		{
			string fileName = solution.FileName;
			string directory = solution.Directory;
			XDocument document = File.Exists(fileName)
				? XDocument.Load(fileName)
				: new XDocument(new XElement("Solution"));
			XElement root = document.Root;
			if (root == null || root.Name.LocalName != "Solution")
				throw new InvalidOperationException(fileName + " is not a valid .slnx file.");

			// Existing elements, keyed the way the model identifies them.
			var existingFolders = root.Descendants("Folder")
				.Where(e => !string.IsNullOrEmpty((string)e.Attribute("Name")))
				.GroupBy(e => NormalizeFolderName((string)e.Attribute("Name")), StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
			var existingProjects = ElementsByFullPath(root, "Project", directory);
			var existingFiles = ElementsByFullPath(root, "File", directory);

			// What the model wants: every folder, and every project/file with its parent folder name.
			var desiredFolders = new List<string>();
			var desiredItems = new List<(string ElementName, string FullPath, string ParentFolder)>();
			Collect(solution, null, desiredFolders, desiredItems);

			var folderElements = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
			foreach (string folderName in desiredFolders) {
				if (!existingFolders.TryGetValue(folderName, out XElement element)) {
					element = new XElement("Folder", new XAttribute("Name", folderName));
					AddAfterLast(root, element, "Folder", "Configurations");
				}
				folderElements[folderName] = element;
			}

			var kept = new HashSet<XElement>();
			foreach (var item in desiredItems) {
				var existing = item.ElementName == "Project" ? existingProjects : existingFiles;
				XElement container = item.ParentFolder != null ? folderElements[item.ParentFolder] : root;
				if (existing.TryGetValue(item.FullPath, out XElement element)) {
					if (element.Parent != container) {
						element.Remove();
						container.Add(element);
					}
				} else {
					element = new XElement(item.ElementName, new XAttribute("Path", RelativePath(directory, item.FullPath)));
					container.Add(element);
				}
				kept.Add(element);
			}

			foreach (XElement element in existingProjects.Values.Concat(existingFiles.Values).Where(e => !kept.Contains(e)).ToList())
				element.Remove();
			var keptFolders = new HashSet<XElement>(folderElements.Values);
			foreach (XElement element in existingFolders.Values.Where(e => !keptFolders.Contains(e)).ToList())
				element.Remove();

			var settings = new XmlWriterSettings {
				Indent = true,
				IndentChars = "  ",
				OmitXmlDeclaration = document.Declaration == null,
				Encoding = new UTF8Encoding(false)
			};
			using (var writer = XmlWriter.Create(fileName, settings))
				document.Save(writer);
			File.AppendAllText(fileName, Environment.NewLine);
		}

		static void Collect(ISolutionFolder folder, string folderName,
			List<string> folders, List<(string, string, string)> items)
		{
			foreach (ISolutionItem item in folder.Items) {
				if (item is ISolutionFolder subFolder) {
					string name = NormalizeFolderName((folderName == null ? "" : folderName.Trim('/') + "/") + subFolder.Name);
					folders.Add(name);
					Collect(subFolder, name, folders, items);
				} else if (item is IProject project) {
					items.Add(("Project", Path.GetFullPath(project.FileName), folderName));
				} else if (item is ISolutionFileItem file) {
					items.Add(("File", Path.GetFullPath(file.FileName), folderName));
				}
			}
		}

		static Dictionary<string, XElement> ElementsByFullPath(XElement root, string elementName, string directory)
		{
			var result = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
			foreach (XElement element in root.Descendants(elementName)) {
				string path = (string)element.Attribute("Path");
				if (string.IsNullOrEmpty(path))
					continue;
				string fullPath = Path.GetFullPath(Path.Combine(directory, path.Replace('\\', Path.DirectorySeparatorChar)));
				if (!result.ContainsKey(fullPath))
					result[fullPath] = element;
			}
			return result;
		}

		static string NormalizeFolderName(string name)
		{
			string trimmed = name.Trim('/');
			return trimmed.Length == 0 ? "/" : "/" + trimmed + "/";
		}

		static string RelativePath(string directory, string fullPath)
		{
			return FileUtility.GetRelativePath(directory, fullPath).Replace('\\', '/');
		}

		static void AddAfterLast(XElement root, XElement element, params string[] precedingNames)
		{
			XElement anchor = root.Elements().LastOrDefault(e => precedingNames.Contains(e.Name.LocalName));
			if (anchor != null)
				anchor.AddAfterSelf(element);
			else
				root.AddFirst(element);
		}
	}
}
