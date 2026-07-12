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

		public PortableNuGetSettings(string directory)
		{
			if (String.IsNullOrEmpty(directory)) {
				directory = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
					".nuget",
					"NuGet");
			}
			fileName = Path.Combine(directory, "NuGet.Config");
		}

		public string GetValue(string section, string key, bool isPath)
		{
			SettingValue value = GetValues(section, isPath).FirstOrDefault(item => item.Key == key);
			return value != null ? value.Value : null;
		}

		public IList<SettingValue> GetValues(string section, bool isPath)
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

		public void SetValues(string section, IList<SettingValue> values)
		{
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
