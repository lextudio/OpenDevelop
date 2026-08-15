using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ICSharpCode.AspNetCore
{
	/// <summary>
	/// Resolves the standalone Blazor WebAssembly development server from NuGet's assets file.
	/// Restore and build are intentionally left to the IDE's normal build-before-run pipeline.
	/// </summary>
	public static class BlazorWebAssemblyDevServer
	{
		const string PackageId = "microsoft.aspnetcore.components.webassembly.devserver";

		public static bool TryCreate(string projectFileName, string applicationPath, AspNetCoreLaunchProfile profile, out ProcessStartInfo startInfo)
		{
			startInfo = null;
			if (string.IsNullOrWhiteSpace(projectFileName) || string.IsNullOrWhiteSpace(applicationPath))
				return false;

			var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFileName))!;
			var assetsFile = Path.Combine(projectDirectory, "obj", "project.assets.json");
			if (!File.Exists(assetsFile))
				return false;

			var server = ResolveServer(assetsFile);
			if (server == null)
				return false;

			if (!File.Exists(applicationPath))
				throw new InvalidOperationException($"The Blazor WebAssembly application has not been built: '{applicationPath}'.");

			var info = new ProcessStartInfo {
				FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet",
				WorkingDirectory = projectDirectory,
				UseShellExecute = false
			};
			info.ArgumentList.Add(server);
			info.ArgumentList.Add("--applicationpath");
			info.ArgumentList.Add(Path.GetFullPath(applicationPath));
			foreach (var variable in profile.EnvironmentVariables)
				info.Environment[variable.Key] = variable.Value;
			if (!info.Environment.ContainsKey("ASPNETCORE_URLS") && !string.IsNullOrWhiteSpace(profile.ApplicationUrl))
				info.Environment["ASPNETCORE_URLS"] = profile.ApplicationUrl;
			startInfo = info;
			return true;
		}

		public static string ResolveServer(string assetsFile)
		{
			using var document = JsonDocument.Parse(File.ReadAllText(assetsFile));
			var root = document.RootElement;
			if (!root.TryGetProperty("libraries", out var libraries) || !root.TryGetProperty("packageFolders", out var packageFolders))
				return null;

			var library = libraries.EnumerateObject().FirstOrDefault(p =>
				p.Name.StartsWith(PackageId + "/", StringComparison.OrdinalIgnoreCase));
			if (library.Equals(default(JsonProperty)) || !library.Value.TryGetProperty("path", out var pathValue))
				return null;

			var relativePackagePath = pathValue.GetString();
			if (string.IsNullOrWhiteSpace(relativePackagePath))
				return null;
			foreach (var folder in packageFolders.EnumerateObject()) {
				var candidate = Path.Combine(folder.Name, relativePackagePath.Replace('/', Path.DirectorySeparatorChar), "tools", "blazor-devserver.dll");
				if (File.Exists(candidate))
					return Path.GetFullPath(candidate);
			}
			return null;
		}
	}
}
