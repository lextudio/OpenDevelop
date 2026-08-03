// Copyright (c) 2025 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.GitAddIn
{
	/// <summary>
	/// One line's "last touched by" info from <c>git blame --porcelain</c>
	/// (doc/technotes/openlens.md §10.4). Deliberately just the fields a "last edited by X, N days
	/// ago" lens needs, not a full blame-entry model.
	/// </summary>
	public sealed class GitBlameLine
	{
		public GitBlameLine(string commitHash, string author, DateTimeOffset authorTime, string summary)
		{
			CommitHash = commitHash;
			Author = author;
			AuthorTime = authorTime;
			Summary = summary;
		}

		public string CommitHash { get; }
		public string Author { get; }
		public DateTimeOffset AuthorTime { get; }
		public string Summary { get; }

		/// <summary>Whether this line has never been committed (working-copy-only content) - a
		/// literal all-zero SHA is how `git blame` reports that.</summary>
		public bool IsUncommitted => CommitHash == "0000000000000000000000000000000000000000";
	}

	/// <summary>
	/// A minimal <c>git blame --porcelain</c> wrapper - GitAddIn had no blame-style API at all
	/// before this (only <see cref="GitGuiWrapper.Log"/>, which shells a whole-file
	/// <c>git log --stat</c> out to an external viewer with no parsed result). Blames exactly one
	/// line at a time (<paramref name="line"/> in <see cref="GetLastEditAsync"/>) rather than a
	/// range, since the only caller (<c>GitOpenLensProvider</c>) only ever needs "who last touched
	/// this declaration's header line" - a single-line blame is also a single-commit porcelain
	/// output, which is far simpler to parse correctly than a multi-line range (repeated lines from
	/// the same commit omit most of the metadata fields the second time they appear).
	/// </summary>
	public static class GitBlame
	{
		/// <summary>
		/// Returns who last touched <paramref name="line"/> (1-based) of <paramref name="fileName"/>,
		/// or <see langword="null"/> if the file isn't in a Git working copy, git isn't found, the
		/// line has no history (e.g. past end of file), or the process fails for any reason -
		/// blame is a "nice to have" annotation, never something that should throw into the editor.
		/// </summary>
		public static async Task<GitBlameLine> GetLastEditAsync(string fileName, int line, CancellationToken cancellationToken)
		{
			string wcRoot = Git.FindWorkingCopyRoot(fileName);
			if (wcRoot == null)
				return null;
			string git = Git.FindGit();
			if (git == null)
				return null;
			string relativeFileName = Git.AdaptFileName(wcRoot, fileName);

			var startInfo = new ProcessStartInfo(git) {
				WorkingDirectory = wcRoot,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			startInfo.ArgumentList.Add("blame");
			startInfo.ArgumentList.Add("--porcelain");
			startInfo.ArgumentList.Add("-L");
			startInfo.ArgumentList.Add(line + "," + line);
			startInfo.ArgumentList.Add("--");
			startInfo.ArgumentList.Add(relativeFileName);

			try {
				using var process = Process.Start(startInfo);
				if (process == null)
					return null;
				string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
				await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
				if (process.ExitCode != 0)
					return null;
				return ParsePorcelain(output);
			} catch (Exception) {
				// Uncommitted file, detached/corrupt repo state, git binary missing permissions,
				// process start failure, etc. - none of these should surface as an editor error.
				return null;
			}
		}

		static GitBlameLine ParsePorcelain(string output)
		{
			if (string.IsNullOrEmpty(output))
				return null;

			string commitHash = null;
			string author = null;
			long authorTimeUnix = 0;
			string summary = null;

			foreach (var rawLine in output.Split('\n')) {
				if (rawLine.Length == 0 || rawLine[0] == '\t')
					continue; // the blamed line's own content - not metadata

				if (commitHash == null) {
					// First line: "<sha> <origline> <finalline> [<numlines>]"
					int spaceIndex = rawLine.IndexOf(' ');
					commitHash = spaceIndex > 0 ? rawLine.Substring(0, spaceIndex) : rawLine;
					continue;
				}

				if (rawLine.StartsWith("author ", StringComparison.Ordinal))
					author = rawLine.Substring("author ".Length);
				else if (rawLine.StartsWith("author-time ", StringComparison.Ordinal))
					long.TryParse(rawLine.Substring("author-time ".Length), out authorTimeUnix);
				else if (rawLine.StartsWith("summary ", StringComparison.Ordinal))
					summary = rawLine.Substring("summary ".Length);
			}

			if (commitHash == null || author == null)
				return null;
			return new GitBlameLine(commitHash, author, DateTimeOffset.FromUnixTimeSeconds(authorTimeUnix), summary ?? string.Empty);
		}

		/// <summary>"3 days ago"/"just now"/"2 years ago" - deliberately coarse (doc §10.4's
		/// "4 days ago" example), not a full localized relative-time library.</summary>
		public static string FormatRelativeTime(DateTimeOffset time, DateTimeOffset now)
		{
			var delta = now - time;
			if (delta.TotalSeconds < 60)
				return "just now";
			if (delta.TotalMinutes < 60)
				return FormatUnit(delta.TotalMinutes, "minute");
			if (delta.TotalHours < 24)
				return FormatUnit(delta.TotalHours, "hour");
			if (delta.TotalDays < 30)
				return FormatUnit(delta.TotalDays, "day");
			if (delta.TotalDays < 365)
				return FormatUnit(delta.TotalDays / 30, "month");
			return FormatUnit(delta.TotalDays / 365, "year");
		}

		static string FormatUnit(double value, string unit)
		{
			int rounded = Math.Max(1, (int)value);
			return rounded == 1 ? "1 " + unit + " ago" : rounded + " " + unit + "s ago";
		}
	}
}
