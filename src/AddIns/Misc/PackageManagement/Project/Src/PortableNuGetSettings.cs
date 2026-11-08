using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using NuGet;

namespace ICSharpCode.PackageManagement
{
	public sealed class PortableNuGetSettings : ISettings
	{
		readonly string fileName;
		readonly string solutionDirectory;

		public PortableNuGetSettings(string directory)
		{
			solutionDirectory = directory;
			if (String.IsNullOrEmpty(directory)) {
				directory = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
					".nuget",
					"NuGet");
			}
			// NuGet accepts three spellings; edit the one the solution already has (a second spelling next to
			// it would be ambiguous on a case-sensitive file system).
			fileName = new[] { "NuGet.Config", "nuget.config", "NuGet.config" }
				.Select(name => Path.Combine(directory, name))
				.FirstOrDefault(File.Exists) ?? Path.Combine(directory, "NuGet.Config");
		}

		public string GetValue(string section, string key, bool isPath)
		{
			SettingValue value = GetValues(section, isPath).FirstOrDefault(item => item.Key == key);
			return value != null ? value.Value : null;
		}

		/// <summary>
		/// What NuGet itself would use for this solution: the solution's directory and the ones above it, the
		/// user and the machine file, merged by NuGet.Configuration. Only the file next to the solution is
		/// ever written (see <see cref="SetValues"/>).
		/// </summary>
		public IList<SettingValue> GetValues(string section, bool isPath)
		{
			try {
				return NuGetConfigHierarchy.Read(solutionDirectory, section, isPath)
					.Where(entry => !String.IsNullOrEmpty(entry.Key))
					.Select(entry => new SettingValue(entry.Key, entry.Value, isPath))
					.ToList();
			} catch (Exception ex) {
				// A malformed file somewhere up the chain must not hide the solution's own sources.
				ICSharpCode.Core.LoggingService.Warn("Could not read the NuGet configuration hierarchy; using the solution's NuGet.Config only.", ex);
				return GetValuesFromSolutionFile(section, isPath);
			}
		}

		IList<SettingValue> GetValuesFromSolutionFile(string section, bool isPath)
		{
			XElement sectionElement = GetSection(section);
			if (sectionElement == null)
				return new List<SettingValue>();

			return sectionElement
				.Elements("add")
				.Select(element => new SettingValue(
					(string)element.Attribute("key"),
					(string)element.Attribute("value"),
					isPath))
				.Where(value => !String.IsNullOrEmpty(value.Key))
				.ToList();
		}

		public IList<SettingValue> GetNestedValues(string section, string key)
		{
			XElement sectionElement = GetSection(section);
			XElement keyElement = sectionElement != null
				? sectionElement.Elements().FirstOrDefault(element => (string)element.Attribute("key") == key || element.Name.LocalName == key)
				: null;
			if (keyElement == null)
				return new List<SettingValue>();

			return keyElement
				.Elements("add")
				.Select(element => new SettingValue((string)element.Attribute("key"), (string)element.Attribute("value"), false))
				.Where(value => !String.IsNullOrEmpty(value.Key))
				.ToList();
		}

		public void SetValue(string section, string key, string value)
		{
			XDocument document = LoadOrCreate();
			XElement sectionElement = GetOrCreateSection(document, section);
			XElement existing = sectionElement.Elements("add").FirstOrDefault(element => (string)element.Attribute("key") == key);
			if (existing == null) {
				sectionElement.Add(new XElement("add", new XAttribute("key", key), new XAttribute("value", value ?? String.Empty)));
			} else {
				existing.SetAttributeValue("value", value ?? String.Empty);
			}
			Save(document);
		}

		/// <summary>
		/// The package sources the legacy provider hands back are the merged list, inherited ones included.
		/// For the two source sections only the entries no other configuration file provides are written
		/// to the solution's own file (and its <c>&lt;clear/&gt;</c> is kept), so a user's private feeds are
		/// not copied into it. A source inherited from another file can therefore not be removed from here;
		/// that is edited in the file that defines it.
		/// </summary>
		public void SetValues(string section, IList<SettingValue> values)
		{
			if (values != null && (section == "packageSources" || section == "disabledPackageSources")) {
				try {
					NuGetConfigHierarchy.WriteSection(
						solutionDirectory, fileName, section, isPath: section == "packageSources",
						values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));
					return;
				} catch (Exception ex) when (!(ex is System.IO.IOException || ex is UnauthorizedAccessException)) {
					ICSharpCode.Core.LoggingService.Warn("Could not read the NuGet configuration hierarchy; writing every source to the solution's NuGet.Config.", ex);
				}
			}
			XDocument document = LoadOrCreate();
			XElement sectionElement = GetOrCreateSection(document, section);
			sectionElement.RemoveNodes();
			foreach (SettingValue value in values ?? Array.Empty<SettingValue>()) {
				sectionElement.Add(new XElement("add",
					new XAttribute("key", value.Key),
					new XAttribute("value", value.Value ?? String.Empty)));
			}
			Save(document);
		}

		public void SetNestedValues(string section, string key, IList<KeyValuePair<string, string>> values)
		{
			XDocument document = LoadOrCreate();
			XElement sectionElement = GetOrCreateSection(document, section);
			XElement keyElement = sectionElement.Elements().FirstOrDefault(element => (string)element.Attribute("key") == key || element.Name.LocalName == key);
			if (keyElement == null) {
				keyElement = new XElement(key, new XAttribute("key", key));
				sectionElement.Add(keyElement);
			}
			keyElement.RemoveNodes();
			foreach (var value in values ?? Array.Empty<KeyValuePair<string, string>>()) {
				keyElement.Add(new XElement("add",
					new XAttribute("key", value.Key),
					new XAttribute("value", value.Value ?? String.Empty)));
			}
			Save(document);
		}

		public bool DeleteValue(string section, string key)
		{
			XDocument document = LoadOrCreate();
			XElement sectionElement = GetOrCreateSection(document, section);
			XElement existing = sectionElement.Elements("add").FirstOrDefault(element => (string)element.Attribute("key") == key);
			if (existing == null)
				return false;
			existing.Remove();
			Save(document);
			return true;
		}

		public bool DeleteSection(string section)
		{
			XDocument document = LoadOrCreate();
			XElement sectionElement = GetSection(document, section);
			if (sectionElement == null)
				return false;
			sectionElement.Remove();
			Save(document);
			return true;
		}

		public void UpdateSections(string section, IList<SettingValue> values)
		{
			SetValues(section, values);
		}

		XElement GetSection(string section)
		{
			return GetSection(LoadOrCreate(), section);
		}

		static XElement GetSection(XDocument document, string section)
		{
			return document.Root != null ? document.Root.Element(section) : null;
		}

		static XElement GetOrCreateSection(XDocument document, string section)
		{
			if (document.Root == null)
				document.Add(new XElement("configuration"));
			XElement sectionElement = document.Root.Element(section);
			if (sectionElement == null) {
				sectionElement = new XElement(section);
				document.Root.Add(sectionElement);
			}
			return sectionElement;
		}

		XDocument LoadOrCreate()
		{
			if (!File.Exists(fileName))
				return new XDocument(new XElement("configuration"));
			return XDocument.Load(fileName);
		}

		void Save(XDocument document)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(fileName));
			document.Save(fileName);
		}
	}
}
