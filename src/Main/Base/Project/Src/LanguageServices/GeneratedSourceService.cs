using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
	/// <summary>
	/// The C# a source generator wrote for an additional file of a project (the LibreWPF XAML source generator reads XAML files as AdditionalFiles),
	/// read from the Roslyn host's compilation rather than from <c>obj/</c>, which a normal build does not fill with generated output.
	/// </summary>
	public static class GeneratedSourceService
	{
		/// <summary>The generated documents for <paramref name="additionalFile"/>; empty when its project is not loaded into a Roslyn host or nothing was
		/// generated. Makes sure the project is loaded first, as an editor request would.</summary>
		public static async Task<IReadOnlyList<GeneratedSourceInfo>> GetAsync(string additionalFile, CancellationToken cancellationToken)
		{
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(additionalFile));
			var sourceFile = project?.Items.OfType<FileProjectItem>().Select(item => item.FileName.ToString())
				.FirstOrDefault(name => name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (sourceFile == null || registry == null || !registry.TryGetService(sourceFile, out var service)
				|| service is not RemoteLanguageService remote)
				return Array.Empty<GeneratedSourceInfo>();
			await remote.RefreshProjectAsync(new DocumentId(sourceFile), cancellationToken).ConfigureAwait(false);
			return await remote.Protocol.RoslynGeneratedSourcesAsync(new TextDocumentIdentifier(additionalFile), cancellationToken).ConfigureAwait(false);
		}

		/// <summary>Writes a generated document under the user's temp folder, read-only (it is output, an edit would be lost), and returns its path.</summary>
		public static string WriteReadOnlyCopy(string projectName, GeneratedSourceInfo source)
		{
			var directory = Path.Combine(Path.GetTempPath(), "OpenDevelop", "Generated", Sanitize(projectName));
			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, Sanitize(source.HintName));
			if (File.Exists(path))
				File.SetAttributes(path, FileAttributes.Normal);
			File.WriteAllText(path, source.Text);
			File.SetAttributes(path, FileAttributes.ReadOnly);
			return path;
		}

		static string Sanitize(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
	}
}
