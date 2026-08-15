using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace MonoDevelop.AspNetCore
{
	/// <summary>System.Text.Json port of MonoDevelop's launch-profile data model.</summary>
	internal sealed class LaunchProfileData
	{
		static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal) {
			"commandName", "executablePath", "commandLineArgs", "workingDirectory",
			"launchBrowser", "launchUrl", "environmentVariables"
		};

		public string Name { get; set; }
		public bool InMemoryProfile { get; set; }
		public string CommandName { get; set; }
		public string ExecutablePath { get; set; }
		public string CommandLineArgs { get; set; }
		public string WorkingDirectory { get; set; }
		public bool? LaunchBrowser { get; set; }
		public string LaunchUrl { get; set; }
		public IDictionary<string, string> EnvironmentVariables { get; set; }
		public IDictionary<string, object> OtherSettings { get; set; }

		public static Dictionary<string, LaunchProfileData> DeserializeProfiles(JsonObject profilesObject)
		{
			var profiles = new Dictionary<string, LaunchProfileData>(StringComparer.Ordinal);
			if (profilesObject == null)
				return profiles;
			foreach (var profileProperty in profilesObject) {
				if (profileProperty.Value is not JsonObject profileObject)
					continue;
				var data = new LaunchProfileData {
					Name = profileProperty.Key,
					CommandName = ReadString(profileObject, "commandName"),
					ExecutablePath = ReadString(profileObject, "executablePath"),
					CommandLineArgs = ReadString(profileObject, "commandLineArgs"),
					WorkingDirectory = ReadString(profileObject, "workingDirectory"),
					LaunchBrowser = ReadBoolean(profileObject, "launchBrowser"),
					LaunchUrl = ReadString(profileObject, "launchUrl"),
					EnvironmentVariables = ReadEnvironment(profileObject["environmentVariables"] as JsonObject)
				};
				var custom = new Dictionary<string, object>(StringComparer.Ordinal);
				foreach (var setting in profileObject) {
					if (!KnownProperties.Contains(setting.Key))
						custom[setting.Key] = setting.Value?.DeepClone();
				}
				if (custom.Count > 0)
					data.OtherSettings = custom;
				profiles.Add(profileProperty.Key, data);
			}
			return profiles;
		}

		internal static Dictionary<string, object> ToSerializableForm(ILaunchProfile profile)
		{
			var data = new Dictionary<string, object>(StringComparer.Ordinal);
			AddString(data, "commandName", profile.CommandName);
			AddString(data, "executablePath", profile.ExecutablePath);
			AddString(data, "commandLineArgs", profile.CommandLineArgs);
			AddString(data, "workingDirectory", profile.WorkingDirectory);
			if (profile.LaunchBrowser) data["launchBrowser"] = true;
			AddString(data, "launchUrl", profile.LaunchUrl);
			if (profile.EnvironmentVariables != null) data["environmentVariables"] = profile.EnvironmentVariables;
			if (profile.OtherSettings != null)
				foreach (var setting in profile.OtherSettings) data[setting.Key] = setting.Value;
			return data;
		}

		static string ReadString(JsonObject value, string name) =>
			value[name] is JsonValue json && json.TryGetValue<string>(out var result) ? result : null;
		static bool? ReadBoolean(JsonObject value, string name) =>
			value[name] is JsonValue json && json.TryGetValue<bool>(out var result) ? result : null;
		static Dictionary<string, string> ReadEnvironment(JsonObject value)
		{
			if (value == null) return null;
			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var item in value)
				if (item.Value is JsonValue json && json.TryGetValue<string>(out var text)) result[item.Key] = text;
			return result;
		}
		static void AddString(Dictionary<string, object> data, string name, string value)
		{
			if (!string.IsNullOrEmpty(value)) data[name] = value;
		}
	}
}
