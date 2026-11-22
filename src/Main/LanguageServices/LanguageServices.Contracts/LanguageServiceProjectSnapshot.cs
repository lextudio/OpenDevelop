#nullable enable
using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
	/// <summary>
	/// Wire-only form of the evaluated project snapshot. The IDE owns evaluation and sends this
	/// object to the host; therefore the host must not reference the IDE project model merely to
	/// deserialise it.
	/// </summary>
	public sealed class LanguageServiceProjectSnapshot
	{
		public LanguageServiceProjectSnapshot(
			string projectFileName, string language, IReadOnlyList<string> documentFileNames,
			IReadOnlyList<string> metadataReferenceFileNames, IReadOnlyList<string> projectReferenceFileNames,
			IReadOnlyList<string> preprocessorSymbols, string? languageVersion, string? nullableContext,
			string? targetFramework = null, IReadOnlyList<string>? analyzerAssemblyFileNames = null,
			IReadOnlyList<string>? additionalFileNames = null)
		{
			ProjectFileName = projectFileName ?? throw new ArgumentNullException(nameof(projectFileName));
			Language = language ?? throw new ArgumentNullException(nameof(language));
			DocumentFileNames = documentFileNames ?? throw new ArgumentNullException(nameof(documentFileNames));
			MetadataReferenceFileNames = metadataReferenceFileNames ?? throw new ArgumentNullException(nameof(metadataReferenceFileNames));
			ProjectReferenceFileNames = projectReferenceFileNames ?? throw new ArgumentNullException(nameof(projectReferenceFileNames));
			PreprocessorSymbols = preprocessorSymbols ?? throw new ArgumentNullException(nameof(preprocessorSymbols));
			LanguageVersion = languageVersion;
			NullableContext = nullableContext;
			TargetFramework = targetFramework;
			AnalyzerAssemblyFileNames = analyzerAssemblyFileNames ?? Array.Empty<string>();
			AdditionalFileNames = additionalFileNames ?? Array.Empty<string>();
		}

		public string ProjectFileName { get; }
		public string Language { get; }
		public IReadOnlyList<string> DocumentFileNames { get; }
		public IReadOnlyList<string> MetadataReferenceFileNames { get; }
		public IReadOnlyList<string> ProjectReferenceFileNames { get; }
		public IReadOnlyList<string> PreprocessorSymbols { get; }
		public string? LanguageVersion { get; }
		public string? NullableContext { get; }
		public string? TargetFramework { get; }
		public IReadOnlyList<string> AnalyzerAssemblyFileNames { get; }
		/// <summary>MSBuild AdditionalFiles: inputs of source generators (XAML for the XAML source generator).</summary>
		public IReadOnlyList<string> AdditionalFileNames { get; }
		public string? SolutionDirectory { get; init; }
		/// <summary>
		/// C# global using directive bodies (<c>global::System</c>, <c>static global::X</c>,
		/// <c>A = global::B</c>) from the evaluated <c>Using</c> items: what the SDK's generated
		/// GlobalUsings.g.cs would hold.
		/// </summary>
		public IReadOnlyList<string> GlobalUsings { get; init; } = Array.Empty<string>();
		public LanguageServiceProjectSnapshot WithSolutionDirectory(string? directory) =>
			new(ProjectFileName, Language, DocumentFileNames, MetadataReferenceFileNames, ProjectReferenceFileNames,
				PreprocessorSymbols, LanguageVersion, NullableContext, TargetFramework, AnalyzerAssemblyFileNames,
				AdditionalFileNames) { SolutionDirectory = directory, GlobalUsings = GlobalUsings };
	}
}
