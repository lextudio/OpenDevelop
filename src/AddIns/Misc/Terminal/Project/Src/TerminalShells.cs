using ICSharpCode.SharpDevelop;

namespace OpenDevelop.Terminal
{
	/// <summary>The shells a new terminal can run and the user's default.</summary>
	static class TerminalShells
	{
		const string DefaultShellKey = "Terminal.DefaultShell";

		/// <summary>The explicitly chosen shell, or "" for the login shell.</summary>
		public static string Stored => SD.PropertyService.Get(DefaultShellKey, "");

		public static string Default {
			get {
				var chosen = SD.PropertyService.Get(DefaultShellKey, "");
				return !string.IsNullOrEmpty(chosen) && File.Exists(chosen) ? chosen : Detect().FirstOrDefault() ?? FallbackShell();
			}
			set => SD.PropertyService.Set(DefaultShellKey, value ?? "");
		}

		static string FallbackShell()
			=> OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : "/bin/sh";

		/// <summary>Full paths of installed shells, the login shell ($SHELL / ComSpec) first.</summary>
		public static IReadOnlyList<string> Detect()
		{
			var found = new List<string>();
			void Add(string path)
			{
				if (!string.IsNullOrEmpty(path) && File.Exists(path) && !found.Contains(path, StringComparer.OrdinalIgnoreCase))
					found.Add(path);
			}

			if (OperatingSystem.IsWindows()) {
				var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
				var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
				Add(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
				Add(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
				Add(Environment.GetEnvironmentVariable("ComSpec"));
				Add(Path.Combine(programFiles, "Git", "bin", "bash.exe"));
				Add(Path.Combine(system, "wsl.exe"));
			} else {
				Add(Environment.GetEnvironmentVariable("SHELL"));
				try {
					foreach (var line in File.ReadAllLines("/etc/shells")) {
						var entry = line.Trim();
						if (entry.StartsWith("/", StringComparison.Ordinal))
							Add(entry);
					}
				} catch (IOException) {
				}
			}
			return found;
		}

		public static string DisplayName(string path) => Path.GetFileNameWithoutExtension(path);
	}
}
