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
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// The <c>Properties/launchSettings.json</c> profile that <c>dotnet run</c> uses for a project:
	/// the first profile whose <c>commandName</c> is <c>Project</c>. Starting a project from the IDE
	/// applies it the same way, so a program that needs, say, <c>DYLD_LIBRARY_PATH</c> from its
	/// profile (a GTK app on macOS) starts from the IDE exactly as it does from <c>dotnet run</c>.
	/// </summary>
	public sealed class LaunchSettingsProfile
	{
		public string Name { get; private set; }
		public string CommandLineArgs { get; private set; }
		public string WorkingDirectory { get; private set; }
		public string ApplicationUrl { get; private set; }
		public IReadOnlyList<KeyValuePair<string, string>> EnvironmentVariables { get; private set; }

		public static string GetPath(string projectDirectory)
		{
			return Path.Combine(projectDirectory, "Properties", "launchSettings.json");
		}

		/// <summary>
		/// Reads the profile <c>dotnet run</c> would pick, or null when the project has no
		/// launchSettings.json or no <c>Project</c> profile in it. A file that is not valid JSON is
		/// an error, as it is for <c>dotnet run</c>.
		/// </summary>
		public static LaunchSettingsProfile Load(string projectDirectory)
		{
			string path = GetPath(projectDirectory);
			if (!File.Exists(path))
				return null;
			JsonDocument document;
			try {
				document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions {
					CommentHandling = JsonCommentHandling.Skip,
					AllowTrailingCommas = true
				});
			} catch (JsonException ex) {
				throw new ProjectStartException("The launch settings file " + path + " is not valid JSON: " + ex.Message);
			}
			using (document) {
				if (document.RootElement.ValueKind != JsonValueKind.Object
				    || !document.RootElement.TryGetProperty("profiles", out var profiles)
				    || profiles.ValueKind != JsonValueKind.Object)
					return null;
				foreach (var profile in profiles.EnumerateObject()) {
					if (profile.Value.ValueKind != JsonValueKind.Object)
						continue;
					if (!string.Equals(ReadString(profile.Value, "commandName"), "Project", StringComparison.OrdinalIgnoreCase))
						continue;
					var variables = new List<KeyValuePair<string, string>>();
					if (profile.Value.TryGetProperty("environmentVariables", out var env) && env.ValueKind == JsonValueKind.Object) {
						foreach (var variable in env.EnumerateObject()) {
							if (variable.Value.ValueKind == JsonValueKind.String)
								variables.Add(new KeyValuePair<string, string>(variable.Name, variable.Value.GetString()));
						}
					}
					return new LaunchSettingsProfile {
						Name = profile.Name,
						CommandLineArgs = ReadString(profile.Value, "commandLineArgs"),
						WorkingDirectory = ReadString(profile.Value, "workingDirectory"),
						ApplicationUrl = ReadString(profile.Value, "applicationUrl"),
						EnvironmentVariables = variables
					};
				}
			}
			return null;
		}

		/// <summary>
		/// Applies the profile like <c>dotnet run</c>: environment variables (with %VAR% references
		/// expanded), <c>applicationUrl</c> as ASPNETCORE_URLS when the profile does not set it,
		/// DOTNET_LAUNCH_PROFILE, and the profile's arguments and working directory only where the
		/// project options do not already give them (an explicit choice wins, as command-line
		/// arguments do over the profile's for <c>dotnet run</c>).
		/// </summary>
		public void ApplyTo(ProcessStartInfo psi, string projectDirectory, bool argumentsConfigured, bool workingDirectoryConfigured)
		{
			if (psi == null)
				throw new ArgumentNullException(nameof(psi));
			foreach (var variable in EnvironmentVariables)
				psi.Environment[variable.Key] = Environment.ExpandEnvironmentVariables(variable.Value ?? string.Empty);
			if (!string.IsNullOrWhiteSpace(ApplicationUrl) && !HasProfileVariable("ASPNETCORE_URLS"))
				psi.Environment["ASPNETCORE_URLS"] = ApplicationUrl;
			psi.Environment["DOTNET_LAUNCH_PROFILE"] = Name;
			if (!argumentsConfigured && !string.IsNullOrWhiteSpace(CommandLineArgs))
				psi.Arguments = Environment.ExpandEnvironmentVariables(CommandLineArgs);
			if (!workingDirectoryConfigured && !string.IsNullOrWhiteSpace(WorkingDirectory)) {
				string directory = Environment.ExpandEnvironmentVariables(WorkingDirectory);
				directory = Path.GetFullPath(Path.IsPathRooted(directory) ? directory : Path.Combine(projectDirectory, directory));
				if (!Directory.Exists(directory))
					throw new ProjectStartException("Working directory " + directory + " from launch profile '" + Name + "' does not exist; the process cannot be started.");
				psi.WorkingDirectory = directory;
			}
		}

		bool HasProfileVariable(string name)
		{
			foreach (var variable in EnvironmentVariables) {
				if (string.Equals(variable.Key, name, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		static string ReadString(JsonElement element, string name)
		{
			return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
				? value.GetString() ?? string.Empty
				: string.Empty;
		}
	}
}
