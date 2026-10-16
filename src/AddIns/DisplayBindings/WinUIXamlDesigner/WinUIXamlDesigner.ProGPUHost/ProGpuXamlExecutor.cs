using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.UI.Xaml;
using ProGPU.WinUI.Designer;
using ProGPU.Xaml.Roslyn;
using ProGPU.Xaml.Schema;
using ProGPU.Xaml.Workspaces;
using XamlStudio.Toolkit.Services;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

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
	// Holds the activated App.xaml wrapper, so its collectible load context outlives every render.
	readonly WinUiXamlLivePreviewSession appSession = new();
	ResourceDictionary appResources;
	bool appResourcesLoaded;
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

		// A page's {StaticResource} is resolved while it activates, falling back to
		// Application.Current.Resources - so the app's resources must be in place first.
		await EnsureAppResourcesAsync().ConfigureAwait(true);
		InstallAppResources();

		// TryUpdate keeps the previous tree when the candidate fails to load or activate, so an
		// invalid edit degrades to "last good preview" instead of a blank or crashed design pane.
		return await CompileAndActivateAsync(session, xaml).ConfigureAwait(true);
	}

	async Task<FrameworkElement> CompileAndActivateAsync(WinUiXamlLivePreviewSession target, string xaml)
	{
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

		FrameworkElement published = null;
		var result = target.TryUpdate(
			artifact.PeImage.ToArray(),
			preview.QualifiedTypeName,
			root => published = root);
		if (!result.Success)
			throw new InvalidOperationException(result.Message);

		return published ?? result.Root;
	}

	/// <summary>
	/// Compiles the owning project's App.xaml resources once per executor. ProGPU has no runtime
	/// XAML reader - markup only becomes objects through the same compile-and-activate pipeline
	/// as the page - so the self-contained dictionary AppResourceBuilder produces (the one the
	/// out-of-process hosts receive over app/resources) is wrapped in a Grid, activated in its own
	/// preview session, and its Resources taken from there. A failure only costs the app
	/// resources: the page still renders, and a missing key then reports itself as before.
	/// </summary>
	async Task EnsureAppResourcesAsync()
	{
		if (appResourcesLoaded)
			return;
		appResourcesLoaded = true;
		var appXaml = FindAppXaml();
		if (appXaml == null)
			return;
		var errors = new List<string>();
		var dictionary = AppResourceBuilder.Build(appXaml, errors);
		foreach (var error in errors)
			LoggingService.Warn("ProGPU WinUI designer: App.xaml resources: " + error);
		if (dictionary == null)
			return;
		try {
			var wrapper = new XElement(Presentation + "Grid",
				new XAttribute(XNamespace.Xmlns + "x", XamlNamespace),
				// Live preview only activates a root that carries x:Class.
				new XAttribute(XamlNamespace + "Class", "OpenDevelop.DesignTime.ApplicationResources"),
				new XElement(Presentation + "Grid.Resources", XElement.Parse(dictionary)));
			var root = await CompileAndActivateAsync(appSession, wrapper.ToString()).ConfigureAwait(true);
			appResources = root.Resources;
		} catch (Exception e) {
			LoggingService.Warn("ProGPU WinUI designer: App.xaml resources could not be compiled: " + e.GetBaseException().Message);
		}
	}

	static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

	string FindAppXaml()
	{
		var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(resourceUri));
		var directory = project?.Directory.ToString();
		if (string.IsNullOrEmpty(directory))
			return null;
		var candidate = Path.Combine(directory, "App.xaml");
		return File.Exists(candidate) ? candidate : null;
	}

	// Application.Current is process-wide while there is one executor per open document, so the
	// dictionary merged into it is swapped for the rendering document's own before each render.
	static ResourceDictionary installedAppResources;

	void InstallAppResources()
	{
		var application = Application.Current;
		if (application == null) {
			// This offscreen host never runs AppRunner, the only code that assigns Current, and
			// its setter is internal.
			application = new Application();
			typeof(Application).GetProperty(nameof(Application.Current))
				.GetSetMethod(nonPublic: true)
				.Invoke(null, new object[] { application });
		}
		if (ReferenceEquals(installedAppResources, appResources))
			return;
		if (installedAppResources != null)
			application.Resources.MergedDictionaries.Remove(installedAppResources);
		if (appResources != null)
			application.Resources.MergedDictionaries.Add(appResources);
		installedAppResources = appResources;
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
	ISet<string> CollectMetadataReferences()
	{
		var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
		foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
			// The list also names native libraries on Windows (e.g. wpfgfx_cor3.dll, vcruntime140_cor3.dll and
			// ijwhost.dll under runtimes\win-*\lib\net10.0); Roslyn cannot open those as metadata.
			if (File.Exists(path) && IsManagedAssembly(path))
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

		// Without this, the preview compilation only ever sees ProGPU.WinUI + the BCL - so any
		// type the DESIGNED PROJECT ITSELF defines (converters, custom controls, code-behind
		// partial classes) is "unresolved", and every member access on it cascades into its own
		// diagnostic (doc/technotes/winui-designer.md "Real-World Project Preview Problem", ~140 of
		// 169 diagnostic lines on a real Uno project). Deliberately NOT adding the project's own
		// Uno.WinUI/Microsoft.UI.Xaml package references here (see that technote's Fix roadmap,
		// option (1) vs (2)): those assemblies declare a DIFFERENT, incompatible identity for
		// "Microsoft.UI.Xaml.FrameworkElement" et al. than ProGPU.WinUI's own implementation
		// already loaded above, and Roslyn would see two unrelated types of the same name - the
		// project's compiled output assembly alone is the safe, additive step.
		var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(resourceUri));
		var outputAssembly = project?.OutputAssemblyFullPath;
		if (outputAssembly != null)
			Add(paths, outputAssembly.ToString());

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
		if (ReferenceEquals(installedAppResources, appResources) && appResources != null) {
			Application.Current?.Resources.MergedDictionaries.Remove(appResources);
			installedAppResources = null;
		}
		appResources = null;
		session.Dispose();
		appSession.Dispose();
		workspace?.Dispose();
		workspace = null;
	}
}
