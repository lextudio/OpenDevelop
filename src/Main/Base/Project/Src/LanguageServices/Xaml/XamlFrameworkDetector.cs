using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop.LanguageServices.Xaml
{
	public enum XamlFrameworkKind { Unknown, Wpf, WinUI, Uno }

	/// <summary>
	/// The runtime that owns the project's UI type identities. This is deliberately separate
	/// from <see cref="XamlFrameworkKind"/>: both LibreWPF and Microsoft WPF use WPF markup,
	/// but their controls must never be loaded by the same design host.
	/// </summary>
	public enum XamlRuntimeKind { Unknown, LibreWpf, MicrosoftWpf, MicrosoftWinUI, Uno }

	public sealed class XamlFrameworkContext
	{
		public XamlFrameworkContext(XamlFrameworkKind kind, string projectFileName, string evidence)
			: this(kind, XamlRuntimeKind.Unknown, projectFileName, evidence) { }

		public XamlFrameworkContext(XamlFrameworkKind kind, XamlRuntimeKind runtime, string projectFileName, string evidence)
		{
			Kind = kind;
			Runtime = runtime;
			ProjectFileName = projectFileName;
			Evidence = evidence;
		}
		public XamlFrameworkKind Kind { get; }
		public XamlRuntimeKind Runtime { get; }
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
					return new XamlFrameworkContext(XamlFrameworkKind.Uno, XamlRuntimeKind.Uno, projectFileName, "Uno SDK/package");
				if (HasPackage("Microsoft.WindowsAppSDK") || HasPackage("Microsoft.UI.Xaml")
				    || properties.TryGetValue("UseWinUI", out var useWinUI) && IsTrue(useWinUI))
				{
					// A WinUI document is served only by the Microsoft WinUI runtime host. It is
					// never re-identified as Uno just because Microsoft WinUI cannot run on this
					// platform: doing so silently swapped the framework identity and rendered a
					// native WinUI page through the Uno/ProGPU compatibility host. When the WinUI
					// host is unavailable the designer must say exactly that (see
					// WinUIXamlHost.StatusText) instead of falling back to another runtime.
					return new XamlFrameworkContext(XamlFrameworkKind.WinUI, XamlRuntimeKind.MicrosoftWinUI, projectFileName, "Windows App SDK/WinUI property or package");
				}
				if (sdk.Contains("LibreWPF.Sdk", StringComparison.OrdinalIgnoreCase))
					return new XamlFrameworkContext(XamlFrameworkKind.Wpf, XamlRuntimeKind.LibreWpf, projectFileName, "LibreWPF SDK");
				if (properties.TryGetValue("UseWPF", out var useWpf) && IsTrue(useWpf))
				{
					// On macOS/Linux, Microsoft WPF is unavailable — use LibreWPF instead.
					var runtime = OperatingSystem.IsWindows() ? XamlRuntimeKind.MicrosoftWpf : XamlRuntimeKind.LibreWpf;
					return new XamlFrameworkContext(XamlFrameworkKind.Wpf, runtime, projectFileName, runtime == XamlRuntimeKind.LibreWpf ? "WPF property (LibreWPF on non-Windows)" : "WPF property");
				}
				return Unknown("Project has no recognized XAML framework marker", projectFileName);
			} catch (Exception ex) {
				return Unknown("Project parse failed: " + ex.Message, projectFileName);
			}
		}

		static IProject FindOwningProject(string fileName) => SD.ProjectService?.CurrentSolution?.Projects
			.Where(p => p.Directory != null && fileName.StartsWith(p.Directory.ToString(), StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(p => p.Directory.ToString().Length).FirstOrDefault();
		static bool IsTrue(string value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
		static XamlFrameworkContext Unknown(string evidence, string project = null) => new(XamlFrameworkKind.Unknown, XamlRuntimeKind.Unknown, project, evidence);
	}
}
