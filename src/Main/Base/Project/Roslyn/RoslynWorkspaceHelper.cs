// Minimal Roslyn workspace bridge for AvalonEdit.AddIn context actions (FindBaseClasses,
// FindDerivedClassesOrOverrides, XmlDocTooltipProvider). This intentionally bypasses
// ICSharpCode.TypeSystem.Abstractions - see doc/technotes/csharp-roslyn.md - and talks to
// Microsoft.CodeAnalysis directly. Projects are only fully rescanned (file list + references)
// when their SD ProjectItem collection actually changed (see dirtyProjects/subscribedProjects
// below) - currently-open files still get their live buffer text diffed in on every call, since
// that's driven by liveOverrides, not by a project-structure change.

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

		/// <summary>Projects whose SD ProjectItem collection changed since the last full sync (or
		/// that have never been synced yet). Everything else skips the file-list/reference rescan.</summary>
		static readonly HashSet<IProject> dirtyProjects = new HashSet<IProject>();
		static readonly HashSet<IProject> subscribedProjects = new HashSet<IProject>();

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

			var csharpProjects = SD.ProjectService.AllProjects
				.Where(p => string.Equals(Path.GetExtension(p.FileName), ".csproj", StringComparison.OrdinalIgnoreCase))
				.ToList();

			// Two passes: every csproj gets a ProjectId reserved first, so that when we wire up
			// P2P references below, the referenced project's ProjectId is always already known -
			// even if that project hasn't been synced yet in this call (e.g. it comes later in
			// AllProjects, or hasn't changed and would otherwise be skipped).
			foreach (var project in csharpProjects)
				EnsureProject(project);
			foreach (var project in csharpProjects)
				SyncProject(project);

			return workspace.CurrentSolution;
		}

		static void EnsureProject(IProject project)
		{
			if (projectIds.ContainsKey(project))
				return;
			var projectId = ProjectId.CreateNewId();
			var info = ProjectInfo.Create(
				projectId, VersionStamp.Create(), project.Name, project.Name, LanguageNames.CSharp,
				compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
			workspace.AddProject(info);
			projectIds[project] = projectId;
			dirtyProjects.Add(project);
		}

		static void SubscribeToItemChanges(IProject project)
		{
			if (!subscribedProjects.Add(project))
				return;
			project.Items.CollectionChanged += (removed, added) => dirtyProjects.Add(project);
		}

		static void SyncProject(IProject project)
		{
			ProjectId projectId = projectIds[project];
			SubscribeToItemChanges(project);

			if (dirtyProjects.Remove(project)) {
				SyncReferences(project, projectId);
				SyncDocumentList(project, projectId);
			} else {
				SyncOpenDocumentText(projectId);
			}
		}

		/// <summary>Full rescan of the project's Compile items: adds new files, removes deleted
		/// ones, and updates content for any whose disk/live-buffer text no longer matches.</summary>
		static void SyncDocumentList(IProject project, ProjectId projectId)
		{
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

		/// <summary>Cheap path for a project whose file list/references haven't changed: only
		/// pushes live (unsaved) editor buffer text into documents that already have an override,
		/// instead of re-reading and re-diffing every Compile item in the project.</summary>
		static void SyncOpenDocumentText(ProjectId projectId)
		{
			if (liveOverrides.Count == 0)
				return;
			var currentProject = workspace.CurrentSolution.GetProject(projectId);
			foreach (var doc in currentProject.Documents) {
				string liveText;
				if (!liveOverrides.TryGetValue(doc.FilePath, out liveText))
					continue;
				if (doc.GetTextAsync().Result.ToString() != liveText) {
					workspace.TryApplyChanges(doc.WithText(SourceText.From(liveText)).Project.Solution);
				}
			}
		}

		/// <summary>
		/// Keeps a project's Roslyn ProjectReferences and MetadataReferences in sync with its
		/// SD ProjectReferenceProjectItem/resolved-assembly-reference items.
		///
		/// P2P references (ItemType.ProjectReference) that point at another project we also have
		/// a live Roslyn Project for are modeled as real Roslyn ProjectReferences (compilation-to-
		/// compilation), not as a DLL MetadataReference of that project's build output. This is what
		/// makes transitively-referenced project outputs visible: if P references Q references R,
		/// Roslyn resolves R's public API through Q's own ProjectReferences automatically, instead of
		/// us having to flatten the whole transitive closure into P's reference list ourselves.
		/// Everything else (NuGet package assemblies, framework assemblies, and P2P references to
		/// non-.csproj/non-loaded projects) still goes through GetMetadataReferences as a file-backed
		/// MetadataReference, same as before.
		/// </summary>
		static void SyncReferences(IProject project, ProjectId projectId)
		{
			var desiredProjectRefs = new HashSet<ProjectId>();
			var referencedProjectOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var item in project.GetItemsOfType(ItemType.ProjectReference)) {
				var projectRefItem = item as ProjectReferenceProjectItem;
				if (projectRefItem == null || !projectRefItem.ReferenceOutputAssembly)
					continue;
				var referencedProject = projectRefItem.ReferencedProject;
				ProjectId referencedProjectId;
				if (referencedProject != null && projectIds.TryGetValue(referencedProject, out referencedProjectId)) {
					desiredProjectRefs.Add(referencedProjectId);
					if (referencedProject.OutputAssemblyFullPath != null)
						referencedProjectOutputs.Add(referencedProject.OutputAssemblyFullPath.ToString());
				}
			}

			var currentProject = workspace.CurrentSolution.GetProject(projectId);
			var currentProjectRefs = new HashSet<ProjectId>(currentProject.ProjectReferences.Select(r => r.ProjectId));
			if (!currentProjectRefs.SetEquals(desiredProjectRefs)) {
				var solution = workspace.CurrentSolution;
				foreach (var stale in currentProjectRefs.Except(desiredProjectRefs))
					solution = solution.RemoveProjectReference(projectId, new ProjectReference(stale));
				foreach (var added in desiredProjectRefs.Except(currentProjectRefs))
					solution = solution.AddProjectReference(projectId, new ProjectReference(added));
				workspace.TryApplyChanges(solution);
			}

			var desiredMetadataRefs = GetMetadataReferences(project, referencedProjectOutputs);
			currentProject = workspace.CurrentSolution.GetProject(projectId);
			var currentMetadataRefPaths = new HashSet<string>(
				currentProject.MetadataReferences.OfType<PortableExecutableReference>().Select(r => r.FilePath),
				StringComparer.OrdinalIgnoreCase);
			var desiredMetadataRefPaths = new HashSet<string>(
				desiredMetadataRefs.OfType<PortableExecutableReference>().Select(r => r.FilePath),
				StringComparer.OrdinalIgnoreCase);
			if (!currentMetadataRefPaths.SetEquals(desiredMetadataRefPaths)) {
				workspace.TryApplyChanges(workspace.CurrentSolution
					.WithProjectMetadataReferences(projectId, desiredMetadataRefs));
			}
		}

		static MetadataReference[] GetMetadataReferences(IProject project, ICollection<string> excludePaths)
		{
			var references = new List<MetadataReference>();
			try {
				foreach (var reference in project.ResolveAssemblyReferences(System.Threading.CancellationToken.None)) {
					string path = reference.FileName;
					if (string.IsNullOrEmpty(path) || !File.Exists(path))
						continue;
					if (excludePaths != null && excludePaths.Contains(path))
						continue; // covered by a real Roslyn ProjectReference instead - see SyncReferences
					references.Add(MetadataReference.CreateFromFile(path));
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
