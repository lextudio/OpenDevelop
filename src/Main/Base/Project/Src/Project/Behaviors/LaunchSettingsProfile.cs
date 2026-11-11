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
		/// <summary>The per-project preference naming the profile Run and Debug use.</summary>
		public const string SelectedProfilePreferenceKey = "LaunchProfile";

		public string Name { get; private set; }
		public string CommandLineArgs { get; private set; }
		public string WorkingDirectory { get; private set; }
		public string ApplicationUrl { get; private set; }
		/// <summary>
		/// Uno's <c>compatibleTargetFramework</c>: the platform the profile belongs to (<c>desktop</c>;
		/// this IDE also writes <c>ios</c> and <c>android</c>). Empty means any target framework.
		/// </summary>
		public string CompatibleTargetFramework { get; private set; }
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
			var profiles = LoadAll(projectDirectory);
			return profiles.Count > 0 ? profiles[0] : null;
		}

		/// <summary>
		/// The profile named <paramref name="selectedName"/> when the file has a <c>Project</c> profile
		/// of that name, else what <see cref="Load(string)"/> picks (a stale selection must not stop a start).
		/// </summary>
		public static LaunchSettingsProfile Load(string projectDirectory, string selectedName)
		{
			var profiles = LoadAll(projectDirectory);
			if (!string.IsNullOrEmpty(selectedName)) {
				foreach (var profile in profiles) {
					if (string.Equals(profile.Name, selectedName, StringComparison.Ordinal))
						return profile;
				}
			}
			return profiles.Count > 0 ? profiles[0] : null;
		}

		/// <summary>The profile the project's preferences select (<see cref="SelectedProfilePreferenceKey"/>), as <see cref="Load(string, string)"/>.</summary>
		public static LaunchSettingsProfile LoadSelected(string projectDirectory, ICSharpCode.Core.Properties preferences)
		{
			return Load(projectDirectory, preferences?.Get(SelectedProfilePreferenceKey, string.Empty));
		}

		/// <summary>
		/// Every profile whose <c>commandName</c> is <c>Project</c>, in file order; other kinds
		/// (<c>WSL2</c>, <c>IISExpress</c>, ...) are not something this IDE starts and are left out.
		/// </summary>
		public static IReadOnlyList<LaunchSettingsProfile> LoadAll(string projectDirectory)
		{
			var result = new List<LaunchSettingsProfile>();
			string path = GetPath(projectDirectory);
			if (!File.Exists(path))
				return result;
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
					return result;
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
					result.Add(new LaunchSettingsProfile {
						Name = profile.Name,
						CommandLineArgs = ReadString(profile.Value, "commandLineArgs"),
						WorkingDirectory = ReadString(profile.Value, "workingDirectory"),
						ApplicationUrl = ReadString(profile.Value, "applicationUrl"),
						CompatibleTargetFramework = ReadString(profile.Value, "compatibleTargetFramework"),
						EnvironmentVariables = variables
					});
				}
			}
			return result;
		}

		/// <summary>
		/// Whether the profile belongs to <paramref name="targetFramework"/> (<c>net10.0-ios</c>): its
		/// <see cref="CompatibleTargetFramework"/> names that framework's platform. A profile without one
		/// fits every framework.
		/// </summary>
		public bool IsCompatibleWith(string targetFramework)
		{
			if (string.IsNullOrWhiteSpace(CompatibleTargetFramework))
				return true;
			return string.Equals(CompatibleTargetFramework.Trim(), GetPlatform(targetFramework), StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>The platform of a target framework moniker: <c>net10.0-android35.0</c> gives
		/// <c>android</c>, <c>net10.0-windows10.0.19041.0</c> gives <c>windows</c>, <c>net10.0</c> gives "".</summary>
		public static string GetPlatform(string targetFramework)
		{
			if (string.IsNullOrEmpty(targetFramework))
				return string.Empty;
			int dash = targetFramework.IndexOf('-');
			if (dash < 0)
				return string.Empty;
			string platform = targetFramework.Substring(dash + 1);
			int end = 0;
			while (end < platform.Length && char.IsLetter(platform[end]))
				end++;
			return platform.Substring(0, end);
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
