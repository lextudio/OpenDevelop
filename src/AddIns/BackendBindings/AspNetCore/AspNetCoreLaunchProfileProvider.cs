using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MonoDevelop.AspNetCore;

namespace ICSharpCode.AspNetCore
{
	public sealed class AspNetCoreLaunchProfile
	{
		internal AspNetCoreLaunchProfile(string name, LaunchProfileData data)
		{
			Name = name;
			CommandName = data.CommandName ?? string.Empty;
			ExecutablePath = data.ExecutablePath ?? string.Empty;
			CommandLineArgs = data.CommandLineArgs ?? string.Empty;
			WorkingDirectory = data.WorkingDirectory ?? string.Empty;
			LaunchBrowser = data.LaunchBrowser == true;
			LaunchUrl = data.LaunchUrl ?? string.Empty;
			ApplicationUrl = data.TryGetApplicationUrl();
			InspectUri = ReadString(data.OtherSettings, "inspectUri");
			EnvironmentVariables = new ReadOnlyDictionary<string, string>(
				new Dictionary<string, string>(data.EnvironmentVariables ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
		}

		public string Name { get; }
		public string CommandName { get; }
		public string ExecutablePath { get; }
		public string CommandLineArgs { get; }
		public string WorkingDirectory { get; }
		public bool LaunchBrowser { get; }
		public string LaunchUrl { get; }
		public string ApplicationUrl { get; }
		public string InspectUri { get; }
		public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

		static string ReadString(IDictionary<string, object> settings, string name)
		{
			if (settings == null || !settings.TryGetValue(name, out var value) || value == null)
				return string.Empty;
			return value is JsonValue token && token.TryGetValue<string>(out var text) ? text : value.ToString() ?? string.Empty;
		}

		public string GetBrowserUrl()
		{
			if (Uri.TryCreate(LaunchUrl, UriKind.Absolute, out var absolute) && !absolute.IsFile)
				return absolute.AbsoluteUri;
			var baseUrl = ApplicationUrl.GetFirstApplicationUrl();
			if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.IsFile)
				return string.Empty;
			return Uri.TryCreate(baseUri, LaunchUrl ?? string.Empty, out var combined) ? combined.AbsoluteUri : string.Empty;
		}
	}

	/// <summary>IDE-neutral port of MonoDevelop.AspNetCore's LaunchProfileProvider.</summary>
	public sealed class AspNetCoreLaunchProfileProvider
	{
		const string DefaultGlobalSettings = "{\"windowsAuthentication\":false,\"anonymousAuthentication\":true}";
		readonly object fileLock = new();
		readonly string defaultProfileName;
		Dictionary<string, LaunchProfileData> profiles = new(StringComparer.Ordinal);
		Dictionary<string, JsonNode> globalSettings = new(StringComparer.Ordinal);

		public AspNetCoreLaunchProfileProvider(string projectBaseDirectory, string defaultNamespace)
		{
			if (string.IsNullOrWhiteSpace(projectBaseDirectory))
				throw new ArgumentException("A project base directory is required.", nameof(projectBaseDirectory));
			ProjectBaseDirectory = Path.GetFullPath(projectBaseDirectory);
			defaultProfileName = string.IsNullOrWhiteSpace(defaultNamespace) ? new DirectoryInfo(ProjectBaseDirectory).Name : defaultNamespace;
		}

		public string ProjectBaseDirectory { get; }
		public string LaunchSettingsJsonPath => Path.Combine(ProjectBaseDirectory, "Properties", "launchSettings.json");
		public IReadOnlyList<AspNetCoreLaunchProfile> Profiles => profiles.Select(p => new AspNetCoreLaunchProfile(p.Key, p.Value)).ToArray();

		public void LoadLaunchSettings(bool createIfMissing = false)
		{
			if (!File.Exists(LaunchSettingsJsonPath)) {
				globalSettings = new Dictionary<string, JsonNode>(StringComparer.Ordinal) { ["iisSettings"] = JsonNode.Parse(DefaultGlobalSettings) };
				profiles = new Dictionary<string, LaunchProfileData>(StringComparer.Ordinal) { [defaultProfileName] = CreateDefaultProfile(defaultProfileName) };
				if (createIfMissing)
					SaveLaunchSettings();
				return;
			}
			JsonObject document;
			try {
				document = JsonNode.Parse(File.ReadAllText(LaunchSettingsJsonPath)) as JsonObject
					?? throw new JsonException("The root value must be an object.");
			} catch (JsonException ex) {
				throw new InvalidDataException($"Invalid launch settings file '{LaunchSettingsJsonPath}'.", ex);
			}
			globalSettings = document.Where(p => p.Key != "profiles")
				.ToDictionary(p => p.Key, p => p.Value?.DeepClone(), StringComparer.Ordinal);
			profiles = LaunchProfileData.DeserializeProfiles(document["profiles"] as JsonObject);
		}

		public AspNetCoreLaunchProfile GetProfile(string preferredName = null)
		{
			if (profiles.Count == 0)
				LoadLaunchSettings();
			if (!string.IsNullOrEmpty(preferredName) && profiles.TryGetValue(preferredName, out var preferred) && IsRunnable(preferred))
				return new AspNetCoreLaunchProfile(preferredName, preferred);
			if (profiles.TryGetValue(defaultProfileName, out var @default) && IsRunnable(@default))
				return new AspNetCoreLaunchProfile(defaultProfileName, @default);
			var candidate = profiles.FirstOrDefault(p => IsRunnable(p.Value));
			return candidate.Value == null ? null : new AspNetCoreLaunchProfile(candidate.Key, candidate.Value);
		}

		public AspNetCoreLaunchProfile AddProjectProfile(string name, string applicationUrl = "http://localhost:5000")
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ArgumentException("A profile name is required.", nameof(name));
			var data = CreateDefaultProfile(name);
			data.OtherSettings["applicationUrl"] = applicationUrl;
			profiles[name] = data;
			return new AspNetCoreLaunchProfile(name, data);
		}

		public AspNetCoreLaunchProfile UpdateProfile(string name, string applicationUrl, string launchUrl, bool launchBrowser)
		{
			if (!profiles.TryGetValue(name, out var data))
				throw new KeyNotFoundException($"Launch profile '{name}' does not exist.");
			data.LaunchUrl = launchUrl ?? string.Empty;
			data.LaunchBrowser = launchBrowser;
			data.OtherSettings ??= new Dictionary<string, object>(StringComparer.Ordinal);
			if (string.IsNullOrWhiteSpace(applicationUrl))
				data.OtherSettings.Remove("applicationUrl");
			else
				data.OtherSettings["applicationUrl"] = applicationUrl;
			return new AspNetCoreLaunchProfile(name, data);
		}

		public void SaveLaunchSettings()
		{
			var document = new JsonObject();
			foreach (var setting in globalSettings)
				document.Add(setting.Key, setting.Value?.DeepClone());
			document.Add("profiles", JsonSerializer.SerializeToNode(profiles.ToSerializableForm()));
			Directory.CreateDirectory(Path.GetDirectoryName(LaunchSettingsJsonPath)!);
			lock (fileLock)
				File.WriteAllText(LaunchSettingsJsonPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		}

		static bool IsRunnable(LaunchProfileData profile) =>
			string.Equals(profile.CommandName, "Project", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(profile.CommandName, "Executable", StringComparison.OrdinalIgnoreCase);

		static LaunchProfileData CreateDefaultProfile(string name) => new() {
			Name = name,
			CommandName = "Project",
			LaunchBrowser = true,
			EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ASPNETCORE_ENVIRONMENT"] = "Development" },
			OtherSettings = new Dictionary<string, object>(StringComparer.Ordinal) { ["applicationUrl"] = "http://localhost:5000" }
		};
	}
}
