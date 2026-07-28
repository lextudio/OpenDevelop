using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.Search;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Refactoring;
using ICSharpCode.TypeSystem;
using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.SharpDevelop.Parser;

// Minimal parser service for hosts that need the legacy IParserService contract
// but provide language features through newer services.
internal sealed class MinimalParserService : IParserService
{
	public IReadOnlyList<string> TaskListTokens { get; set; } = Array.Empty<string>();

	public ILoadSolutionProjectsThread LoadSolutionProjectsThread { get; } = new NullLoadSolutionProjectsThread();

	public ICompilation GetCompilation(IProject project) => throw new NotImplementedException();
	public ICompilation GetCompilationForFile(FileName fileName) => throw new NotImplementedException();
	public ISolutionSnapshotWithProjectMapping GetCurrentSolutionSnapshot() => throw new NotImplementedException();
	public void InvalidateCurrentSolutionSnapshot() { }

	public IUnresolvedFile GetExistingUnresolvedFile(FileName fileName, ITextSourceVersion version = null, IProject parentProject = null) => null;
	public ParseInformation GetCachedParseInformation(FileName fileName, ITextSourceVersion version = null, IProject parentProject = null) => null;

	public ParseInformation Parse(FileName fileName, ITextSource fileContent = null, IProject parentProject = null, CancellationToken cancellationToken = default) => null;
	public IUnresolvedFile ParseFile(FileName fileName, ITextSource fileContent = null, IProject parentProject = null, CancellationToken cancellationToken = default) => null;
	public Task<ParseInformation> ParseAsync(FileName fileName, ITextSource fileContent = null, IProject parentProject = null, CancellationToken cancellationToken = default) => Task.FromResult<ParseInformation>(null);
	public Task<IUnresolvedFile> ParseFileAsync(FileName fileName, ITextSource fileContent = null, IProject parentProject = null, CancellationToken cancellationToken = default) => Task.FromResult<IUnresolvedFile>(null);

	public ResolveResult Resolve(ITextEditor editor, TextLocation location, ICompilation compilation = null, CancellationToken cancellationToken = default) => null;
	public ResolveResult Resolve(FileName fileName, TextLocation location, ITextSource fileContent = null, ICompilation compilation = null, CancellationToken cancellationToken = default) => null;
	public ResolveResult ResolveSnippet(FileName fileName, TextLocation fileLocation, string codeSnippet, ITextSource fileContent = null, ICompilation compilation = null, CancellationToken cancellationToken = default) => null;
	public Task<ResolveResult> ResolveAsync(FileName fileName, TextLocation location, ITextSource fileContent = null, ICompilation compilation = null, CancellationToken cancellationToken = default) => Task.FromResult<ResolveResult>(null);
	public Task FindLocalReferencesAsync(FileName fileName, IVariable variable, Action<SearchResultMatch> callback, ITextSource fileContent = null, ICompilation compilation = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
	public ICodeContext ResolveContext(ITextEditor editor, TextLocation location, ICompilation compilation = null, CancellationToken cancellationToken = default) => null;
	public ICodeContext ResolveContext(FileName fileName, TextLocation location, ITextSource fileContent = null, ICompilation compilation = null, CancellationToken cancellationToken = default) => null;

	public bool HasParser(FileName fileName) => false;
	public void ClearParseInformation(FileName fileName) { }
	public void AddOwnerProject(FileName fileName, IProject project, bool startAsyncParse, bool isLinkedFile) { }
	public void RemoveOwnerProject(FileName fileName, IProject project) { }
	public event EventHandler<ParseInformationEventArgs> ParseInformationUpdated { add { } remove { } }

	public void RegisterUnresolvedFile(FileName fileName, IProject project, IUnresolvedFile unresolvedFile) { }

	private sealed class NullLoadSolutionProjectsThread : ILoadSolutionProjectsThread
	{
		public bool IsRunning => false;
		public event EventHandler Started { add { } remove { } }
		public event EventHandler Finished { add { } remove { } }
		public void AddJob(Action<IProgressMonitor> action, string name, double cost) { }
	}
}
