using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ICSharpCode.AspNetCore
{
	public sealed class AspNetCorePublishProfile
	{
		public string FilePath { get; init; } = string.Empty;
		public string Name { get; init; } = string.Empty;
		public string PublishDirectory { get; init; } = string.Empty;
		public string Configuration { get; init; } = string.Empty;
		public string TargetFramework { get; init; } = string.Empty;
		public string RuntimeIdentifier { get; init; } = string.Empty;
		public bool SelfContained { get; init; }
		public bool DeleteExistingFiles { get; init; }

		public static AspNetCorePublishProfile Load(string fileName)
		{
			if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("A publish profile path is required.", nameof(fileName));
			var document = XDocument.Load(fileName, LoadOptions.SetLineInfo);
			var properties = document.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup")
				.GroupBy(e => e.Name.LocalName, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.Last().Value.Trim(), StringComparer.OrdinalIgnoreCase);
			string Get(params string[] names) => names.Select(n => properties.TryGetValue(n, out var value) ? value : null).FirstOrDefault(v => v != null) ?? string.Empty;
			bool GetBool(string name) => bool.TryParse(Get(name), out var value) && value;
			return new AspNetCorePublishProfile {
				FilePath = Path.GetFullPath(fileName),
				Name = Path.GetFileNameWithoutExtension(fileName),
				PublishDirectory = Get("PublishDir", "PublishUrl", "publishUrl"),
				Configuration = Get("Configuration", "LastUsedBuildConfiguration"),
				TargetFramework = Get("TargetFramework"),
				RuntimeIdentifier = Get("RuntimeIdentifier"),
				SelfContained = GetBool("SelfContained"),
				DeleteExistingFiles = GetBool("DeleteExistingFiles")
			};
		}

		public void Save()
		{
			if (string.IsNullOrWhiteSpace(FilePath)) throw new InvalidOperationException("This is a temporary publish profile and has no file path.");
			var document = XDocument.Load(FilePath, LoadOptions.PreserveWhitespace);
			var root = document.Root ?? throw new InvalidDataException("The publish profile has no Project root element.");
			var group = root.Elements().FirstOrDefault(e => e.Name.LocalName == "PropertyGroup");
			if (group == null) { group = new XElement(root.Name.Namespace + "PropertyGroup"); root.Add(group); }
			Set(group, "LastUsedBuildConfiguration", Configuration);
			Set(group, "PublishUrl", PublishDirectory);
			Set(group, "TargetFramework", TargetFramework);
			Set(group, "RuntimeIdentifier", RuntimeIdentifier);
			Set(group, "SelfContained", SelfContained ? "true" : "false");
			Set(group, "DeleteExistingFiles", DeleteExistingFiles ? "true" : "false");
			document.Save(FilePath);
		}

		static void Set(XElement group, string name, string value)
		{
			var element = group.Elements().LastOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
			if (string.IsNullOrWhiteSpace(value)) { element?.Remove(); return; }
			if (element == null) group.Add(new XElement(group.Name.Namespace + name, value)); else element.Value = value;
		}
	}

	public static class AspNetCorePublishCommand
	{
		public static string GetOutputDirectory(string projectFileName, AspNetCorePublishProfile profile)
		{
			if (profile == null) throw new ArgumentNullException(nameof(profile));
			if (string.IsNullOrWhiteSpace(profile.PublishDirectory)) throw new InvalidDataException("The publish profile does not define PublishDir or PublishUrl.");
			return Path.GetFullPath(profile.PublishDirectory, Path.GetDirectoryName(Path.GetFullPath(projectFileName))!);
		}

		public static IReadOnlyList<AspNetCorePublishProfile> LoadProfiles(string projectDirectory)
		{
			var directory = Path.Combine(projectDirectory, "Properties", "PublishProfiles");
			if (!Directory.Exists(directory)) return Array.Empty<AspNetCorePublishProfile>();
			return Directory.EnumerateFiles(directory, "*.pubxml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
				.Select(AspNetCorePublishProfile.Load).ToArray();
		}

		public static ProcessStartInfo Create(string projectFileName, AspNetCorePublishProfile profile)
		{
			var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFileName))!;
			var output = GetOutputDirectory(projectFileName, profile);
			var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, WorkingDirectory = projectDirectory };
			Add(info, "publish", Path.GetFullPath(projectFileName));
			if (!string.IsNullOrWhiteSpace(profile.Configuration)) Add(info, "--configuration", profile.Configuration);
			if (!string.IsNullOrWhiteSpace(profile.TargetFramework)) Add(info, "--framework", profile.TargetFramework);
			if (!string.IsNullOrWhiteSpace(profile.RuntimeIdentifier)) Add(info, "--runtime", profile.RuntimeIdentifier);
			if (profile.SelfContained) Add(info, "--self-contained", "true");
			Add(info, "--output", output);
			return info;
		}

		static void Add(ProcessStartInfo info, params string[] values) { foreach (var value in values) info.ArgumentList.Add(value); }
	}
}
