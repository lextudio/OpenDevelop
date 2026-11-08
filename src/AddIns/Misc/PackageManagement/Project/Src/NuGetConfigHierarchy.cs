// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using NuGet.Common;
using NuGet.Configuration;

namespace ICSharpCode.PackageManagement
{
	/// <summary>One <c>&lt;add key= value=&gt;</c> of a NuGet configuration section after NuGet merged its hierarchy.</summary>
	public sealed class ConfigEntry
	{
		public ConfigEntry(string key, string value, string? configFile)
		{
			Key = key;
			Value = value;
			ConfigFile = configFile;
		}

		public string Key { get; }

		/// <summary>The value as written; relative paths are resolved against <see cref="ConfigFile"/> when asked for as a path.</summary>
		public string Value { get; }

		/// <summary>The configuration file the entry comes from.</summary>
		public string? ConfigFile { get; }
	}

	/// <summary>
	/// NuGet's own definition of which configuration applies to a solution: its directory and every directory
	/// above it, then the user and machine files, merged by NuGet.Configuration (<c>&lt;clear/&gt;</c>, nearer
	/// files win). The package sources the add-in shows, and the ones <c>dotnet restore</c> uses, are read
	/// from this, not from a single file.
	/// </summary>
	public static class NuGetConfigHierarchy
	{
		/// <summary>
		/// The entries of <paramref name="section"/> that apply to <paramref name="solutionDirectory"/>
		/// (null: the user and machine configuration only), nearest first as NuGet merges them.
		/// </summary>
		public static IReadOnlyList<ConfigEntry> Read(string? solutionDirectory, string section, bool isPath)
		{
			ISettings settings = Settings.LoadDefaultSettings(string.IsNullOrEmpty(solutionDirectory) ? null : solutionDirectory);
			SettingSection? loaded = settings.GetSection(section);
			if (loaded == null) {
				return Array.Empty<ConfigEntry>();
			}
			var entries = new List<ConfigEntry>();
			foreach (AddItem item in loaded.Items.OfType<AddItem>()) {
				string value = isPath ? item.GetValueAsPath() : item.Value;
				entries.Add(new ConfigEntry(item.Key, value, item.ConfigPath));
			}
			return entries;
		}

		/// <summary>
		/// Of <paramref name="wanted"/> (what the user's edits leave in a section), the entries to write to
		/// the solution's own configuration file: those that no other file already provides. Writing the
		/// inherited ones too would copy the user's private feeds into the solution's file.
		/// </summary>
		public static IReadOnlyList<KeyValuePair<string, string>> OwnEntries(
			string solutionConfigFile, IEnumerable<ConfigEntry> effective, IEnumerable<KeyValuePair<string, string>> wanted)
		{
			StringComparer paths = PathUtility.GetStringComparerBasedOnOS();
			string own = Path.GetFullPath(solutionConfigFile);
			List<ConfigEntry> inherited = effective
				.Where(entry => entry.ConfigFile == null || !paths.Equals(Path.GetFullPath(entry.ConfigFile), own))
				.ToList();
			return wanted
				.Where(pair => !inherited.Any(entry =>
					string.Equals(entry.Key, pair.Key, StringComparison.OrdinalIgnoreCase)
					&& ValuesEqual(entry.Value, pair.Value, paths)))
				.ToList();
		}

		/// <summary>A value as NuGet resolves a path value: URLs unchanged, other paths against the file's directory.</summary>
		static string ResolveAgainst(string value, string configDirectory)
		{
			bool isUrl = Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
			return isUrl ? value : Path.GetFullPath(value, configDirectory);
		}

		/// <summary>URLs compare ignoring case; local paths the way the file system of this OS does (NuGet's rule).</summary>
		static bool ValuesEqual(string a, string b, StringComparer paths)
		{
			bool isUrl = Uri.TryCreate(a, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
			return isUrl ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase) : paths.Equals(a, b);
		}

		/// <summary>
		/// Replaces the <c>add</c> entries of <paramref name="section"/> in <paramref name="solutionConfigFile"/>
		/// with those of <paramref name="wanted"/> that no other file already provides, creating the file if
		/// needed. Everything else in the section is kept, above all <c>&lt;clear/&gt;</c> (which hides the user's
		/// and the machine's sources) and <c>&lt;remove&gt;</c>: dropping them would silently re-enable
		/// feeds the solution had switched off.
		/// </summary>
		public static void WriteSection(
			string? solutionDirectory, string solutionConfigFile, string section, bool isPath, IEnumerable<KeyValuePair<string, string>> wanted)
		{
			IReadOnlyList<KeyValuePair<string, string>> own = OwnEntries(solutionConfigFile, Read(solutionDirectory, section, isPath), wanted);
			XDocument document = File.Exists(solutionConfigFile)
				? XDocument.Load(solutionConfigFile)
				: new XDocument(new XElement("configuration"));
			XElement root = document.Root ?? throw new InvalidDataException(solutionConfigFile + " has no root element.");
			XElement? element = root.Element(section);
			if (element == null) {
				element = new XElement(section);
				root.Add(element);
			}
			// What the file says now, so an entry that did not change keeps the text its author wrote (a
			// relative feed path stays relative instead of becoming this machine's absolute path).
			var written = element.Elements("add")
				.Where(add => add.Attribute("key") != null && add.Attribute("value") != null)
				.GroupBy(add => (string)add.Attribute("key")!, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(group => group.Key, group => (string)group.First().Attribute("value")!, StringComparer.OrdinalIgnoreCase);
			string? configDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionConfigFile));
			StringComparer paths = PathUtility.GetStringComparerBasedOnOS();
			element.Elements("add").Remove();
			foreach (KeyValuePair<string, string> pair in own) {
				string value = pair.Value;
				if (isPath && written.TryGetValue(pair.Key, out string? previous) && configDirectory != null
					&& paths.Equals(ResolveAgainst(previous, configDirectory), value)) {
					value = previous;
				}
				element.Add(new XElement("add", new XAttribute("key", pair.Key), new XAttribute("value", value)));
			}
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(solutionConfigFile))!);
			document.Save(solutionConfigFile);
		}
	}
}
