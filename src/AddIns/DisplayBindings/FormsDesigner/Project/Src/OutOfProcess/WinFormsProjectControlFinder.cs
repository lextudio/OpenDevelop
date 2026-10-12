// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.FormsDesigner.OutOfProcess
{
	/// <summary>
	/// Finds the controls a WinForms project contributes to the Toolbox, from PE metadata only: the
	/// public concrete <c>Control</c> types with a public parameterless constructor in the project
	/// output and its copy-local references. Like the in-process CustomComponentsSideTab, which
	/// skipped GAC assemblies, framework and runtime assemblies are ignored - they would only repeat
	/// the curated WinForms catalog. <c>[ToolboxItem(false)]</c> types and <c>Form</c> subclasses
	/// (top-level windows) are skipped. One instance remembers what it returned and each file's
	/// timestamp, so reopening a form neither rescans an unchanged assembly nor repeats an item.
	/// </summary>
	public sealed class WinFormsProjectControlFinder
	{
		const string FormBase = "System.Windows.Forms.Form";

		static readonly HashSet<string> frameworkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
			"Accessibility", "mscorlib", "netstandard", "WindowsBase", "WindowsFormsIntegration",
			"PresentationCore", "PresentationFramework", "UIAutomationClient", "UIAutomationProvider", "UIAutomationTypes"
		};

		readonly Dictionary<string, DateTime> scanned = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
		readonly HashSet<string> returned = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>Whether a copy-local file is part of a framework or runtime rather than the
		/// project's own code or a third-party control library.</summary>
		public static bool IsFrameworkAssembly(string path)
		{
			var name = Path.GetFileNameWithoutExtension(path);
			return frameworkNames.Contains(name)
				|| name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Libre", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("ProGpu", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Controls not returned before, as (category = assembly file name, control).
		/// <paramref name="winFormsAssemblyPath"/> lets the metadata walk follow a base chain into
		/// System.Windows.Forms when the output directory does not carry it.</summary>
		public IReadOnlyList<(string Category, ToolboxMetadataDiscovery.ControlInfo Control)> FindNew(
			string projectAssemblyPath, IEnumerable<string> referencedAssemblyPaths, string winFormsAssemblyPath)
		{
			var all = new[] { projectAssemblyPath }.Concat(referencedAssemblyPaths)
				.Where(path => !String.IsNullOrEmpty(path) && File.Exists(path))
				.ToList();
			var references = all.Append(winFormsAssemblyPath).Where(path => !String.IsNullOrEmpty(path)).ToList();
			var result = new List<(string, ToolboxMetadataDiscovery.ControlInfo)>();
			foreach (var assembly in all.Where(path => !IsFrameworkAssembly(path))) {
				var stamp = File.GetLastWriteTimeUtc(assembly);
				if (scanned.TryGetValue(assembly, out var previous) && previous == stamp)
					continue;
				scanned[assembly] = stamp;
				try {
					var category = Path.GetFileNameWithoutExtension(assembly);
					var forms = new HashSet<string>(ToolboxMetadataDiscovery.Discover(assembly, references, FormBase, FormBase)
						.Select(control => control.FullName), StringComparer.Ordinal);
					foreach (var control in ToolboxMetadataDiscovery.Discover(assembly, references,
						ToolboxMetadataDiscovery.WinFormsControlBase, ToolboxMetadataDiscovery.WinFormsUserControlBase)) {
						if (control.HasPublicDefaultConstructor && !control.IsHiddenFromToolbox && !forms.Contains(control.FullName)
							&& returned.Add(category + "|" + control.FullName))
							result.Add((category, control));
					}
				} catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException) {
					// A native or locked file in the output directory contributes nothing.
				}
			}
			return result;
		}
	}
}
