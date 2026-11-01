using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BlueprintBinding
{
	/// <summary>
	/// Finds the <c>blueprint-compiler</c> program. It is GNOME's own tool (Python, with PyGObject and the GIR typelibs), so the
	/// IDE does not ship it: it looks where a package manager puts it. Order: the <c>OD_BLUEPRINT_COMPILER</c> environment
	/// variable (a full path), then the <c>PATH</c>, then the usual install folders a GUI process may not have on its PATH
	/// (Homebrew, <c>/usr/local</c>, <c>/usr</c>, <c>~/.local/bin</c>).
	/// </summary>
	public static class BlueprintCompilerLocator
	{
		public const string EnvironmentVariable = "OD_BLUEPRINT_COMPILER";

		static IEnumerable<string> Names => OperatingSystem.IsWindows()
			? new[] { "blueprint-compiler.exe", "blueprint-compiler.cmd", "blueprint-compiler.bat", "blueprint-compiler" }
			: new[] { "blueprint-compiler" };

		/// <summary>The compiler's full path, or null when none is installed.</summary>
		public static string? Find()
			=> Find(Environment.GetEnvironmentVariable(EnvironmentVariable), Environment.GetEnvironmentVariable("PATH"), DefaultFolders());

		public static IEnumerable<string> DefaultFolders()
		{
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			return new[] { "/opt/homebrew/bin", "/home/linuxbrew/.linuxbrew/bin", "/usr/local/bin", "/usr/bin", string.IsNullOrEmpty(home) ? "" : Path.Combine(home, ".local", "bin") }
				.Where(folder => folder.Length > 0);
		}

		/// <summary>The testable core: <paramref name="overridePath"/> when it names a file, else the first executable of that name in
		/// <paramref name="pathVariable"/>'s folders, else in <paramref name="fallbackFolders"/>.</summary>
		public static string? Find(string? overridePath, string? pathVariable, IEnumerable<string> fallbackFolders)
		{
			if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return Path.GetFullPath(overridePath);
			var folders = (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Concat(fallbackFolders);
			foreach (var folder in folders) {
				foreach (var name in Names) {
					string candidate;
					try { candidate = Path.Combine(folder.Trim('"'), name); } catch (ArgumentException) { continue; }
					if (File.Exists(candidate)) return Path.GetFullPath(candidate);
				}
			}
			return null;
		}
	}
}
