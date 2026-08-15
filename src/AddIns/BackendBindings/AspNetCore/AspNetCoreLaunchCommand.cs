using System;
using System.Diagnostics;
using System.IO;

namespace ICSharpCode.AspNetCore
{
	public static class AspNetCoreLaunchCommand
	{
		public static ProcessStartInfo Create(string projectFileName, AspNetCoreLaunchProfile profile, bool noBuild = true)
		{
			if (string.IsNullOrWhiteSpace(projectFileName)) throw new ArgumentException("A project file is required.", nameof(projectFileName));
			if (profile == null) throw new ArgumentNullException(nameof(profile));
			var projectFile = Path.GetFullPath(projectFileName);
			var projectDirectory = Path.GetDirectoryName(projectFile)!;
			var info = new ProcessStartInfo { UseShellExecute = false };
			if (string.Equals(profile.CommandName, "Project", StringComparison.OrdinalIgnoreCase)) {
				info.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
				info.WorkingDirectory = ResolveDirectory(projectDirectory, profile.WorkingDirectory);
				info.ArgumentList.Add("run");
				if (noBuild) info.ArgumentList.Add("--no-build");
				info.ArgumentList.Add("--project");
				info.ArgumentList.Add(projectFile);
				if (!string.IsNullOrWhiteSpace(profile.CommandLineArgs)) {
					info.ArgumentList.Add("--");
					foreach (var argument in SplitArguments(profile.CommandLineArgs)) info.ArgumentList.Add(argument);
				}
			} else if (string.Equals(profile.CommandName, "Executable", StringComparison.OrdinalIgnoreCase)) {
				if (string.IsNullOrWhiteSpace(profile.ExecutablePath)) throw new InvalidOperationException($"Launch profile '{profile.Name}' has no executablePath.");
				info.FileName = ResolvePath(projectDirectory, profile.ExecutablePath);
				info.WorkingDirectory = ResolveDirectory(projectDirectory, profile.WorkingDirectory);
				foreach (var argument in SplitArguments(profile.CommandLineArgs)) info.ArgumentList.Add(argument);
			} else {
				throw new NotSupportedException($"Launch profile commandName '{profile.CommandName}' is not supported. Use Project or Executable.");
			}
			foreach (var variable in profile.EnvironmentVariables) info.Environment[variable.Key] = variable.Value;
			if (!info.Environment.ContainsKey("ASPNETCORE_URLS") && !string.IsNullOrWhiteSpace(profile.ApplicationUrl)) info.Environment["ASPNETCORE_URLS"] = profile.ApplicationUrl;
			return info;
		}

		// launchSettings uses command-line text, not an argv array. This covers the quoting rules
		// used by the SDK templates without invoking a shell.
		static string[] SplitArguments(string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
			var result = new System.Collections.Generic.List<string>();
			var current = new System.Text.StringBuilder();
			var quoted = false;
			for (var i = 0; i < text.Length; i++) {
				var c = text[i];
				if (c == '"') { quoted = !quoted; continue; }
				if (char.IsWhiteSpace(c) && !quoted) { if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); } continue; }
				current.Append(c);
			}
			if (quoted) throw new FormatException("Unterminated quote in commandLineArgs.");
			if (current.Length > 0) result.Add(current.ToString());
			return result.ToArray();
		}

		static string ResolvePath(string baseDirectory, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
		static string ResolveDirectory(string projectDirectory, string configured) => string.IsNullOrWhiteSpace(configured) ? projectDirectory : ResolvePath(projectDirectory, configured);
	}
}
