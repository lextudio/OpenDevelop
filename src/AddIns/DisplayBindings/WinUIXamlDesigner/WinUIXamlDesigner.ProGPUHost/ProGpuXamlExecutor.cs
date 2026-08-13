using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.UI.Xaml;
using ProGPU.WinUI.Designer;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Workspaces;
using XamlStudio.Toolkit.Services;

namespace ICSharpCode.WinUIXamlDesigner.ProGPUHost;

/// <summary>
/// Materializes WinUI/Uno XAML through ProGPU's XAML compiler and its collectible preview
/// assembly pipeline. This is the single execution seam behind XAML Studio's preprocessing,
/// binding inspection and diagnostics; no WPF XamlReader is involved.
/// </summary>
sealed class ProGpuXamlExecutor : IProGpuXamlExecutor, IDisposable
{
	readonly RoslynXamlProjectPreviewService previewService = new();
	readonly WinUiXamlLivePreviewSession session = new();
	readonly WinUiXamlProfile profile = new();
	readonly string resourceUri;
	AdhocWorkspace workspace;
	ProjectId projectId;
	DocumentId xamlDocumentId;
	bool disposed;

	public ProGpuXamlExecutor(string resourceUri)
	{
		this.resourceUri = string.IsNullOrWhiteSpace(resourceUri) ? "Preview.xaml" : resourceUri;
	}

	/// <summary>Set when the compilation host could not be created at all.</summary>
	public string SetupError { get; private set; }

	public async Task<object> MaterializeAsync(string xaml)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (!WinUiXamlLivePreviewSession.IsRuntimeSupported)
			throw new InvalidOperationException(WinUiXamlLivePreviewSession.RuntimeSupportMessage);

		var project = EnsureProject();
		var preview = await previewService.CompileAsync(
			project,
			xamlDocumentId,
			profile,
			new RoslynXamlProjectPreviewOptions {
				EmitArtifact = true,
				EditedText = SourceText.From(xaml ?? string.Empty),
				InspectionOptions = new RoslynXamlCompilationInspectionOptions {
					CompilerOptions = new XamlCompilerOptions {
						Framework = "winui",
						ResourceUri = resourceUri,
						Strict = false
					}
				}
			},
			CancellationToken.None).ConfigureAwait(true);

		if (!preview.CanMaterialize)
			throw new InvalidOperationException(DescribeFailure(preview));

		var artifact = preview.Artifact;
		if (artifact == null || !artifact.Success)
			throw new InvalidOperationException(DescribeFailure(preview));

		// TryUpdate keeps the previous tree when the candidate fails to load or activate, so an
		// invalid edit degrades to "last good preview" instead of a blank or crashed design pane.
		FrameworkElement published = null;
		var result = session.TryUpdate(
			artifact.PeImage.ToArray(),
			preview.QualifiedTypeName,
			root => published = root);
		if (!result.Success)
			throw new InvalidOperationException(result.Message);

		return published ?? result.Root;
	}

	static string DescribeFailure(RoslynXamlProjectPreview preview)
	{
		if (!string.IsNullOrWhiteSpace(preview.MaterializationError))
			return preview.MaterializationError;
		var errors = preview.Artifact?.Diagnostics
			.Where(static d => d.Severity == DiagnosticSeverity.Error)
			.Select(static d => d.GetMessage())
			.ToArray() ?? Array.Empty<string>();
		return errors.Length == 0
			? "ProGPU could not materialize this document and reported no diagnostic."
			: string.Join(Environment.NewLine, errors);
	}

	Project EnsureProject()
	{
		if (workspace != null)
			return workspace.CurrentSolution.GetProject(projectId);

		var references = CollectMetadataReferences();
		if (references.Count == 0) {
			SetupError = "This runtime does not expose trusted metadata reference paths.";
			throw new InvalidOperationException(SetupError);
		}

		var created = new AdhocWorkspace();
		try {
			projectId = ProjectId.CreateNewId();
			xamlDocumentId = DocumentId.CreateNewId(projectId);
			var solution = created.CurrentSolution
				.AddProject(ProjectInfo.Create(
					projectId,
					VersionStamp.Create(),
					"OpenDevelop.WinUIXamlPreview",
					"OpenDevelop.WinUIXamlPreview",
					LanguageNames.CSharp,
					parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
					compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
					metadataReferences: references
						.OrderBy(static path => path, StringComparer.Ordinal)
						.Select(static path => MetadataReference.CreateFromFile(path))))
				.AddAdditionalDocument(xamlDocumentId, resourceUri, SourceText.From(string.Empty), filePath: resourceUri);
			if (!created.TryApplyChanges(solution))
				throw new InvalidOperationException("The preview project could not be applied to its workspace.");
			workspace = created;
			return workspace.CurrentSolution.GetProject(projectId);
		} catch {
			created.Dispose();
			throw;
		}
	}

	/// <summary>
	/// The preview compilation resolves WinUI types from the ProGPU runtime this process already
	/// loaded, so the designed document sees exactly the assemblies the renderer will execute.
	/// </summary>
	static ISet<string> CollectMetadataReferences()
	{
		var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
		foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
			if (File.Exists(path))
				paths.Add(path);
		}

		// The trusted-platform list only covers what the host resolved at startup, so the ProGPU
		// dependency graph the generated program binds against (ProGPU.Layout, ProGPU.Scene, ...)
		// is missing from it. Take the whole directory that carries the WinUI runtime instead.
		var runtimeDirectory = Path.GetDirectoryName(typeof(FrameworkElement).Assembly.Location);
		if (!string.IsNullOrEmpty(runtimeDirectory) && Directory.Exists(runtimeDirectory)) {
			foreach (var dll in Directory.EnumerateFiles(runtimeDirectory, "*.dll")) {
				if (IsManagedAssembly(dll))
					Add(paths, dll);
			}
		}
		Add(paths, typeof(FrameworkElement).Assembly.Location);
		return paths;
	}

	/// <summary>The AddIn folder also carries native interop libraries that Roslyn cannot read.</summary>
	static bool IsManagedAssembly(string path)
	{
		try {
			System.Reflection.AssemblyName.GetAssemblyName(path);
			return true;
		} catch (BadImageFormatException) {
			return false;
		} catch (IOException) {
			return false;
		}
	}

	static void Add(ISet<string> paths, string path)
	{
		if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
			paths.Add(path);
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		session.Dispose();
		workspace?.Dispose();
		workspace = null;
	}
}
