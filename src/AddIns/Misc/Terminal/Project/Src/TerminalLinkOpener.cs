using System.Diagnostics;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;

namespace OpenDevelop.Terminal
{
	/// <summary>A link on the screen whose target was checked: the file exists, or it is an http(s) URL.</summary>
	sealed record TerminalLinkHit(TerminalLinkCandidate Candidate, string Target, int Row, int StartColumn, int EndColumn);

	/// <summary>Resolves link candidates to real targets and opens them. UI thread.</summary>
	static class TerminalLinkOpener
	{
		/// <summary>
		/// The full path of an existing file for a file candidate, the URL for a URL candidate, else null. Relative paths are
		/// tried against the folder the terminal started in, the solution folder and every project folder (the shell's
		/// current directory is not known).
		/// </summary>
		static readonly Dictionary<string, (DateTime when, string target)> cache = new Dictionary<string, (DateTime, string)>();
		static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);

		/// <summary>
		/// <see cref="ResolveUncached"/> remembered for a couple of seconds: hovering with Ctrl held asks for the same row
		/// on every mouse move, and each ask would otherwise stat the file under every project folder again.
		/// </summary>
		public static string Resolve(TerminalLinkCandidate candidate, string startDirectory)
		{
			if (candidate.Kind == TerminalLinkKind.Url)
				return candidate.Path;
			string key = startDirectory + "|" + candidate.Path;
			var now = DateTime.UtcNow;
			if (cache.TryGetValue(key, out var hit) && now - hit.when < CacheLifetime)
				return hit.target;
			string target = ResolveUncached(candidate, startDirectory);
			if (cache.Count > 512)
				cache.Clear();
			cache[key] = (now, target);
			return target;
		}

		static string ResolveUncached(TerminalLinkCandidate candidate, string startDirectory)
		{
			string path = candidate.Path;
			if (path.StartsWith("~/", StringComparison.Ordinal))
				path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
			if (System.IO.Path.IsPathRooted(path))
				return System.IO.File.Exists(path) ? System.IO.Path.GetFullPath(path) : null;
			foreach (var baseDirectory in BaseDirectories(startDirectory)) {
				string full;
				try {
					full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, path));
				} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) {
					continue;
				}
				if (System.IO.File.Exists(full))
					return full;
			}
			return null;
		}

		static IEnumerable<string> BaseDirectories(string startDirectory)
		{
			if (!string.IsNullOrEmpty(startDirectory))
				yield return startDirectory;
			var solution = SD.ProjectService?.CurrentSolution;
			if (solution == null)
				yield break;
			if (solution.Directory != null)
				yield return solution.Directory.ToString();
			foreach (var project in solution.Projects) {
				if (project.Directory != null)
					yield return project.Directory.ToString();
			}
		}

		/// <summary>Jumps to the file position in the editor, or hands an http(s) URL to the system browser (only when <paramref name="launchUrls"/>).</summary>
		public static void Open(TerminalLinkHit hit, bool launchUrls)
		{
			if (hit.Candidate.Kind == TerminalLinkKind.File) {
				SD.FileService.JumpToFilePosition(FileName.Create(hit.Target), Math.Max(1, hit.Candidate.Line), Math.Max(1, hit.Candidate.Column));
			} else if (launchUrls) {
				Process.Start(new ProcessStartInfo(hit.Target) { UseShellExecute = true });
			}
		}
	}
}
