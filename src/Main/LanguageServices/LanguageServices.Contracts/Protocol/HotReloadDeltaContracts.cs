// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.

#nullable enable

using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.LanguageServices.Protocol;

/// <summary>Stable key for one evaluated project/TFM slice in the Roslyn host workspace.</summary>
public sealed record HotReloadProjectId(string ProjectFileName, string? TargetFramework);

/// <summary>
/// IDE-to-host launch snapshot. Both images are copied before the debuggee starts, so an ordinary
/// rebuild cannot silently pair a current Roslyn compilation with a different module or PDB.
/// </summary>
public sealed record HotReloadBaselineRequest(HotReloadProjectId Project, byte[] ModuleImage, byte[] PdbImage);

/// <summary>
/// IDE-to-host request to calculate the next delta for the application as it runs now.
/// <paramref name="AdditionalTexts"/> carries the editor's current text of source-generator inputs
/// (XAML read by the XAML source generator): those are AdditionalFiles, which the ordinary
/// C# document synchronisation does not track, so the request brings them up to date first.
/// <paramref name="PreviousDeltaApplied"/> says whether the application accepted the delta the
/// previous request returned: the host then builds on that generation, otherwise it discards it and
/// stays on the generation the application still runs. Deltas are a chain; one computed against the
/// wrong generation is rejected or corrupts the running module.
/// </summary>
public sealed record HotReloadDeltaRequest(HotReloadProjectId Project, HotReloadAdditionalText[]? AdditionalTexts = null,
	bool PreviousDeltaApplied = false);

/// <summary>Current text of one AdditionalFile (full path) of the project.</summary>
public sealed record HotReloadAdditionalText(string FileName, string Text);

public enum HotReloadDeltaStatus
{
	Applied,
	NoChanges,
	RudeEdit,
	CompilationError,
	Failed,
}

/// <summary>
/// One serializable CLR metadata update, identified by the loaded module MVID.
/// <paramref name="LineMaps"/> are the lines the edit left unchanged in each edited C# document; a debugger uses them
/// to move a frame that is executing an updated method onto the new version at a statement both versions share.
/// </summary>
public sealed record HotReloadModuleDelta(
	System.Guid Mvid,
	byte[] Metadata,
	byte[] IL,
	byte[] Pdb,
	int[] UpdatedTypeTokens,
	int[]? UpdatedMethodTokens = null,
	HotReloadSourceLineMap[]? LineMaps = null,
	string[]? MetadataUpdateHandlers = null,
	string[]? UpdatedTypeNames = null);

/// <summary>
/// <paramref name="LineCount"/> lines starting at <paramref name="OldStartLine"/> in the text the running code was compiled
/// from are the same lines starting at <paramref name="NewStartLine"/> in the edited text (1-based).
/// </summary>
public sealed record HotReloadUnchangedLines(int OldStartLine, int NewStartLine, int LineCount);

/// <summary>The unchanged lines of one document, keyed by the path its PDB records.</summary>
public sealed record HotReloadSourceLineMap(string DocumentPath, HotReloadUnchangedLines[] UnchangedLines)
{
	/// <summary>Above this many line pairs in the edited middle of a file, only the common start and end count.</summary>
	const long MaxDiffCells = 4_000_000;

	/// <summary>
	/// The lines <paramref name="newText"/> keeps from <paramref name="oldText"/>, in order: the common first and last lines,
	/// and between them a longest common subsequence of lines. The two texts need not share an edit history, which is why
	/// this compares lines rather than asking for text changes. Errs toward fewer lines: a line it misses only costs a remap.
	/// </summary>
	public static HotReloadUnchangedLines[] Compute(string oldText, string newText)
	{
		var a = SplitLines(oldText);
		var b = SplitLines(newText);
		var prefix = 0;
		while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
			prefix++;
		var suffix = 0;
		while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
			suffix++;

		var pairs = new List<(int Old, int New)>();
		for (var i = 0; i < prefix; i++)
			pairs.Add((i, i));
		int oldCount = a.Length - prefix - suffix, newCount = b.Length - prefix - suffix;
		if (oldCount > 0 && newCount > 0 && (long)oldCount * newCount <= MaxDiffCells) {
			var lcs = new int[oldCount + 1, newCount + 1];
			for (var i = oldCount - 1; i >= 0; i--)
				for (var j = newCount - 1; j >= 0; j--)
					lcs[i, j] = a[prefix + i] == b[prefix + j] ? lcs[i + 1, j + 1] + 1 : System.Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
			for (int i = 0, j = 0; i < oldCount && j < newCount;) {
				if (a[prefix + i] == b[prefix + j]) {
					pairs.Add((prefix + i, prefix + j));
					i++;
					j++;
				} else if (lcs[i + 1, j] >= lcs[i, j + 1]) {
					i++;
				} else {
					j++;
				}
			}
		}
		for (var k = suffix; k > 0; k--)
			pairs.Add((a.Length - k, b.Length - k));

		var ranges = new List<HotReloadUnchangedLines>();
		foreach (var (oldLine, newLine) in pairs) {
			if (ranges.Count > 0) {
				var last = ranges[ranges.Count - 1];
				if (last.OldStartLine + last.LineCount == oldLine + 1 && last.NewStartLine + last.LineCount == newLine + 1) {
					ranges[ranges.Count - 1] = last with { LineCount = last.LineCount + 1 };
					continue;
				}
			}
			ranges.Add(new HotReloadUnchangedLines(oldLine + 1, newLine + 1, 1));
		}
		return ranges.ToArray();
	}

	// Line breaks as the compiler counts them for sequence points: \r\n, \r or \n.
	static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}

/// <summary>Host result for baseline capture or delta production.</summary>
public sealed record HotReloadDeltaResponse(
	HotReloadDeltaStatus Status,
	HotReloadModuleDelta? Delta,
	string Diagnostic);
