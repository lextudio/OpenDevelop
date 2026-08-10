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
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.LanguageServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
// See CSharpVBLanguageService.cs's alias comment: disambiguates against the COM interop
// "Accessibility" namespace now visible via UseWindowsForms=true.
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;
using CS = Microsoft.CodeAnalysis.CSharp;
using VB = Microsoft.CodeAnalysis.VisualBasic;

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

		public static void InvalidateProject(IProject project)
		{
			if (project != null)
				dirtyProjects.Add(project);
		}

		public static Solution GetSolution()
		{
			if (workspace == null)
				workspace = new AdhocWorkspace();

			// Supported Roslyn-backed project types today: C# and VB.NET - see doc/technotes/roslyn.md
			// and doc/technotes/csharp-vb-binding.md. Driven by which ProjectBinding is actually
			// registered (CSharpBinding.addin/VBBinding.addin), not a hardcoded ".csproj"/".vbproj"
			// check, so disabling either binding addin also stops its projects from getting a Roslyn
			// workspace project. Each project is a single language (unlike a hypothetical
			// mixed-language project), so the language is picked once per project, not per file.
			var languageProjects = SD.ProjectService.AllProjects
				.Where(p => IsSupportedExtension(Path.GetExtension(p.FileName)))
				.ToList();

			// Two passes: every project gets a ProjectId reserved first, so that when we wire up
			// P2P references below, the referenced project's ProjectId is always already known -
			// even if that project hasn't been synced yet in this call (e.g. it comes later in
			// AllProjects, or hasn't changed and would otherwise be skipped).
			foreach (var project in languageProjects)
				EnsureProject(project);
			foreach (var project in languageProjects)
				SyncProject(project);

			return workspace.CurrentSolution;
		}

		/// <summary>
		/// Roslyn only knows how to build CS/VB compilation options (see EnsureProject/
		/// SyncCompilationOptions below) - this is inherent to what this helper implements, not an
		/// ownership decision, so it's fine for it to name the two languages directly. What must NOT
		/// be hardcoded is which file extension maps to which binding - that comes from whichever
		/// ProjectBindingDescriptor is actually registered right now.
		/// </summary>
		static bool IsRoslynLanguage(string language)
		{
			return string.Equals(language, "C#", StringComparison.Ordinal)
				|| string.Equals(language, "VB", StringComparison.Ordinal);
		}

		static bool IsSupportedExtension(string extension)
		{
			return SD.ProjectService.ProjectBindings.Any(b =>
				IsRoslynLanguage(b.Language) && string.Equals(b.ProjectFileExtension, extension, StringComparison.OrdinalIgnoreCase));
		}

		static bool IsVBProject(IProject project)
		{
			string extension = Path.GetExtension(project.FileName.ToString());
			return SD.ProjectService.ProjectBindings.Any(b =>
				string.Equals(b.Language, "VB", StringComparison.Ordinal) && string.Equals(b.ProjectFileExtension, extension, StringComparison.OrdinalIgnoreCase));
		}

		static void EnsureProject(IProject project)
		{
			if (projectIds.ContainsKey(project))
				return;
			var projectId = ProjectId.CreateNewId();
			ProjectInfo info;
			if (IsVBProject(project)) {
				info = ProjectInfo.Create(
					projectId, VersionStamp.Create(), project.Name, project.Name, LanguageNames.VisualBasic,
					compilationOptions: new VB.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
			} else {
				info = ProjectInfo.Create(
					projectId, VersionStamp.Create(), project.Name, project.Name, LanguageNames.CSharp,
					compilationOptions: new CS.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
			}
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
				var targetFramework = ProjectTargetFrameworkService.GetActiveTargetFramework(project);
				var snapshot = LanguageServiceProjectSnapshot.FromProject(project, targetFramework);
				SyncReferences(project, projectId, snapshot);
				SyncCompilationOptions(projectId, snapshot);
				SyncDocumentList(projectId, snapshot);
			} else {
				SyncOpenDocumentText(projectId);
			}
		}

		/// <summary>Full rescan of the project's Compile items: adds new files, removes deleted
		/// ones, and updates content for any whose disk/live-buffer text no longer matches.</summary>
		static void SyncDocumentList(ProjectId projectId, LanguageServiceProjectSnapshot snapshot)
		{
			var currentProject = workspace.CurrentSolution.GetProject(projectId);
			var existingDocsByPath = currentProject.Documents.ToDictionary(d => d.FilePath, StringComparer.OrdinalIgnoreCase);

			foreach (var path in snapshot.DocumentFileNames) {
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
						Microsoft.CodeAnalysis.DocumentId.CreateNewId(projectId), Path.GetFileName(path),
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
		static void SyncReferences(IProject project, ProjectId projectId, LanguageServiceProjectSnapshot snapshot)
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

			var desiredMetadataRefs = GetMetadataReferences(project, referencedProjectOutputs).ToList();
			var desiredMetadataPaths = new HashSet<string>(
				desiredMetadataRefs.OfType<PortableExecutableReference>().Select(reference => reference.FilePath),
				StringComparer.OrdinalIgnoreCase);
			foreach (var path in snapshot.MetadataReferenceFileNames) {
				if (File.Exists(path) && desiredMetadataPaths.Add(path))
					desiredMetadataRefs.Add(MetadataReference.CreateFromFile(path));
			}
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

		static void SyncCompilationOptions(ProjectId projectId, LanguageServiceProjectSnapshot snapshot)
		{
			var project = workspace.CurrentSolution.GetProject(projectId);
			ParseOptions parseOptions;
			if (project.Language == LanguageNames.VisualBasic) {
				var languageVersion = VB.LanguageVersion.Default;
				if (!string.IsNullOrWhiteSpace(snapshot.LanguageVersion))
					VB.LanguageVersionFacts.TryParse(snapshot.LanguageVersion, ref languageVersion);

				// VB preprocessor symbols are name/value pairs (not just names like C#'s) - Roslyn's
				// own convention for a plain #Const NAME without a value is `true`.
				var symbols = snapshot.PreprocessorSymbols
					.Select(name => new KeyValuePair<string, object>(name, true))
					.ToArray();
				parseOptions = new VB.VisualBasicParseOptions(languageVersion, preprocessorSymbols: symbols);
			} else {
				var languageVersion = CS.LanguageVersion.Default;
				if (!string.IsNullOrWhiteSpace(snapshot.LanguageVersion))
					CS.LanguageVersionFacts.TryParse(snapshot.LanguageVersion, out languageVersion);

				parseOptions = new CS.CSharpParseOptions(languageVersion, preprocessorSymbols: snapshot.PreprocessorSymbols);
			}

			var solution = project.Solution.WithProjectParseOptions(projectId, parseOptions);
			workspace.TryApplyChanges(solution);
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

		/// <summary>
		/// Finds every reference to the symbol at the given file:line/column across the whole
		/// solution, using Roslyn's own <see cref="Microsoft.CodeAnalysis.FindSymbols.SymbolFinder"/> -
		/// this is the modern replacement for the deleted NRefactory-era find-references engine
		/// (Src\Services\RefactoringService, see doc/technotes/csharp-roslyn.md Phase 3); unlike
		/// that engine it needs no persistent symbol index, since SymbolFinder walks the
		/// already-in-memory Roslyn solution built by <see cref="GetSolution"/>.
		/// </summary>
		public static async Task<IReadOnlyList<ReferenceLocation>> FindReferencesAtAsync(
			string filePath, ICSharpCode.AvalonEdit.Document.TextLocation location, CancellationToken cancellationToken = default)
		{
			var document = FindDocument(filePath);
			if (document == null)
				return Array.Empty<ReferenceLocation>();

			var symbol = GetSymbolAt(document, location);
			if (symbol == null)
				return Array.Empty<ReferenceLocation>();

			var referencedSymbols = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder
				.FindReferencesAsync(symbol, document.Project.Solution, cancellationToken)
				.ConfigureAwait(false);

			return referencedSymbols.SelectMany(r => r.Locations).ToList();
		}

		/// <summary>
		/// Renames <paramref name="symbol"/> to <paramref name="newName"/> across the whole solution
		/// via Roslyn's own <see cref="Microsoft.CodeAnalysis.Rename.Renamer"/> - the modern
		/// replacement for the deleted NRefactory-era FindReferenceService.RenameSymbol - and writes
		/// every changed document back out: to the live editor buffer (if the file is currently
		/// open, so the user sees the change immediately and it participates in normal undo/save),
		/// or straight to disk otherwise. <paramref name="renameOverloads"/>/<paramref name="renameInStrings"/>/
		/// <paramref name="renameInComments"/> all default to false, matching Visual Studio's own Rename
		/// dialog defaults - renaming inside string literals/comments is a text match, not something
		/// Roslyn can prove is the same symbol, so it's opt-in.
		///
		/// <paramref name="renameFile"/> only takes effect for a non-partial named type whose single
		/// declaring file's name (without extension) currently matches the symbol's own name - the
		/// same "does the file look like it's meant to track the type" heuristic Visual Studio uses to
		/// decide whether to even offer a file rename. Deliberately NOT plumbed through Renamer's own
		/// SymbolRenameOptions.RenameFile: that flag only tells the Renamer to prefer certain reference
		/// text when a file is renamed elsewhere, it does not perform a rename itself - the actual
		/// physical rename has to go through SD's own <see cref="SD.FileService.RenameFile"/> (which
		/// handles re-pointing an already-open editor to the new path) plus updating the owning
		/// project's FileProjectItem for non-SDK-style projects (SDK-style projects re-discover the
		/// renamed file automatically via their implicit glob, same as a newly added file).
		/// </summary>
		public static async Task RenameSymbolAsync(
			ISymbol symbol, string newName, bool renameOverloads = false, bool renameInStrings = false,
			bool renameInComments = false, bool renameFile = false, CancellationToken cancellationToken = default)
		{
			string oldFilePath = null;
			string newFilePath = null;
			if (renameFile && symbol is INamedTypeSymbol typeSymbol && typeSymbol.DeclaringSyntaxReferences.Length == 1) {
				var declaringPath = typeSymbol.DeclaringSyntaxReferences[0].SyntaxTree.FilePath;
				if (!string.IsNullOrEmpty(declaringPath)
					&& string.Equals(Path.GetFileNameWithoutExtension(declaringPath), symbol.Name, StringComparison.Ordinal)) {
					oldFilePath = declaringPath;
					newFilePath = Path.Combine(Path.GetDirectoryName(declaringPath)!, newName + Path.GetExtension(declaringPath));
				}
			}

			var oldSolution = GetSolution();
			var options = new Microsoft.CodeAnalysis.Rename.SymbolRenameOptions(
				RenameOverloads: renameOverloads, RenameInStrings: renameInStrings, RenameInComments: renameInComments, RenameFile: false);
			var newSolution = await Microsoft.CodeAnalysis.Rename.Renamer
				.RenameSymbolAsync(oldSolution, symbol, options, newName, cancellationToken)
				.ConfigureAwait(true);

			// Physically rename the file before writing any new content to it - at this point the
			// file (if open) is not yet dirty, so RenameFile's already-open-editor handling doesn't
			// have to contend with unsaved changes. The renamed identifier text is written afterwards,
			// once the file already lives at its new path.
			if (oldFilePath != null)
				RenameProjectFile(oldFilePath, newFilePath);

			var solutionChanges = newSolution.GetChanges(oldSolution);
			foreach (var projectChanges in solutionChanges.GetProjectChanges()) {
				foreach (var documentId in projectChanges.GetChangedDocuments()) {
					var newDocument = newSolution.GetDocument(documentId);
					if (newDocument?.FilePath == null)
						continue;
					var newText = (await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(true)).ToString();
					var targetPath = oldFilePath != null && string.Equals(newDocument.FilePath, oldFilePath, StringComparison.OrdinalIgnoreCase)
						? newFilePath
						: newDocument.FilePath;
					OpenAndReplaceText(targetPath, newText);
				}
			}
		}

		static void RenameProjectFile(string oldPath, string newPath)
		{
			if (!SD.FileService.RenameFile(oldPath, newPath, isDirectory: false))
				throw new IOException($"Failed to rename '{oldPath}' to '{newPath}'.");

			if (SD.ProjectService.FindProjectContainingFile(FileName.Create(newPath)) is MSBuildBasedProject project
				&& !project.IsSdkStyleProject) {
				var item = project.Items.OfType<FileProjectItem>()
					.FirstOrDefault(i => string.Equals(i.FileName.ToString(), oldPath, StringComparison.OrdinalIgnoreCase));
				if (item != null) {
					item.FileName = FileName.Create(newPath);
					project.Save();
				}
			}
		}

		/// <summary>
		/// Opens the file (bringing it into view if it wasn't already) and replaces its text through
		/// the editor rather than writing straight to disk - the file is left dirty afterwards, same
		/// as any other in-editor edit, so a multi-file refactoring surfaces every touched file for
		/// the user to review/undo/save rather than silently rewriting files they never opened.
		/// Shared by <see cref="RenameSymbolAsync"/> and <see cref="ExtractInterfaceAsync"/>.
		/// </summary>
		public static void OpenAndReplaceText(string filePath, string newText)
		{
			var viewContent = SD.FileService.OpenFile(FileName.Create(filePath));
			var editor = viewContent?.GetService<ITextEditor>();
			if (editor != null) {
				using (editor.Document.OpenUndoGroup()) {
					editor.Document.Text = newText;
				}
			} else {
				// No text editor available for this file (e.g. a non-text view) - nothing to leave dirty.
				File.WriteAllText(filePath, newText);
			}
		}

		/// <summary>
		/// Members eligible for interface extraction: public instance methods/properties/events,
		/// excluding constructors, operators, and accessors (matching the pre-2011 NRefactory-era
		/// ExtractInterfaceDialog's own filter - see doc/technotes/csharp-roslyn.md).
		/// </summary>
		public static IReadOnlyList<ISymbol> GetExtractInterfaceCandidateMembers(INamedTypeSymbol type)
		{
			return type.GetMembers()
				.Where(m => m.DeclaredAccessibility == RoslynAccessibility.Public && !m.IsStatic)
				.Where(m => (m is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
					|| m is IPropertySymbol || m is IEventSymbol)
				.ToArray();
		}

		/// <summary>
		/// Generates a new interface file containing <paramref name="chosenMembers"/>' signatures and,
		/// if requested, adds it to <paramref name="classSymbol"/>'s base list - the modern replacement
		/// for the pre-2011 NRefactory-era ExtractInterfaceDialog/ExtractInterfaceOptions engine (see
		/// doc/technotes/csharp-roslyn.md; that engine predates this project's Roslyn migration
		/// entirely and was never revived across two separate rewrites). C# only for now.
		/// </summary>
		public static async Task<string> ExtractInterfaceAsync(
			INamedTypeSymbol classSymbol, string interfaceName, IReadOnlyList<ISymbol> chosenMembers,
			bool addInterfaceToClass, string newFilePath, bool includeComments = false, CancellationToken cancellationToken = default)
		{
			var classSyntaxRef = classSymbol.DeclaringSyntaxReferences.First();
			var classNode = (CS.Syntax.ClassDeclarationSyntax)await classSyntaxRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(true);
			var root = await classSyntaxRef.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(true) as CS.Syntax.CompilationUnitSyntax;

			var usings = root?.Usings.Select(u => u.ToString()) ?? Enumerable.Empty<string>();
			var interfaceText = BuildInterfaceSourceText(usings, classSymbol.ContainingNamespace, interfaceName, chosenMembers, includeComments);
			File.WriteAllText(newFilePath, interfaceText);
			OpenAndReplaceText(newFilePath, interfaceText);
			AddCompileItemIfNonSdkProject(classSyntaxRef.SyntaxTree.FilePath, newFilePath);

			if (addInterfaceToClass) {
				var newBaseType = CS.SyntaxFactory.SimpleBaseType(CS.SyntaxFactory.ParseTypeName(interfaceName));
				var newBaseList = classNode.BaseList == null
					? CS.SyntaxFactory.BaseList(CS.SyntaxFactory.SingletonSeparatedList<CS.Syntax.BaseTypeSyntax>(newBaseType))
					: classNode.BaseList.AddTypes(newBaseType);
				var newClassNode = classNode.WithBaseList(newBaseList).NormalizeWhitespace();
				var newRoot = root!.ReplaceNode(classNode, newClassNode);
				OpenAndReplaceText(classSyntaxRef.SyntaxTree.FilePath, newRoot.ToFullString());
			}

			return newFilePath;
		}

		/// <summary>
		/// SDK-style projects (the common case, verified against tests/fixtures/SolutionExplorerFixture)
		/// pick up a new file under the project directory automatically via their own implicit glob -
		/// see <see cref="ICSharpCode.SharpDevelop.LanguageServices.LanguageServiceProjectSnapshot"/>'s
		/// SDK-style Compile-item handling. Legacy (non-SDK) projects have no such glob: a new file
		/// only becomes part of the project - and therefore only shows up in Solution Explorer or
		/// gets compiled - if it's explicitly added as a Compile item, so do that here.
		/// </summary>
		static void AddCompileItemIfNonSdkProject(string classFilePath, string newFilePath)
		{
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(classFilePath));
			if (project is not MSBuildBasedProject msbuildProject || msbuildProject.IsSdkStyleProject)
				return;

			var relativeInclude = FileUtility.GetRelativePath(project.Directory.ToString(), newFilePath);
			project.Items.Add(new FileProjectItem(project, ItemType.Compile, relativeInclude));
			project.Save();
		}

		static string BuildInterfaceSourceText(
			IEnumerable<string> usings, INamespaceSymbol containingNamespace, string interfaceName, IReadOnlyList<ISymbol> members,
			bool includeComments)
		{
			var sb = new System.Text.StringBuilder();
			foreach (var u in usings)
				sb.AppendLine(u);
			if (usings.Any())
				sb.AppendLine();

			bool hasNamespace = containingNamespace != null && !containingNamespace.IsGlobalNamespace;
			string indent = hasNamespace ? "\t" : "";
			if (hasNamespace) {
				sb.Append("namespace ").AppendLine(containingNamespace.ToDisplayString());
				sb.AppendLine("{");
			}

			sb.Append(indent).Append("public interface ").AppendLine(interfaceName);
			sb.Append(indent).AppendLine("{");
			foreach (var member in members) {
				sb.Append(indent).Append('\t').AppendLine(FormatInterfaceMember(member, includeComments));
			}
			sb.Append(indent).AppendLine("}");

			if (hasNamespace)
				sb.AppendLine("}");

			return sb.ToString();
		}

		static string FormatInterfaceMember(ISymbol member, bool includeComments)
		{
			string signature;
			switch (member) {
				case IMethodSymbol method: {
					var typeParams = method.TypeParameters.Length == 0
						? ""
						: "<" + string.Join(", ", method.TypeParameters.Select(t => t.Name)) + ">";
					var parameters = string.Join(", ", method.Parameters.Select(FormatParameter));
					var constraints = string.Join(" ", method.TypeParameters
						.Select(FormatTypeParameterConstraints)
						.Where(c => c != null));
					signature = $"{method.ReturnType.ToDisplayString()} {method.Name}{typeParams}({parameters})"
						+ (constraints.Length > 0 ? " " + constraints : "") + ";";
					break;
				}
				case IPropertySymbol property: {
					var accessors = property.GetMethod != null ? "get; " : "";
					accessors += property.SetMethod != null ? "set; " : "";
					signature = $"{property.Type.ToDisplayString()} {property.Name} {{ {accessors}}}";
					break;
				}
				case IEventSymbol evt:
					signature = $"event {evt.Type.ToDisplayString()} {evt.Name};";
					break;
				default:
					signature = "// unsupported member kind: " + member.Name;
					break;
			}

			if (!includeComments)
				return signature;

			var comment = GetXmlDocComment(member);
			return comment == null ? signature : comment + "\n\t" + signature;
		}

		/// <summary>
		/// Builds a `where T : ...` clause for a generic method's type parameter, or null if the
		/// parameter has no constraints. Roslyn's <see cref="ITypeParameterSymbol"/> only exposes
		/// constraint flags/types, not source text, so this has to be assembled by hand rather than
		/// copied verbatim like <see cref="GetXmlDocComment"/> does for doc comments.
		/// </summary>
		static string FormatTypeParameterConstraints(ITypeParameterSymbol typeParameter)
		{
			var constraints = new List<string>();
			if (typeParameter.HasReferenceTypeConstraint)
				constraints.Add("class");
			if (typeParameter.HasValueTypeConstraint)
				constraints.Add("struct");
			if (typeParameter.HasNotNullConstraint)
				constraints.Add("notnull");
			if (typeParameter.HasUnmanagedTypeConstraint)
				constraints.Add("unmanaged");
			constraints.AddRange(typeParameter.ConstraintTypes.Select(t => t.ToDisplayString()));
			if (typeParameter.HasConstructorConstraint)
				constraints.Add("new()");

			return constraints.Count == 0 ? null : $"where {typeParameter.Name} : {string.Join(", ", constraints)}";
		}

		/// <summary>
		/// Returns the member's original "///" doc comment block verbatim (not the semantically
		/// processed XML from GetDocumentationCommentXml()), so the extracted interface member keeps
		/// exactly what the author wrote on the class member.
		/// </summary>
		static string GetXmlDocComment(ISymbol member)
		{
			var syntaxRef = member.DeclaringSyntaxReferences.FirstOrDefault();
			if (syntaxRef == null)
				return null;
			var node = syntaxRef.GetSyntax();
			var docTrivia = node.GetLeadingTrivia()
				.Select(t => t.GetStructure())
				.OfType<CS.Syntax.DocumentationCommentTriviaSyntax>()
				.FirstOrDefault();
			return docTrivia?.ToFullString().Trim();
		}

		static string FormatParameter(IParameterSymbol parameter)
		{
			var modifier = parameter.RefKind switch {
				RefKind.Ref => "ref ",
				RefKind.Out => "out ",
				RefKind.In => "in ",
				_ => parameter.IsParams ? "params " : "",
			};
			return $"{modifier}{parameter.Type.ToDisplayString()} {parameter.Name}";
		}
	}
}
