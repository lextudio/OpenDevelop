// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ICSharpCode.PackageManagement
{
	/// <summary>Edits the PackageVersion items of a Central Package Management <c>Directory.Packages.props</c>.</summary>
	public static class CentralPackageVersionsFile
	{
		/// <summary>
		/// The versions an unconditional <c>PackageVersion</c> entry gives each package id (ids compare
		/// ignoring case). Entries in conditional item groups are skipped, as in <see cref="Set"/>.
		/// </summary>
		public static System.Collections.Generic.Dictionary<string, string> Read(string propsFile)
		{
			var versions = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			XDocument document = XDocument.Load(propsFile);
			foreach (XElement element in document.Descendants().Where(e => e.Name.LocalName == "PackageVersion")) {
				if (element.Attribute("Condition") != null || element.Parent?.Attribute("Condition") != null) {
					continue;
				}
				string id = (string)element.Attribute("Include") ?? (string)element.Attribute("Update");
				string version = (string)element.Attribute("Version");
				if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(version) && !versions.ContainsKey(id)) {
					versions[id] = version;
				}
			}
			return versions;
		}

		/// <summary>Sets or adds <c>&lt;PackageVersion Include="id" Version="version" /&gt;</c>, keeping the file's layout.</summary>
		public static void Set(string propsFile, string packageId, string version)
		{
			// Remember how the file was written: XDocument.Save would add an XML declaration, drop a
			// byte-order mark and turn CRLF into LF, so the edit would show up as a rewrite of the whole file.
			byte[] original = File.ReadAllBytes(propsFile);
			bool hasBom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
			bool usesCrLf = Encoding.UTF8.GetString(original).Contains("\r\n");
			XDocument document = XDocument.Load(propsFile, LoadOptions.PreserveWhitespace);
			XElement root = document.Root;
			XNamespace ns = root.Name.Namespace;

			// Only entries that apply everywhere count: one inside a conditional ItemGroup (for example per
			// target framework) is another configuration's pin, and changing it would leave this one without
			// an effective version or silently re-pin that configuration.
			Func<XElement, bool> isUnconditional = element =>
				element.Attribute("Condition") == null && element.Parent?.Attribute("Condition") == null;

			XElement existing = root.Descendants(ns + "PackageVersion").FirstOrDefault(element =>
				isUnconditional(element)
				&& string.Equals((string)element.Attribute("Include") ?? (string)element.Attribute("Update"), packageId, StringComparison.OrdinalIgnoreCase));
			if (existing != null) {
				existing.SetAttributeValue("Version", version);
			} else {
				var added = new XElement(ns + "PackageVersion", new XAttribute("Include", packageId), new XAttribute("Version", version));
				XElement last = root.Descendants(ns + "PackageVersion").LastOrDefault(isUnconditional);
				if (last != null) {
					// Same indentation as the previous entry.
					string indent = (last.PreviousNode as XText)?.Value ?? Environment.NewLine + "    ";
					last.AddAfterSelf(new XText(indent), added);
				} else {
					root.Add(new XText(Environment.NewLine + "  "), new XElement(ns + "ItemGroup", new XText(Environment.NewLine + "    "), added, new XText(Environment.NewLine + "  ")), new XText(Environment.NewLine));
				}
			}
			var settings = new XmlWriterSettings {
				OmitXmlDeclaration = document.Declaration == null,
				Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom),
				NewLineHandling = NewLineHandling.Replace,
				NewLineChars = usesCrLf ? "\r\n" : "\n",
			};
			using (XmlWriter writer = XmlWriter.Create(propsFile, settings)) {
				document.Save(writer);
			}
		}
	}
}
