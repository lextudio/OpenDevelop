#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;

namespace ICSharpCode.SearchAndReplace.Portable;

public sealed class PortableSearchService
{
	readonly PortableSearchEngine engine;

	public PortableSearchService()
		: this(new PortableSearchEngine())
	{
	}

	public PortableSearchService(PortableSearchEngine engine)
	{
		this.engine = engine;
	}

	public PortableSearchRunResult FindAll(
		PortableSearchOptions options,
		CancellationToken cancellationToken = default,
		IProgress<int>? searchedFileProgress = null)
	{
		var results = engine.FindAll(options, out var searchedFileCount, cancellationToken, searchedFileProgress);
		return new PortableSearchRunResult(results, searchedFileCount);
	}

	public PortableSearchRunResult FindAll(
		PortableSearchOptions options,
		PortableSearchScope scope,
		CancellationToken cancellationToken = default,
		IProgress<int>? searchedFileProgress = null)
	{
		return FindAll(ApplyScope(options, scope), cancellationToken, searchedFileProgress);
	}

	public PortableReplaceRunResult ReplaceListed(IEnumerable<PortableSearchResult> results, PortableSearchOptions options)
	{
		var plan = CreateReplacePlan(results, options);
		return ApplyReplacePlan(plan);
	}

	public PortableReplaceRunResult ReplaceListed(IEnumerable<PortableSearchResult> results, PortableSearchOptions options, PortableSearchScope scope)
	{
		return ReplaceListed(results, ApplyScope(options, scope));
	}

	public PortableReplacePlan CreateReplacePlan(IEnumerable<PortableSearchResult> results, PortableSearchOptions options)
	{
		return engine.CreateReplacePlan(results, options);
	}

	public PortableReplacePlan CreateReplacePlan(IEnumerable<PortableSearchResult> results, PortableSearchOptions options, PortableSearchScope scope)
	{
		return CreateReplacePlan(results, ApplyScope(options, scope));
	}

	public PortableReplaceRunResult ApplyReplacePlan(PortableReplacePlan plan)
	{
		return engine.ApplyReplacePlan(plan);
	}

	static PortableSearchOptions ApplyScope(PortableSearchOptions options, PortableSearchScope scope)
	{
		if (scope.IsDirectory)
			return options with { RootDirectory = scope.Directory ?? options.RootDirectory, FilePaths = null };

		return options with { FilePaths = scope.FilePaths };
	}
}
