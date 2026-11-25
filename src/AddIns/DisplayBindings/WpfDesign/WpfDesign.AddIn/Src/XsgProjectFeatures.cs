using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>Which XAML source generator (XSG) features a project has on, read from its evaluated MSBuild
	/// properties. BAML and XSG are separate compile paths: every feature belongs to XSG and is off for a BAML project.</summary>
	public static class XsgProjectFeatures
	{
		/// <summary>Simpler XAML: needs the overall switch on and the feature not switched off (the SDK defaults the
		/// feature to the overall switch; a feature set without the overall switch does nothing).</summary>
		public static bool ImplicitXamlNamespaces(Func<string, string> evaluated)
		{
			if (evaluated == null)
				return false;
			try {
				return IsTrue(evaluated("UseLibreWpfXamlSourceGenerator"))
					&& !string.Equals(evaluated("LibreWpfSimplerXaml")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
			} catch (Exception ex) when (ex is not OutOfMemoryException) {
				return false;
			}
		}

		/// <summary>C# expressions in attribute values: the overall switch on and <c>LibreWpfCSharpExpressions</c> not switched off (it follows the
		/// overall switch).</summary>
		public static bool CSharpExpressions(Func<string, string> evaluated)
		{
			if (evaluated == null)
				return false;
			try {
				return IsTrue(evaluated("UseLibreWpfXamlSourceGenerator"))
					&& !string.Equals(evaluated("LibreWpfCSharpExpressions")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
			} catch (Exception ex) when (ex is not OutOfMemoryException) {
				return false;
			}
		}

		static readonly Regex GlobalUsingLine = new Regex(@"^[ \t]*global[ \t]+using[ \t]+(?!static\b)(?:global::)?(?<ns>[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*)[ \t]*;",
			RegexOptions.Multiline | RegexOptions.CultureInvariant);

		/// <summary>The namespaces of the <c>global using</c> directives in <paramref name="sourceText"/>: no alias (<c>X = N</c> does not match), no
		/// <c>static</c>. Mirrors what the XAML source generator reads from the compilation.</summary>
		public static IEnumerable<string> ParseGlobalUsings(string sourceText)
			=> sourceText == null ? Enumerable.Empty<string>() : GlobalUsingLine.Matches(sourceText).Cast<Match>().Select(m => m.Groups["ns"].Value);

		static readonly ConcurrentDictionary<string, (long Ticks, string[] Namespaces)> SourceCache = new ConcurrentDictionary<string, (long, string[])>(StringComparer.OrdinalIgnoreCase);

		/// <summary>The namespaces the project's <c>global using</c> directives name: the SDK's <c>Using</c> items (ImplicitUsings and explicit ones,
		/// without alias or <c>static</c>) and the directives written in its C# source files, in that order, without repeats. Source files are read
		/// again only when they change.</summary>
		public static List<string> GlobalUsingNamespaces(IProject project)
		{
			var result = new List<string>();
			if (!(project is MSBuildBasedProject msbuild))
				return result;
			foreach (var item in msbuild.GetEvaluatedProjectItems())
			{
				if (!string.Equals(item.ItemType, "Using", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(item.EvaluatedInclude))
					continue;
				if (!string.IsNullOrWhiteSpace(item.GetMetadata("Alias")) || string.Equals(item.GetMetadata("Static")?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
					continue;
				var name = item.EvaluatedInclude.Trim();
				if (!result.Contains(name))
					result.Add(name);
			}
			foreach (var file in project.Items.OfType<FileProjectItem>().Where(i => i.ItemType == ItemType.Compile))
			{
				var path = file.FileName.ToString();
				if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
					continue;
				try
				{
					var ticks = File.GetLastWriteTimeUtc(path).Ticks;
					if (!SourceCache.TryGetValue(path, out var cached) || cached.Ticks != ticks)
					{
						cached = (ticks, ParseGlobalUsings(File.ReadAllText(path)).ToArray());
						SourceCache[path] = cached;
					}
					foreach (var name in cached.Namespaces)
						if (!result.Contains(name))
							result.Add(name);
				}
				catch (IOException)
				{
					// A file that cannot be read now contributes nothing; the next snapshot reads it again.
				}
			}
			return result;
		}

		static bool IsTrue(string value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
	}
}
