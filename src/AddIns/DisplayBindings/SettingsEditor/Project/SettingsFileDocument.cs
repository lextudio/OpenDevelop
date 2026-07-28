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
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ICSharpCode.SettingsEditor;

public sealed class SettingsFileDocument
{
	public const string XmlNamespace = "http://schemas.microsoft.com/VisualStudio/2004/01/settings";

	public string GeneratedClassNamespace { get; set; } = string.Empty;
	public string GeneratedClassName { get; set; } = string.Empty;
	public bool UseMySettingsClassName { get; set; }
	public List<SettingsFileEntry> Entries { get; } = new List<SettingsFileEntry>();

	public static SettingsFileDocument Load(string fileName)
	{
		using var stream = File.OpenRead(fileName);
		return Load(stream);
	}

	public static SettingsFileDocument Load(Stream stream)
	{
		return Load(XDocument.Load(stream));
	}

	public static SettingsFileDocument Load(XDocument document)
	{
		var root = document.Root ?? throw new FormatException("Not a settings file.");
		var settings = root.Elements().FirstOrDefault(element => element.Name.LocalName == "Settings");
		if (settings == null)
			throw new FormatException("Not a settings file.");

		var result = new SettingsFileDocument {
			GeneratedClassNamespace = GetAttributeValue(root, "GeneratedClassNamespace"),
			GeneratedClassName = GetAttributeValue(root, "GeneratedClassName"),
			UseMySettingsClassName = "true".Equals(GetAttributeValue(root, "UseMySettingsClassName"), StringComparison.OrdinalIgnoreCase)
		};

		foreach (var setting in settings.Elements().Where(element => element.Name.LocalName == "Setting")) {
			var value = setting.Elements().FirstOrDefault(element => element.Name.LocalName == "Value");
			result.Entries.Add(new SettingsFileEntry {
				Name = GetAttributeValue(setting, "Name"),
				Type = GetAttributeValue(setting, "Type", SettingsFileEntry.DefaultType),
				Scope = NormalizeScope(GetAttributeValue(setting, "Scope")),
				Value = value?.Value ?? string.Empty,
				Description = GetAttributeValue(setting, "Description"),
				GenerateDefaultValueInCode = GetAttributeValue(setting, "GenerateDefaultValueInCode"),
				Provider = GetAttributeValue(setting, "Provider"),
				Roaming = GetAttributeValue(setting, "Roaming")
			});
		}

		return result;
	}

	public XDocument ToXDocument()
	{
		XNamespace ns = XmlNamespace;
		var root = new XElement(ns + "SettingsFile",
			new XAttribute("CurrentProfile", "(Default)"),
			new XAttribute("GeneratedClassNamespace", GeneratedClassNamespace ?? string.Empty),
			new XAttribute("GeneratedClassName", GeneratedClassName ?? string.Empty));

		if (UseMySettingsClassName)
			root.Add(new XAttribute("UseMySettingsClassName", "true"));

		root.Add(new XElement(ns + "Profiles", new XElement(ns + "Profile", new XAttribute("Name", "(Default)"))));
		var settings = new XElement(ns + "Settings");
		foreach (var entry in Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name))) {
			var setting = new XElement(ns + "Setting",
				new XAttribute("Name", entry.Name.Trim()),
				new XAttribute("Type", string.IsNullOrWhiteSpace(entry.Type) ? SettingsFileEntry.DefaultType : entry.Type.Trim()),
				new XAttribute("Scope", NormalizeScope(entry.Scope)));

			if (!string.IsNullOrWhiteSpace(entry.Description))
				setting.Add(new XAttribute("Description", entry.Description));
			if (!string.IsNullOrWhiteSpace(entry.GenerateDefaultValueInCode))
				setting.Add(new XAttribute("GenerateDefaultValueInCode", entry.GenerateDefaultValueInCode));
			if (!string.IsNullOrWhiteSpace(entry.Provider))
				setting.Add(new XAttribute("Provider", entry.Provider));
			if (NormalizeScope(entry.Scope) == "User" && !string.IsNullOrWhiteSpace(entry.Roaming))
				setting.Add(new XAttribute("Roaming", entry.Roaming));

			setting.Add(new XElement(ns + "Value", new XAttribute("Profile", "(Default)"), entry.Value ?? string.Empty));
			settings.Add(setting);
		}

		root.Add(settings);
		return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
	}

	public void Save(Stream stream)
	{
		ToXDocument().Save(stream);
	}

	public static string NormalizeScope(string scope)
	{
		return "Application".Equals(scope, StringComparison.OrdinalIgnoreCase) ? "Application" : "User";
	}

	static string GetAttributeValue(XElement element, string name, string defaultValue = "")
	{
		var attribute = element.Attribute(name);
		return attribute != null ? attribute.Value : defaultValue;
	}
}

public sealed class SettingsFileEntry
{
	public const string DefaultType = "System.String";

	public string Name { get; set; } = string.Empty;
	public string Type { get; set; } = DefaultType;
	public string Scope { get; set; } = "User";
	public string Value { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string GenerateDefaultValueInCode { get; set; } = string.Empty;
	public string Provider { get; set; } = string.Empty;
	public string Roaming { get; set; } = string.Empty;
}
