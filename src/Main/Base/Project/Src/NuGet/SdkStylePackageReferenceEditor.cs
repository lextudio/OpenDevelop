using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using NuGet.Versioning;

namespace ICSharpCode.SharpDevelop.NuGet
{
	public sealed class SdkStylePackageReferenceEditor
	{
		readonly string projectFileName;

		public SdkStylePackageReferenceEditor(string projectFileName)
		{
			if (string.IsNullOrWhiteSpace(projectFileName))
				throw new ArgumentException("Project file name cannot be empty.", nameof(projectFileName));

			this.projectFileName = projectFileName;
		}

		public bool AddOrUpdate(string packageId, NuGetVersion version)
		{
			if (string.IsNullOrWhiteSpace(packageId))
				throw new ArgumentException("Package id cannot be empty.", nameof(packageId));
			if (version is null)
				throw new ArgumentNullException(nameof(version));

			var project = XDocument.Load(projectFileName, LoadOptions.PreserveWhitespace);
			var existing = FindPackageReference(project, packageId);
			if (existing != null) {
				var existingVersion = GetVersion(existing);
				if (string.Equals(existingVersion, version.ToNormalizedString(), StringComparison.OrdinalIgnoreCase))
					return false;

				SetVersion(existing, version);
				project.Save(projectFileName);
				return true;
			}

			var itemGroup = FindPackageReferenceItemGroup(project);
			if (itemGroup is null) {
				itemGroup = new XElement("ItemGroup");
				project.Root!.Add(itemGroup);
			}

			itemGroup.Add(new XElement(
				"PackageReference",
				new XAttribute("Include", packageId),
				new XAttribute("Version", version.ToNormalizedString())));
			project.Save(projectFileName);
			return true;
		}

		public IReadOnlyList<SdkStylePackageReference> GetPackageReferences()
		{
			var project = XDocument.Load(projectFileName, LoadOptions.PreserveWhitespace);
			return project.Descendants()
				.Where(element => string.Equals(element.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase))
				.Select(ToPackageReference)
				.Where(package => package is not null)
				.Select(package => package!)
				.OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		public bool Remove(string packageId)
		{
			if (string.IsNullOrWhiteSpace(packageId))
				throw new ArgumentException("Package id cannot be empty.", nameof(packageId));

			var project = XDocument.Load(projectFileName, LoadOptions.PreserveWhitespace);
			var existing = FindPackageReference(project, packageId);
			if (existing is null)
				return false;

			var itemGroup = existing.Parent;
			existing.Remove();
			if (itemGroup != null && !itemGroup.Elements().Any())
				itemGroup.Remove();
			project.Save(projectFileName);
			return true;
		}

		static XElement? FindPackageReference(XDocument project, string packageId)
		{
			return project.Descendants()
				.FirstOrDefault(element =>
					string.Equals(element.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase) &&
					string.Equals((string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"), packageId, StringComparison.OrdinalIgnoreCase));
		}

		static XElement? FindPackageReferenceItemGroup(XDocument project)
		{
			return project.Root?.Elements()
				.FirstOrDefault(element =>
					string.Equals(element.Name.LocalName, "ItemGroup", StringComparison.OrdinalIgnoreCase) &&
					element.Attribute("Condition") is null &&
					element.Elements().Any(child => string.Equals(child.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase)));
		}

		static string? GetVersion(XElement item)
		{
			var version = item.Attribute("Version");
			if (version != null)
				return version.Value;

			return item.Elements().FirstOrDefault(element =>
				string.Equals(element.Name.LocalName, "Version", StringComparison.OrdinalIgnoreCase))?.Value;
		}

		static void SetVersion(XElement item, NuGetVersion version)
		{
			var versionText = version.ToNormalizedString();
			var versionAttribute = item.Attribute("Version");
			if (versionAttribute != null) {
				versionAttribute.Value = versionText;
				return;
			}

			var versionElement = item.Elements().FirstOrDefault(element =>
				string.Equals(element.Name.LocalName, "Version", StringComparison.OrdinalIgnoreCase));
			if (versionElement != null) {
				versionElement.Value = versionText;
				return;
			}

			item.Add(new XAttribute("Version", versionText));
		}

		static SdkStylePackageReference? ToPackageReference(XElement item)
		{
			var id = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update");
			var version = GetVersion(item);
			if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
				return null;

			return new SdkStylePackageReference(id, version);
		}
	}
}
