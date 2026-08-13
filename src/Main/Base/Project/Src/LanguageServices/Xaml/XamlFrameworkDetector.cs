using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop.LanguageServices.Xaml
{
	public enum XamlFrameworkKind { Unknown, Wpf, WinUI, Uno }

	public sealed class XamlFrameworkContext
	{
		public XamlFrameworkContext(XamlFrameworkKind kind, string projectFileName, string evidence)
		{
			Kind = kind;
			ProjectFileName = projectFileName;
			Evidence = evidence;
		}
		public XamlFrameworkKind Kind { get; }
		public string ProjectFileName { get; }
		public string Evidence { get; }
	}

	/// <summary>Single routing authority shared by XAML designers and language-service hosts.</summary>
	public static class XamlFrameworkDetector
	{
		public static XamlFrameworkContext Detect(string xamlFileName)
		{
			if (string.IsNullOrEmpty(xamlFileName)) return Unknown("No file name");
			var project = FindOwningProject(xamlFileName);
			return project == null ? Unknown("No owning project") : DetectProjectFile(project.FileName);
		}

		public static XamlFrameworkContext DetectProjectFile(string projectFileName)
		{
			if (string.IsNullOrEmpty(projectFileName) || !File.Exists(projectFileName))
				return Unknown("Project file is unavailable", projectFileName);
			try {
				var document = XDocument.Load(projectFileName, LoadOptions.None);
				var root = document.Root;
				var sdk = (string)root?.Attribute("Sdk") ?? string.Join(";", root?.Elements().Where(e => e.Name.LocalName == "Sdk").Select(e => (string)e.Attribute("Name")) ?? Array.Empty<string>());
				var packages = root?.Descendants().Where(e => e.Name.LocalName == "PackageReference")
					.Select(e => (string)e.Attribute("Include") ?? (string)e.Attribute("Update") ?? "").ToArray() ?? Array.Empty<string>();
				var properties = root?.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup")
					.GroupBy(e => e.Name.LocalName, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

				bool HasPackage(string prefix) => packages.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
				if (sdk.Contains("Uno.Sdk", StringComparison.OrdinalIgnoreCase) || HasPackage("Uno.WinUI") || HasPackage("Uno.UI"))
					return new XamlFrameworkContext(XamlFrameworkKind.Uno, projectFileName, "Uno SDK/package");
				if (HasPackage("Microsoft.WindowsAppSDK") || HasPackage("Microsoft.UI.Xaml")
				    || properties.TryGetValue("UseWinUI", out var useWinUI) && IsTrue(useWinUI))
					return new XamlFrameworkContext(XamlFrameworkKind.WinUI, projectFileName, "Windows App SDK/WinUI property or package");
				if (properties.TryGetValue("UseWPF", out var useWpf) && IsTrue(useWpf)
				    || sdk.Contains("LibreWPF.Sdk", StringComparison.OrdinalIgnoreCase))
					return new XamlFrameworkContext(XamlFrameworkKind.Wpf, projectFileName, "UseWPF/LibreWPF SDK");
				return Unknown("Project has no recognized XAML framework marker", projectFileName);
			} catch (Exception ex) {
				return Unknown("Project parse failed: " + ex.Message, projectFileName);
			}
		}

		static IProject FindOwningProject(string fileName) => SD.ProjectService?.CurrentSolution?.Projects
			.Where(p => p.Directory != null && fileName.StartsWith(p.Directory.ToString(), StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(p => p.Directory.ToString().Length).FirstOrDefault();
		static bool IsTrue(string value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
		static XamlFrameworkContext Unknown(string evidence, string project = null) => new(XamlFrameworkKind.Unknown, project, evidence);
	}
}
