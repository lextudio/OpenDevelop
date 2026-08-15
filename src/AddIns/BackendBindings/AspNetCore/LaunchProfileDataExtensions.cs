using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace MonoDevelop.AspNetCore
{
	internal static class LaunchProfileDataExtensions
	{
		public static T TryGetOtherSettings<T>(this LaunchProfileData profile, string name)
		{
			if (profile.OtherSettings == null || !profile.OtherSettings.TryGetValue(name, out var value) || value == null)
				return default;
			if (value is T typed)
				return typed;
			if (value is JsonValue json && json.TryGetValue<T>(out var converted))
				return converted;
			return default;
		}

		public static string TryGetApplicationUrl(this LaunchProfileData profile) =>
			profile.TryGetOtherSettings<string>("applicationUrl") ?? string.Empty;

		public static string GetFirstApplicationUrl(this string urls) =>
			string.IsNullOrEmpty(urls) || !urls.Contains(';') ? urls : urls.Split(';').FirstOrDefault();

		public static IDictionary<string, Dictionary<string, object>> ToSerializableForm(this IDictionary<string, LaunchProfileData> profiles)
		{
			var result = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
			foreach (var profile in profiles)
				result.Add(profile.Key, LaunchProfileData.ToSerializableForm(new LaunchProfile(profile.Value)));
			return result;
		}
	}
}
