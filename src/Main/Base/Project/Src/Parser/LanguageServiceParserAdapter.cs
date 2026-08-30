using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.Search;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Refactoring;
using ICSharpCode.TypeSystem;
using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.SharpDevelop.Parser;

// Compatibility adapter for hosts that still have legacy IParserService callers
// but provide language features through ILanguageService implementations.
public sealed class LanguageServiceParserAdapter : IParserService
{
	readonly object syncRoot = new object();
	readonly Dictionary<FileName, FileEntry> files = new Dictionary<FileName, FileEntry>();
	volatile Snapshot currentSnapshot;

	/// <summary>Comment tokens that the parser recognises as Task List entries.
	/// Defaults mirror Visual Studio's out-of-box set.</summary>
	public IReadOnlyList<string> TaskListTokens { get; set; } = new[]
	{
		"TODO", "HACK", "FIXME", "UNDONE", "NOTE",
	};

	public ILoadSolutionProjectsThread LoadSolutionProjectsThread { get; } = new NullLoadSolutionProjectsThread();

	public ICompilation GetCompilation(IProject project) => GetCurrentSnapshot().GetCompilation(project);

	public ICompilation GetCompilationForFile(FileName fileName)
	{
		var project = SD.ProjectService.FindProjectContainingFile(fileName);
		return project != null ? GetCompilation(project) : MinimalCorlib.Instance.CreateCompilation();
	}

	public ISolutionSnapshotWithProjectMapping GetCurrentSolutionSnapshot() => GetCurrentSnapshot();

	public void InvalidateCurrentSolutionSnapshot()
	{
		currentSnapshot = null;
	}

	public IUnresolvedFile GetExistingUnresolvedFile(FileName fileName, ITextSourceVersion version = null, IProject parentProject = null)
	{
		return GetEntry(fileName, false)?.Get(parentProject)?.UnresolvedFile;
	}

	public ParseInformation GetCachedParseInformation(FileName fileName, ITextSourceVersion version = null, IProject parentProject = null)
	{
		return GetEntry(fileName, false)?.Get(parentProject)?.ParseInformation;
	}

	public ParseInformation Parse(FileName fileName, ITextSource fileContent = null, IProject parentProject = null, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		UpsertLanguageServiceDocument(fileName, fileContent, cancellationToken);
		var unresolvedFile = CreateUnresolvedFile(fileName, fileContent, cancellationToken);
		var parseInformation = new ParseInformation(unresolvedFile, fileContent?.Version, true);
		if (fileContent == null && File.Exists(fileName))
			fileContent = new ICSharpCode.AvalonEdit.Document.TextDocument(File.ReadAllText(fileName));
		if (fileContent != null)
			ExtractCommentTags(fileName, fileContent, parseInformation.TagComments);
		RegisterParseInformation(fileName, parentProject, parseInformation);
		return parseInformation;
	}

	public IUnresolvedFile ParseFile(FileName fileName, ITextSource fileContent = null, IProject parentProject = null, CancellationToken cancellationToken = default)
	{
		return Parse(fileName, fileContent, parentProject, cancellationToken).UnresolvedFile;
	}

	public Task<ParseInformation> ParseAsync(FileName fileName, ITextSource fileContent = null, IProject parentProject = null, CancellationToken cancellationToken = default)
	{
		return Task.Run(() => Parse(fileName, fileContent, parentProject, cancellationToken), cancellationToken);
	}

	public Task<IUnresolvedFile> ParseFileAsync(FileName fileName, ITextSource fileContent = null, IProject parentProject = null, CancellationToken cancellationToken = default)
	{
		return Task.Run(() => ParseFile(fileName, fileContent, parentProject, cancellationToken), cancellationToken);
	}

	static void ExtractCommentTags(FileName fileName, ITextSource fileContent, IList<TagComment> tagComments)
	{
		var tokens = SD.ParserService.TaskListTokens;
		if (tokens == null || tokens.Count == 0)
			return;
		var text = fileContent.Text;
		using var reader = new StringReader(text);
		int line = 1;
		string lineText;
		while ((lineText = reader.ReadLine()) != null) {
			int commentStart = lineText.IndexOf("//", StringComparison.Ordinal);
			if (commentStart >= 0) {
				string commentBody = lineText.Substring(commentStart + 2).TrimStart();
				foreach (string token in tokens) {
					if (commentBody.StartsWith(token, StringComparison.Ordinal)) {
						int col = commentStart + 1;
						string rest = commentBody.Substring(token.Length);
						tagComments.Add(new TagComment(token, new DomRegion(fileName, line, col), rest));
						break;
					}
				}
			}
			line++;
		}
	}

	public ResolveResult Resolve(ITextEditor editor, TextLocation location, ICompilation compilation = null, CancellationToken cancellationToken = default)
	{
		if (editor == null)
			throw new ArgumentNullException(nameof(editor));
		return Resolve(editor.FileName, location, editor.Document, compilation, cancellationToken);
	}

	public ResolveResult Resolve(FileName fileName, TextLocation location, ITextSource fileContent = null, ICompilation compilation = null, CancellationToken cancellationToken = default)
	{
		if (fileContent != null)
			UpsertLanguageServiceDocument(fileName, fileContent, cancellationToken);
		return ErrorResolveResult.UnknownError;
	}

	public ResolveResult ResolveSnippet(FileName fileName, TextLocation fileLocation, string codeSnippet, ITextSource fileContent = null, ICompilation compilation = null, CancellationToken cancellationToken = default) => ErrorResolveResult.UnknownError;

	public Task<ResolveResult> ResolveAsync(FileName fileName, TextLocation location, ITextSource fileContent = null, ICompilation compilation = null, CancellationToken cancellationToken = default)
	{
		return Task.Run(() => Resolve(fileName, location, fileContent, compilation, cancellationToken), cancellationToken);
	}

	public Task FindLocalReferencesAsync(FileName fileName, IVariable variable, Action<SearchResultMatch> callback, ITextSource fileContent = null, ICompilation compilation = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

	public ICodeContext ResolveContext(ITextEditor editor, TextLocation location, ICompilation compilation = null, CancellationToken cancellationToken = default)
	{
		if (editor == null)
			throw new ArgumentNullException(nameof(editor));
		return ResolveContext(editor.FileName, location, editor.Document, compilation, cancellationToken);
	}

	public ICodeContext ResolveContext(FileName fileName, TextLocation location, ITextSource fileContent = null, ICompilation compilation = null, CancellationToken cancellationToken = default)
	{
		if (fileContent != null)
			UpsertLanguageServiceDocument(fileName, fileContent, cancellationToken);
		compilation ??= GetCompilationForFile(fileName);
		var unresolvedFile = GetExistingUnresolvedFile(fileName);
		return unresolvedFile != null
			? new UnknownCodeContext(compilation, unresolvedFile, location)
			: new UnknownCodeContext(compilation);
	}

	public bool HasParser(FileName fileName) => IsCSharpOrVisualBasic(fileName);

	public void ClearParseInformation(FileName fileName)
	{
		lock (syncRoot) {
			files.Remove(fileName);
			InvalidateCurrentSolutionSnapshot();
		}
	}

	public void AddOwnerProject(FileName fileName, IProject project, bool startAsyncParse, bool isLinkedFile)
	{
		if (project == null)
			throw new ArgumentNullException(nameof(project));

		var entry = GetEntry(fileName, true);
		IUnresolvedFile existing = null;
		lock (syncRoot) {
			if (!entry.ProjectEntries.ContainsKey(project))
				entry.ProjectEntries.Add(project, new ProjectEntry());
			existing = entry.Get(project)?.UnresolvedFile ?? entry.Get(null)?.UnresolvedFile;
		}
		if (existing != null)
			project.OnParseInformationUpdated(new ParseInformationEventArgs(project, null, new ParseInformation(existing, null, false)));
		if (startAsyncParse)
			ParseFileAsync(fileName, parentProject: project).FireAndForget();
	}

	public void RemoveOwnerProject(FileName fileName, IProject project)
	{
		if (project == null)
			throw new ArgumentNullException(nameof(project));

		IUnresolvedFile oldFile = null;
		lock (syncRoot) {
			var entry = GetEntry(fileName, false);
			if (entry == null)
				return;
			if (entry.ProjectEntries.TryGetValue(project, out var projectEntry))
				oldFile = projectEntry.UnresolvedFile;
			entry.ProjectEntries.Remove(project);
			if (entry.ProjectEntries.Count == 0 && entry.LooseEntry.UnresolvedFile == null)
				files.Remove(fileName);
			InvalidateCurrentSolutionSnapshot();
		}
		if (oldFile != null)
			project.OnParseInformationUpdated(new ParseInformationEventArgs(project, oldFile, null));
	}

	public event EventHandler<ParseInformationEventArgs> ParseInformationUpdated = delegate { };

	public void RegisterUnresolvedFile(FileName fileName, IProject project, IUnresolvedFile unresolvedFile)
	{
		if (unresolvedFile == null)
			throw new ArgumentNullException(nameof(unresolvedFile));
		var parseInformation = new ParseInformation(unresolvedFile, null, false);
		if (File.Exists(fileName)) {
			try {
				var text = File.ReadAllText(fileName);
				var textSource = new ICSharpCode.AvalonEdit.Document.TextDocument(text);
				ExtractCommentTags(fileName, textSource, parseInformation.TagComments);
			} catch (IOException) {
			}
		}
		RegisterParseInformation(fileName, project, parseInformation);
	}

	Snapshot GetCurrentSnapshot()
	{
		var snapshot = currentSnapshot;
		if (snapshot == null) {
			lock (syncRoot) {
				snapshot = currentSnapshot;
				if (snapshot == null) {
					IEnumerable<IProject> projects = SD.ProjectService.CurrentSolution?.Projects ?? Enumerable.Empty<IProject>();
					snapshot = new Snapshot(projects);
					currentSnapshot = snapshot;
				}
			}
		}
		return snapshot;
	}

	FileEntry GetEntry(FileName fileName, bool create)
	{
		if (fileName == null)
			throw new ArgumentNullException(nameof(fileName));

		lock (syncRoot) {
			if (!files.TryGetValue(fileName, out var entry) && create) {
				entry = new FileEntry();
				files.Add(fileName, entry);
			}
			return entry;
		}
	}

	void RegisterParseInformation(FileName fileName, IProject project, ParseInformation parseInformation)
	{
		ParseInformationEventArgs args;
		lock (syncRoot) {
			var entry = GetEntry(fileName, true);
			var projectEntry = entry.Get(project, true);
			var oldFile = projectEntry.UnresolvedFile;
			projectEntry.ParseInformation = parseInformation;
			projectEntry.UnresolvedFile = parseInformation.UnresolvedFile;
			args = new ParseInformationEventArgs(project, oldFile, parseInformation);
			InvalidateCurrentSolutionSnapshot();
		}
		if (project != null)
			project.OnParseInformationUpdated(args);
		SD.MainThread.InvokeAsyncAndForget(() => ParseInformationUpdated(this, args));
	}

	// The first real (non-mock) IParser for .cs/.vb - see doc/technotes/csharp-roslyn.md, Phase 1.
	// It talks to the real project/compilation (via RoslynWorkspaceHelper, itself fed from
	// IProject.Items/GetEvaluatedProjectItems), so TopLevelTypeDefinitions here is real data, not
	// a stub - this is what CodeEditor's QuickClassBrowser-creation gate and IconBarManager's
	// class/member bookmarks both read.
	//
	// A dev-environment red herring during the investigation that led here: IProject.Items
	// LOOKED permanently empty (FindProjectContainingFile returning null for every file), which
	// briefly pointed at "the classic project-item model is dead, route through the outline API
	// instead". It wasn't - a manually-launched dev instance was missing the MSBuild environment
	// variables OpenDevelopAppFixture.ConfigureDotNetEnvironment sets for every test run
	// (DOTNET_ROOT/MSBuildSDKsPath/MSBuildEnableWorkloadResolver=false), so EVERY project load
	// failed with "ProjectLoadException: The SDK 'Microsoft.NET.Sdk' specified could not be
	// found" and silently downgraded to an ErrorProject placeholder with no items at all. With
	// that environment set up correctly, IProject.Items populates exactly as designed (755 items
	// for a normal WinFormsSample project, confirmed live) and RoslynParser resolves real types.
	static readonly ICSharpCode.SharpDevelop.Roslyn.RoslynParser roslynParser = new ICSharpCode.SharpDevelop.Roslyn.RoslynParser();

	static IUnresolvedFile CreateUnresolvedFile(FileName fileName, ITextSource fileContent, CancellationToken cancellationToken)
	{
		if (IsCSharpOrVisualBasic(fileName)) {
			try {
				// ParseAsync runs this on a thread-pool thread, but fileContent can be a live,
				// UI-thread-owned AvalonEdit TextDocument - reading .Text off-thread throws
				// (RoslynParser.Parse does exactly that internally). Resolve a safe, detached
				// snapshot first, same fallback-to-disk pattern UpsertLanguageServiceDocument
				// already uses for the same reason.
				var safeContent = ToThreadSafeTextSource(fileName, fileContent);
				var roslynParseInfo = roslynParser.Parse(fileName, safeContent, true, null, cancellationToken);
				if (roslynParseInfo?.UnresolvedFile != null)
					return roslynParseInfo.UnresolvedFile;
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception ex) {
				// Falls through to the plain empty file below - a missing class/member bar beats
				// a parse that throws and leaves the editor without any ParseInformation at all.
				LoggingService.Warn("RoslynParser fallback failed for " + fileName + ": " + ex.Message);
			}
		}
		return new EmptyUnresolvedFile(fileName, File.Exists(fileName) ? File.GetLastWriteTimeUtc(fileName) : null);
	}

	static ITextSource ToThreadSafeTextSource(FileName fileName, ITextSource fileContent)
	{
		if (fileContent == null)
			return null;
		try {
			return new StringTextSource(fileContent.Text);
		} catch (InvalidOperationException) {
			try {
				return File.Exists(fileName) ? new StringTextSource(File.ReadAllText(fileName)) : null;
			} catch (IOException) {
				return null;
			}
		}
	}

	static bool IsCSharpOrVisualBasic(FileName fileName)
	{
		var extension = Path.GetExtension(fileName);
		return string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(extension, ".vb", StringComparison.OrdinalIgnoreCase);
	}

	static void UpsertLanguageServiceDocument(FileName fileName, ITextSource fileContent, CancellationToken cancellationToken)
	{
		var registry = SD.GetService<LanguageServiceRegistry>();
		if (registry == null || !registry.TryGetService(fileName, out var languageService))
			return;

		string text = null;
		if (fileContent != null) {
			try {
				text = fileContent.Text;
			} catch (InvalidOperationException) {
				// ITextSource may be a UI-thread-owned TextDocument (AvalonEdit): ParseAsync runs
				// on a thread-pool thread and reading .Text there throws. Fall back to disk - the
				// upsert is best-effort and the in-memory buffer usually matches the file anyway.
			}
		}
		if (text == null && File.Exists(fileName))
			text = File.ReadAllText(fileName);
		if (text == null)
			return;

		// Never block the calling thread on UpsertDocumentAsync. Callers run on the WPF UI
		// thread (CodeEditor.FetchParseInformation -> Parse -> this), and the async chain
		// (LSP server start gate / initialize / didOpen RPC round-trip for XAML and other
		// language-server backends) resumes on the captured DispatcherSynchronizationContext -
		// a synchronous .GetResult() here deadlocks the whole app the moment the language
		// service has to do more than a fast no-op. This upsert is a side-channel notification:
		// Parse()/Resolve()/ResolveContext() build their own (unresolved) results and never
		// observe its outcome, so fire-and-forget with logging is the correct contract.
		_ = UpsertLanguageServiceDocumentAsync(languageService, new DocumentId(fileName), text, cancellationToken);
	}

	static async Task UpsertLanguageServiceDocumentAsync(ICSharpCode.SharpDevelop.LanguageServices.ILanguageService languageService, DocumentId documentId, string text, CancellationToken cancellationToken)
	{
		try {
			await languageService.UpsertDocumentAsync(documentId, text, cancellationToken).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			// Cancelled during app shutdown or by a superseded parse - expected.
		} catch (Exception ex) {
			LoggingService.Warn("Language service document upsert failed for " + documentId.FileName + ": " + ex.Message);
		}
	}

	private sealed class NullLoadSolutionProjectsThread : ILoadSolutionProjectsThread
	{
		public bool IsRunning => false;
		public event EventHandler Started { add { } remove { } }
		public event EventHandler Finished { add { } remove { } }
		public void AddJob(Action<IProgressMonitor> action, string name, double cost)
		{
			if (action == null)
				throw new ArgumentNullException(nameof(action));
			using (var monitor = new DummyProgressMonitor()) {
				action(monitor);
			}
		}
	}

	sealed class FileEntry
	{
		public ProjectEntry LooseEntry { get; } = new ProjectEntry();
		public Dictionary<IProject, ProjectEntry> ProjectEntries { get; } = new Dictionary<IProject, ProjectEntry>();

		public ProjectEntry Get(IProject project, bool create = false)
		{
			if (project == null)
				return LooseEntry;
			if (!ProjectEntries.TryGetValue(project, out var entry) && create) {
				entry = new ProjectEntry();
				ProjectEntries.Add(project, entry);
			}
			return entry;
		}
	}

	sealed class ProjectEntry
	{
		public IUnresolvedFile UnresolvedFile;
		public ParseInformation ParseInformation;
	}

	sealed class Snapshot : ISolutionSnapshotWithProjectMapping
	{
		readonly Dictionary<IProject, IProjectContent> contentByProject = new Dictionary<IProject, IProjectContent>();
		readonly Dictionary<IAssembly, IProject> projectByAssembly = new Dictionary<IAssembly, IProject>();

		public Snapshot(IEnumerable<IProject> projects)
		{
			foreach (var project in projects) {
				var content = project.ProjectContent ?? new MutableProjectContent(project.Name).SetProjectFileName(project.FileName.ToString());
				contentByProject[project] = content;
			}
		}

		public IProject GetProject(IAssembly assembly)
		{
			return assembly != null && projectByAssembly.TryGetValue(assembly, out var project) ? project : null;
		}

		public IProjectContent GetProjectContent(IProject project)
		{
			return project != null && contentByProject.TryGetValue(project, out var content) ? content : null;
		}

		public IProjectContent GetProjectContent(string projectFileName)
		{
			return contentByProject.Values.FirstOrDefault(content => FileUtility.IsEqualFileName(content.ProjectFileName, projectFileName));
		}

		public ICompilation GetCompilation(IProject project)
		{
			var content = GetProjectContent(project);
			return content != null ? GetCompilation(content) : MinimalCorlib.Instance.CreateCompilation();
		}

		public ICompilation GetCompilation(IProjectContent project)
		{
			var compilation = project != null ? project.CreateCompilation(this) : MinimalCorlib.Instance.CreateCompilation();
			if (project != null && compilation.MainAssembly != null) {
				var owner = contentByProject.FirstOrDefault(pair => pair.Value == project).Key;
				if (owner != null)
					projectByAssembly[compilation.MainAssembly] = owner;
			}
			return compilation;
		}
	}

	[Serializable]
	sealed class MutableProjectContent : IProjectContent
	{
		readonly Dictionary<string, IUnresolvedFile> files;
		readonly List<IAssemblyReference> assemblyReferences;

		public MutableProjectContent(string assemblyName)
			: this(assemblyName, assemblyName, null, null, null, Array.Empty<IUnresolvedFile>(), Array.Empty<IAssemblyReference>())
		{
		}

		MutableProjectContent(string assemblyName, string fullAssemblyName, string projectFileName, string location, object compilerSettings,
		                      IEnumerable<IUnresolvedFile> files, IEnumerable<IAssemblyReference> assemblyReferences)
		{
			AssemblyName = assemblyName ?? string.Empty;
			FullAssemblyName = string.IsNullOrEmpty(fullAssemblyName) ? AssemblyName : fullAssemblyName;
			ProjectFileName = projectFileName;
			Location = location;
			CompilerSettings = compilerSettings;
			this.files = files.ToDictionary(file => file.FileName, FileNameComparer);
			this.assemblyReferences = assemblyReferences.ToList();
		}

		public string AssemblyName { get; }
		public string FullAssemblyName { get; }
		public string ProjectFileName { get; }
		public string Location { get; }
		public IEnumerable<IUnresolvedAttribute> AssemblyAttributes => Files.SelectMany(file => file.AssemblyAttributes);
		public IEnumerable<IUnresolvedAttribute> ModuleAttributes => Files.SelectMany(file => file.ModuleAttributes);
		public IEnumerable<IUnresolvedTypeDefinition> TopLevelTypeDefinitions => Files.SelectMany(file => file.TopLevelTypeDefinitions);
		public IEnumerable<IUnresolvedFile> Files => files.Values;
		public IEnumerable<IAssemblyReference> AssemblyReferences => assemblyReferences;
		public object CompilerSettings { get; }

		static StringComparer FileNameComparer => StringComparer.OrdinalIgnoreCase;

		public IUnresolvedFile GetFile(string fileName)
		{
			return fileName != null && files.TryGetValue(fileName, out var file) ? file : null;
		}

		public ICompilation CreateCompilation() => CreateCompilation(null);

		public ICompilation CreateCompilation(ISolutionSnapshot solutionSnapshot)
		{
			return new SimpleCompilation(this, assemblyReferences);
		}

		public IAssembly Resolve(ITypeResolveContext context) => null;

		public IProjectContent SetAssemblyName(string newAssemblyName)
		{
			return With(assemblyName: newAssemblyName, fullAssemblyName: newAssemblyName);
		}

		public IProjectContent SetProjectFileName(string newProjectFileName)
		{
			return With(projectFileName: newProjectFileName);
		}

		public IProjectContent SetLocation(string newLocation)
		{
			return With(location: newLocation);
		}

		public IProjectContent AddAssemblyReferences(IEnumerable<IAssemblyReference> references)
		{
			return With(assemblyReferences: assemblyReferences.Concat(references ?? Array.Empty<IAssemblyReference>()));
		}

		public IProjectContent AddAssemblyReferences(params IAssemblyReference[] references) => AddAssemblyReferences((IEnumerable<IAssemblyReference>)references);

		public IProjectContent RemoveAssemblyReferences(IEnumerable<IAssemblyReference> references)
		{
			var remove = new HashSet<IAssemblyReference>(references ?? Array.Empty<IAssemblyReference>());
			return With(assemblyReferences: assemblyReferences.Where(reference => !remove.Contains(reference)));
		}

		public IProjectContent RemoveAssemblyReferences(params IAssemblyReference[] references) => RemoveAssemblyReferences((IEnumerable<IAssemblyReference>)references);

		public IProjectContent AddOrUpdateFiles(IEnumerable<IUnresolvedFile> newFiles)
		{
			var updated = new Dictionary<string, IUnresolvedFile>(files, FileNameComparer);
			foreach (var file in newFiles ?? Array.Empty<IUnresolvedFile>())
				updated[file.FileName] = file;
			return With(files: updated.Values);
		}

		public IProjectContent AddOrUpdateFiles(params IUnresolvedFile[] newFiles) => AddOrUpdateFiles((IEnumerable<IUnresolvedFile>)newFiles);

		public IProjectContent RemoveFiles(IEnumerable<string> fileNames)
		{
			var updated = new Dictionary<string, IUnresolvedFile>(files, FileNameComparer);
			foreach (var fileName in fileNames ?? Array.Empty<string>())
				updated.Remove(fileName);
			return With(files: updated.Values);
		}

		public IProjectContent RemoveFiles(params string[] fileNames) => RemoveFiles((IEnumerable<string>)fileNames);

		public IProjectContent UpdateProjectContent(IUnresolvedFile oldFile, IUnresolvedFile newFile)
		{
			var updated = oldFile != null ? RemoveFiles(oldFile.FileName) : this;
			return newFile != null ? updated.AddOrUpdateFiles(newFile) : updated;
		}

		public IProjectContent UpdateProjectContent(IEnumerable<IUnresolvedFile> oldFiles, IEnumerable<IUnresolvedFile> newFiles)
		{
			return RemoveFiles((oldFiles ?? Array.Empty<IUnresolvedFile>()).Select(file => file.FileName)).AddOrUpdateFiles(newFiles);
		}

		public IProjectContent SetCompilerSettings(object compilerSettings)
		{
			return With(compilerSettings: compilerSettings);
		}

		MutableProjectContent With(string assemblyName = null, string fullAssemblyName = null, string projectFileName = null, string location = null,
		                           object compilerSettings = null, IEnumerable<IUnresolvedFile> files = null,
		                           IEnumerable<IAssemblyReference> assemblyReferences = null)
		{
			return new MutableProjectContent(
				assemblyName ?? AssemblyName,
				fullAssemblyName ?? FullAssemblyName,
				projectFileName ?? ProjectFileName,
				location ?? Location,
				compilerSettings ?? CompilerSettings,
				files ?? Files,
				assemblyReferences ?? this.assemblyReferences);
		}
	}

	[Serializable]
	sealed class EmptyUnresolvedFile : IUnresolvedFile
	{
		public EmptyUnresolvedFile(string fileName, DateTime? lastWriteTime)
		{
			FileName = fileName;
			LastWriteTime = lastWriteTime;
		}

		public string FileName { get; }
		public DateTime? LastWriteTime { get; set; }
		public IList<IUnresolvedTypeDefinition> TopLevelTypeDefinitions { get; } = new List<IUnresolvedTypeDefinition>();
		public IList<IUnresolvedAttribute> AssemblyAttributes { get; } = new List<IUnresolvedAttribute>();
		public IList<IUnresolvedAttribute> ModuleAttributes { get; } = new List<IUnresolvedAttribute>();
		public IList<Error> Errors { get; } = new List<Error>();
		public IUnresolvedTypeDefinition GetTopLevelTypeDefinition(ICSharpCode.TypeSystem.TextLocation location) => null;
		public IUnresolvedTypeDefinition GetInnermostTypeDefinition(ICSharpCode.TypeSystem.TextLocation location) => null;
		public IUnresolvedMember GetMember(ICSharpCode.TypeSystem.TextLocation location) => null;
	}
}
