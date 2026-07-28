#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ICSharpCode.SearchAndReplace.Portable;

public sealed class PortableSearchResultGrouper
{
	public IReadOnlyList<PortableSearchResultGroup> Group(
		IEnumerable<PortableSearchResult> results,
		PortableSearchResultGroupingKind groupingKind,
		Func<string, string?>? projectByFile = null)
	{
		var resultArray = results.ToArray();
		return groupingKind switch
		{
			PortableSearchResultGroupingKind.Flat => [new PortableSearchResultGroup("Results", resultArray, [])],
			PortableSearchResultGroupingKind.PerFile => GroupByFile(resultArray),
			PortableSearchResultGroupingKind.PerProject => GroupByProject(resultArray, projectByFile),
			PortableSearchResultGroupingKind.PerProjectAndFile => GroupByProjectAndFile(resultArray, projectByFile),
			_ => throw new ArgumentOutOfRangeException(nameof(groupingKind))
		};
	}

	static IReadOnlyList<PortableSearchResultGroup> GroupByFile(IReadOnlyList<PortableSearchResult> results) =>
		results
			.GroupBy(result => result.FilePath, StringComparer.OrdinalIgnoreCase)
			.Select(group => new PortableSearchResultGroup(group.Key, group.ToArray(), []))
			.ToArray();

	static IReadOnlyList<PortableSearchResultGroup> GroupByProject(
		IReadOnlyList<PortableSearchResult> results,
		Func<string, string?>? projectByFile) =>
		results
			.GroupBy(result => GetProjectName(result.FilePath, projectByFile), StringComparer.OrdinalIgnoreCase)
			.Select(group => new PortableSearchResultGroup(group.Key, group.ToArray(), []))
			.ToArray();

	static IReadOnlyList<PortableSearchResultGroup> GroupByProjectAndFile(
		IReadOnlyList<PortableSearchResult> results,
		Func<string, string?>? projectByFile) =>
		results
			.GroupBy(result => GetProjectName(result.FilePath, projectByFile), StringComparer.OrdinalIgnoreCase)
			.Select(group => new PortableSearchResultGroup(group.Key, [], GroupByFile(group.ToArray())))
			.ToArray();

	static string GetProjectName(string filePath, Func<string, string?>? projectByFile)
	{
		var projectName = projectByFile?.Invoke(filePath);
		if (!string.IsNullOrWhiteSpace(projectName))
			return projectName;

		return Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "No project";
	}
}
