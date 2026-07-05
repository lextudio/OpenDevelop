// Minimal Roslyn workspace bridge for AvalonEdit.AddIn context actions (FindBaseClasses,
// FindDerivedClassesOrOverrides, XmlDocTooltipProvider). This intentionally bypasses
// ICSharpCode.TypeSystem.Abstractions - see doc/technotes/csharp-roslyn.md - and talks to
// Microsoft.CodeAnalysis directly. Rebuilds the AdhocWorkspace greedily on each call; there
// is no incremental diffing against OpenedFile change events yet.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Project;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace ICSharpCode.SharpDevelop.Roslyn
{
	public static class RoslynWorkspaceHelper
	{
		static AdhocWorkspace workspace;
		static readonly Dictionary<IProject, ProjectId> projectIds = new Dictionary<IProject, ProjectId>();

		/// <summary>
		/// Unsaved editor buffer content, keyed by file path. SyncProject() prefers this over
		/// on-disk content so completion/resolve reflect what's actually being typed; cleared once
		/// the buffer matches disk again (e.g. after a save) so stale overrides don't linger forever.
		/// </summary>
		static readonly Dictionary<string, string> liveOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public static Solution GetSolution()
		{
			if (workspace == null)
				workspace = new AdhocWorkspace();

			foreach (IProject project in SD.ProjectService.AllProjects) {
				if (!string.Equals(Path.GetExtension(project.FileName), ".csproj", StringComparison.OrdinalIgnoreCase))
					continue;
				SyncProject(project);
			}

			return workspace.CurrentSolution;
		}

		static void SyncProject(IProject project)
		{
			ProjectId projectId;
			if (!projectIds.TryGetValue(project, out projectId)) {
				projectId = ProjectId.CreateNewId();
				var info = ProjectInfo.Create(
					projectId, VersionStamp.Create(), project.Name, project.Name, LanguageNames.CSharp,
					metadataReferences: GetMetadataReferences(project),
					compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
				workspace.AddProject(info);
				projectIds[project] = projectId;
			}

			var currentProject = workspace.CurrentSolution.GetProject(projectId);
			var existingDocsByPath = currentProject.Documents.ToDictionary(d => d.FilePath, StringComparer.OrdinalIgnoreCase);

			foreach (var item in project.GetItemsOfType(ItemType.Compile)) {
				var fileItem = item as FileProjectItem;
				if (fileItem == null)
					continue;
				string path = fileItem.FileName;
				if (!File.Exists(path))
					continue;

				string text;
				string liveText;
				if (liveOverrides.TryGetValue(path, out liveText)) {
					text = liveText;
				} else {
					try {
						text = File.ReadAllText(path);
					} catch (IOException) {
						continue;
					}
				}

				Microsoft.CodeAnalysis.Document existingDoc;
				if (existingDocsByPath.TryGetValue(path, out existingDoc)) {
					existingDocsByPath.Remove(path);
					if (existingDoc.GetTextAsync().Result.ToString() != text) {
						workspace.TryApplyChanges(existingDoc.WithText(SourceText.From(text)).Project.Solution);
					}
				} else {
					workspace.AddDocument(DocumentInfo.Create(
						DocumentId.CreateNewId(projectId), Path.GetFileName(path),
						filePath: path, loader: TextLoader.From(TextAndVersion.Create(SourceText.From(text), VersionStamp.Create()))));
				}
			}

			// Anything left in existingDocsByPath was removed from the project since the last sync.
			foreach (var stale in existingDocsByPath.Values) {
				workspace.TryApplyChanges(workspace.CurrentSolution.RemoveDocument(stale.Id));
			}
		}

		static MetadataReference[] GetMetadataReferences(IProject project)
		{
			var references = new List<MetadataReference>();
			try {
				foreach (var reference in project.ResolveAssemblyReferences(System.Threading.CancellationToken.None)) {
					string path = reference.FileName;
					if (!string.IsNullOrEmpty(path) && File.Exists(path)) {
						references.Add(MetadataReference.CreateFromFile(path));
					}
				}
			} catch (Exception ex) {
				LoggingService.Warn("RoslynWorkspaceHelper: failed to resolve project references, falling back to runtime assemblies. " + ex.Message);
			}

			if (references.Count == 0) {
				// Fallback: at least resolve BCL types via the host runtime's own assemblies.
				string trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
				if (trustedPlatformAssemblies != null) {
					foreach (string path in trustedPlatformAssemblies.Split(Path.PathSeparator)) {
						if (File.Exists(path)) {
							references.Add(MetadataReference.CreateFromFile(path));
						}
					}
				}
			}

			return references.ToArray();
		}

		/// <summary>
		/// Finds the Roslyn symbol at the current caret position in the given editor, resolving
		/// either a declaration (cursor on a class/method/field name) or a reference (cursor on a usage).
		/// </summary>
		public static ISymbol GetSymbolAtCaret(ITextEditor editor)
		{
			if (editor == null)
				return null;
			return GetSymbolAt(editor, editor.Caret.Location);
		}

		public static ISymbol GetSymbolAt(ITextEditor editor, ICSharpCode.AvalonEdit.Document.TextLocation location)
		{
			if (editor == null || editor.FileName == null)
				return null;

			var document = FindDocument(editor.FileName, editor.Document.Text);
			return document != null ? GetSymbolAt(document, location) : null;
		}

		public static Microsoft.CodeAnalysis.Document FindDocument(string filePath)
		{
			return GetSolution().Projects
				.SelectMany(p => p.Documents)
				.FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Finds the Roslyn document for the given file, first syncing it to match the editor's
		/// live (possibly unsaved) buffer content - GetSolution() otherwise only reflects what's
		/// on disk, which would make completion/resolve stale while actively typing.
		/// </summary>
		public static Microsoft.CodeAnalysis.Document FindDocument(string filePath, string liveText)
		{
			if (liveText != null) {
				string onDisk;
				try {
					onDisk = File.Exists(filePath) ? File.ReadAllText(filePath) : null;
				} catch (IOException) {
					onDisk = null;
				}
				if (onDisk == liveText)
					liveOverrides.Remove(filePath);
				else
					liveOverrides[filePath] = liveText;
			}
			return FindDocument(filePath);
		}

		public static ISymbol GetSymbolAt(Microsoft.CodeAnalysis.Document document, ICSharpCode.AvalonEdit.Document.TextLocation location)
		{
			var text = document.GetTextAsync().Result;
			if (location.Line < 1 || location.Line > text.Lines.Count)
				return null;
			int position = text.Lines[location.Line - 1].Start + Math.Max(0, location.Column - 1);
			if (position > text.Length)
				position = text.Length;

			var semanticModel = document.GetSemanticModelAsync().Result;
			var root = semanticModel.SyntaxTree.GetRoot();
			var token = root.FindToken(position);
			for (var node = token.Parent; node != null; node = node.Parent) {
				var declared = semanticModel.GetDeclaredSymbol(node);
				if (declared != null)
					return declared;
				var symbolInfo = semanticModel.GetSymbolInfo(node);
				if (symbolInfo.Symbol != null)
					return symbolInfo.Symbol;
			}
			return null;
		}
	}
}
