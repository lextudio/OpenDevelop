using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

#if MICROSOFT_WPF
using System.Windows.Interop;
#endif
using System.Xml;

using ICSharpCode.WpfDesign;
using ICSharpCode.WpfDesign.Designer.Services;
using ICSharpCode.WpfDesign.Designer.Xaml;
using ICSharpCode.SharpDevelop.Designer.Remote;

using StreamJsonRpc;

#if !MICROSOFT_WPF
using ProGPU.Backend;
using Silk.NET.WebGPU;
using GpuCompositionTarget = System.Windows.Media.ProGPU.ProGpuWpfCompositionTarget;
#endif

namespace ICSharpCode.WpfDesign.SurfaceHost
{
	/// <summary>
	/// StreamJsonRpc target of the WPF out-of-process design host (Phase 0 slice). Every method
	/// runs on the headless dispatcher thread - WPF layout/render/hit-test all require the STA
	/// dispatcher, even though nothing here is ever attached to a real window.
	///
	/// Scope (see doc/technotes/wpf-designer.md's Phase 0 update): load/render/hit-test/save plus
	/// discrete design/set-property, design/set-bounds, design/delete-elements, design/rename
	/// mutations, matching the same DDP shape WinForms/WinUI already converged onto. No
	/// selection/adorner rendering, no raw gesture/input forwarding, no project-assembly type
	/// resolution (default XamlTypeFinder only) - all deliberately deferred.
	/// </summary>
	sealed class WpfSurfaceHostService : IDesignerChildService
	{
		readonly string expectedToken;
		readonly WpfHeadlessDispatcher dispatcher;
		readonly ManualResetEventSlim shutdown = new(false);

		string? sessionId;
		string? documentId;
		bool initialized;
		long version;

		XamlDesignContext? current;
		Dictionary<string, DesignItem> pathToItem = new(StringComparer.Ordinal);
		/// <summary>Expanders <see cref="Select"/> forced open because the current selection sits
		/// inside them while they were authored collapsed (Blend's "selecting inside a collapsed
		/// Expander temporarily opens it" behavior) - keyed by DesignItem so the override survives a
		/// selection change even though the element has no path-based identity of its own beyond
		/// that. Reverted to the recorded original value (always <c>false</c> - only ever populated
		/// for an Expander this method itself found collapsed) on every subsequent call, before
		/// applying the new selection's own overrides, so leftover state from the previous selection
		/// never lingers past one round trip.</summary>
		readonly Dictionary<DesignItem, bool> forcedExpansions = new();
		double lastWidth = 800;
		double lastHeight = 600;
		#if MICROSOFT_WPF
		// Native WPF's RenderTargetBitmap can return an all-black frame for a visual that has
		// never been connected to a PresentationSource.  This is a child-process-only hidden HWND;
		// it gives the native compositor a real source without ever putting a project visual in the
		// IDE process or showing a window to the user.
		HwndSource? renderPresentationSource;
		Visual? renderPresentationRoot;
		#endif
		#if !MICROSOFT_WPF
		/// <summary>Created once, lazily, on first successful render and reused for the process's
		/// life - matches every LibreWPF ProGPU test/harness, which all construct one
		/// CreateHeadless() target and reuse it across frames rather than recreating the GPU
		/// context per render.</summary>
		GpuCompositionTarget? renderTarget;
		// The ProGPU compositor can block inside composition (before its ReadPixels call) on
		// a headless/unsupported adapter. That work executes on the WPF dispatcher and cannot
		// be cancelled safely, so a timeout around ReadPixels alone cannot protect session/open.
		// Keep the portable host responsive by default; deployments that have validated their
		// adapter may explicitly enable the higher-fidelity path.
		static readonly bool portableGpuRenderingEnabled = String.Equals(
			Environment.GetEnvironmentVariable("OPENDEVELOP_WPF_GPU_RENDER"), "1", StringComparison.Ordinal);
		bool renderUnavailable = !portableGpuRenderingEnabled;
		#endif

		// Design-time theme resolution - resolved once per session/open from the project
		// assembly, see ResolveThemes. Theme name (as shown in the designer's combo) to
		// theme source; any number of themes, with no light/dark semantics attached to the
		// names (the theme IS whatever the dictionary paints).
		Assembly? projectAssembly;
		Dictionary<string, string>? themeSources;
		ResourceDictionary? appliedThemeDictionary;
		string? appliedThemeName;
		// The current document's app-resources dictionary as installed into
		// Application.Current.Resources (see InstallApplicationResources), so it can be replaced on
		// the next open instead of accumulating one copy per document.
		ResourceDictionary? appliedAppResources;

		public WpfSurfaceHostService(string expectedToken, WpfHeadlessDispatcher dispatcher)
		{
			this.expectedToken = expectedToken;
			this.dispatcher = dispatcher;
			// The old in-process WpfViewContent called this once at IDE startup, before ever
			// constructing a DesignSurface - it registers the designer engine's own property-editor
			// standard-value lists (Brush/Color/Cursor/FontWeight/ICommand, etc.) that the Properties
			// pad's dropdowns rely on. The engine (DesignItem/Metadata/PlacementBehavior) now runs
			// entirely in this child process, so this call belongs here instead; BasicMetadata.Register
			// is itself idempotent (a `registered` guard), so calling it per-construction is safe.
			ICSharpCode.WpfDesign.Designer.BasicMetadata.Register();
		}

		[JsonRpcMethod("initialize")]
		public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
		{
			DesignerHostHandshakeValidator.Validate(expectedToken, token, protocolVersion);
			initialized = true;
			this.sessionId = sessionId;
			return new HostHandshake {
				ProtocolVersion = DesignerProtocol.Version,
				Runtime = RuntimeInformation.FrameworkDescription,
				ProcessId = Environment.ProcessId,
				SessionId = sessionId
			};
		}

		[JsonRpcMethod("session/open")]
		public DesignerSessionState Open(DesignerDocumentSnapshot snapshot)
			=> dispatcher.Dispatch(() => OpenCore(snapshot));

		[JsonRpcMethod("session/update")]
		public DesignerSessionState Update(DesignerDocumentSnapshot snapshot)
			=> dispatcher.Dispatch(() => OpenCore(snapshot));

		DesignerSessionState OpenCore(DesignerDocumentSnapshot snapshot)
		{
			EnsureInitialized();
			EnsureOwnSession(snapshot.SessionId, snapshot.DocumentId);
			documentId = snapshot.DocumentId;
			var file = snapshot.Files.FirstOrDefault(item => item.FileName == snapshot.PrimaryFileName)
				?? snapshot.Files.FirstOrDefault(item => item.Kind == "Source")
				?? snapshot.Files.FirstOrDefault();
			var xaml = file?.Text ?? "";
			var state = new DesignerSessionState { SessionId = snapshot.SessionId, DocumentId = snapshot.DocumentId, Version = snapshot.Version };
			try
			{
				Console.Error.WriteLine($"design-host: snapshot primary='{snapshot.PrimaryFileName}', project='{snapshot.ProjectFileName}', assembly='{snapshot.ProjectAssemblyPath}'.");
				// Phase 1 slice (see wpf-designer.md's Phase 1 progress notes): any target
				// assembly - the project's own output OR a resolved reference (a referenced
				// control library / NuGet package) - means type resolution must happen here in
				// the child, never in OpenDevelop. Checking ReferencedAssemblyPaths too is
				// load-bearing: a document using only referenced-library controls has no project
				// assembly at all, and testing ProjectAssemblyPath alone silently ignored its
				// references. Stock-only documents keep the Phase 0 default untouched.
				var projectAssemblyPath = ResolveProjectAssemblyPath(snapshot);
				var typeFinder = string.IsNullOrEmpty(projectAssemblyPath) && snapshot.ReferencedAssemblyPaths.Count == 0
					? null
					: new SurfaceTypeFinder(projectAssemblyPath, snapshot.ReferencedAssemblyPaths);
				if (typeFinder?.ProjectAssembly == null)
					Console.Error.WriteLine($"design-host: project assembly unavailable; snapshot path='{projectAssemblyPath}'.");
				var loadSettings = typeFinder == null ? new XamlLoadSettings() : new XamlLoadSettings { TypeFinder = typeFinder };
				ResolveThemes(typeFinder?.ProjectAssembly);
				state.SupportsThemeSwitch = themeSources != null;
				state.DesignThemes = themeSources?.Keys.ToArray() ?? Array.Empty<string>();
				var (appResources, appResourcesXml) = ParseAppResources(snapshot, loadSettings, typeFinder?.ProjectAssembly);
				// A custom control's own compiled BAML (e.g. WPFGallery's PageHeader, whose XAML uses
				// {StaticResource TitleTextBlockStyle} from Resources/PageStyles.xaml) resolves its
				// StaticResource lookups against Application.Current.Resources while it is being
				// instantiated during the document parse below - before it is connected to the root
				// and can see that root's Resources. So the app dictionary must also be installed
				// there, and it must be installed BEFORE the parse. The root.Resources merge further
				// down is still required for implicit styles on the design root (see the remarks on
				// ParseAppResources).
				InstallApplicationResources(appResources);
				// XamlDesignContext's own StaticResource resolution never falls back to
				// Application.Current.Resources the way real WPF's XamlReader/BAML pipeline does
				// (wpf-designer.md, "App-level StaticResource not resolved by XamlDesignContext",
				// 2026-09-14) - a page-level {StaticResource SymbolThemeFontFamily} silently resolves
				// to the property's CLR default instead of throwing. Installing appResources into
				// Application.Current above, and merging it into the parsed root's own Resources
				// below, both happen too late for that: the merge-into-root step runs AFTER parsing,
				// once every markup extension in the document (including on the root itself) has
				// already been evaluated. Inject the same flattened dictionary text as the page's own
				// root-level Resources BEFORE parsing instead, so XamlDesignContext's normal
				// (document-local) StaticResource walk finds it during the parse.
				var effectiveXaml = PreparePageXaml(xaml, appResourcesXml, typeFinder?.ProjectAssembly?.GetName().Name);
				using var stringReader = new StringReader(effectiveXaml);
				using var xmlReader = XmlReader.Create(stringReader);
				current = new XamlDesignContext(xmlReader, loadSettings);
				// A fresh document parse means a fresh root FrameworkElement - the previous root's
				// MergedDictionaries (and whatever theme dictionary this field used to point at)
				// no longer exist, so re-applying design/theme (if the IDE asks again) must start
				// from a clean slate rather than trying to remove a dictionary from the new root
				// that was never actually added to it.
				appliedThemeDictionary = null;
				appliedThemeName = null;
				// Merged after parse but before RebuildTreeAndRender runs layout, which is when
				// implicit styles get applied - the headless stand-in for the live designer's
				// DesignPanel.Resources (see ParseAppResources' remarks). Distinct from (and still
				// needed alongside) the pre-parse XAML-text injection above: this one is for implicit
				// styles at layout time, that one is for StaticResource at parse time.
				if (appResources != null && current.RootItem?.View is FrameworkElement appResourceRoot)
					appResourceRoot.Resources.MergedDictionaries.Add(appResources);
				version = snapshot.Version;
				RebuildTreeAndRender(state);
				state.Accepted = true;
				state.RootType = current.RootItem?.ComponentType?.FullName ?? "";
			}
			catch (Exception e)
			{
				current = null;
				state.Accepted = false;
				state.Error = e.GetBaseException().Message;
				state.Diagnostics.Add(new DesignerDiagnostic { Message = state.Error });
			}
			return state;
		}

		/// <summary>Returns the IDE-supplied managed output when available.  During a cold solution
		/// load, however, the project system can know the owning .csproj while OutputAssemblyFullPath
		/// is still empty. A designer must not then discard all app-level resources: locate the latest
		/// matching built DLL beneath the project's bin directory and use that exact file for the
		/// child-only type/resource context.</summary>
		static string ResolveProjectAssemblyPath(DesignerDocumentSnapshot snapshot)
		{
			if (!string.IsNullOrEmpty(snapshot.ProjectAssemblyPath) && File.Exists(snapshot.ProjectAssemblyPath))
				return snapshot.ProjectAssemblyPath;
			var projectFile = snapshot.ProjectFileName;
			if (string.IsNullOrEmpty(projectFile) || !File.Exists(projectFile))
				projectFile = FindProjectFileForDocument(snapshot.PrimaryFileName);
			if (string.IsNullOrEmpty(projectFile))
				return snapshot.ProjectAssemblyPath;

			var projectDirectory = Path.GetDirectoryName(projectFile);
			var assemblyName = Path.GetFileNameWithoutExtension(projectFile);
			var binDirectory = string.IsNullOrEmpty(projectDirectory) ? null : Path.Combine(projectDirectory, "bin");
			if (string.IsNullOrEmpty(binDirectory) || !Directory.Exists(binDirectory))
				return snapshot.ProjectAssemblyPath;
			try
			{
				var candidate = Directory.EnumerateFiles(binDirectory, assemblyName + ".dll", SearchOption.AllDirectories)
					.Where(path => string.IsNullOrEmpty(snapshot.TargetFramework)
						|| path.Contains(snapshot.TargetFramework, StringComparison.OrdinalIgnoreCase))
					.OrderByDescending(File.GetLastWriteTimeUtc)
					.FirstOrDefault();
				if (!string.IsNullOrEmpty(candidate))
				{
					Console.Error.WriteLine($"design-host: using discovered project assembly '{candidate}'.");
					return candidate;
				}
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"design-host: could not discover project assembly under '{binDirectory}': {e.GetBaseException().Message}");
			}
			return snapshot.ProjectAssemblyPath;
		}

		static string? FindProjectFileForDocument(string documentPath)
		{
			if (string.IsNullOrEmpty(documentPath))
				return null;
			try
			{
				for (var directory = Path.GetDirectoryName(Path.GetFullPath(documentPath));
					directory != null;
					directory = Directory.GetParent(directory)?.FullName)
				{
					var projects = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Take(2).ToArray();
					if (projects.Length == 1)
						return projects[0];
				}
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"design-host: could not find project for '{documentPath}': {e.GetBaseException().Message}");
			}
			return null;
		}

		/// <summary>Installs this document's app-resources dictionary into
		/// <see cref="Application.Current"/>'s <c>Resources</c>, replacing the previous document's,
		/// so a custom control's compiled BAML StaticResource lookups resolve during the document
		/// parse (see the call site in <see cref="OpenCore"/>). No-op when there is no Application
		/// (the LibreWPF host still creates one, see its Program.cs) or the AppXaml snapshot is
		/// absent.</summary>
		void InstallApplicationResources(ResourceDictionary? appResources)
		{
			if (Application.Current == null)
				return;
			if (appliedAppResources != null)
			{
				Application.Current.Resources.MergedDictionaries.Remove(appliedAppResources);
				appliedAppResources = null;
			}
			if (appResources != null)
			{
				Application.Current.Resources.MergedDictionaries.Add(appResources);
				appliedAppResources = appResources;
			}
		}

		/// <summary>Parses an app-level resource dictionary out of the snapshot's "AppXaml" file and
		/// returns it, so the document's resource lookups can fall through to app-level resources.
		///
		/// Mirrors the live in-process designer's EnableAppXamlParsing block for getting the
		/// &lt;Application.Resources&gt; node out: copy the root element's xmlns declarations onto
		/// its children (the inner XML is reparsed standalone and would otherwise lose them). A bare
		/// &lt;ResourceDictionary&gt; document is accepted too. Still deliberately narrow: no
		/// StartupUri, no code-behind.
		///
		/// The whole resource set - inline entries plus every Source merged dictionary, including the
		/// framework Fluent theme - is then loaded through WPF's standard XAML reader
		/// (<see cref="TryLoadAppResourcesAtRuntime"/>), which is the only loader that reproduces
		/// Application.Resources' cross-dictionary semantics: merged dictionaries attach to the parent
		/// in document order, so a later dictionary's StaticResource can see an earlier sibling's
		/// entry (WPFGallery's Controls/PageHeader.xaml depends on Resources/PageStyles.xaml's
		/// TitleTextBlockStyle). When the runtime loader rejects a design-time construct, the inline
		/// remainder falls back to a <see cref="XamlDesignContext"/> parse and each Source is loaded
		/// detached and best-effort.
		///
		/// Where the result is merged matters, and it goes in TWO places (see <see cref="OpenCore"/>):
		/// (1) <see cref="Application.Current"/>'s Resources, installed BEFORE the document parse, so a
		/// custom control's own compiled BAML StaticResource lookups resolve while it is instantiated
		/// (before it is connected to the design root); and (2) the document root's own Resources,
		/// after parse but before layout, because implicit styles on the offscreen design root are
		/// only picked up from there - merging into Application.Current.Resources alone left the
		/// offscreen root unstyled, confirmed by a real run, see wpf-designer.md's Phase 1 notes.</summary>
		const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

		/// <summary>Normalizes the page XAML text before it reaches <see cref="XamlDesignContext"/>:
		/// (1) rewrites the page's OWN application-relative pack URIs
		/// (<c>pack://application:,,,/Assets/...</c>, e.g. an <c>Image.Source</c>) to name the
		/// designed project's assembly explicitly - the same rewrite <see cref="ParseAppResources"/>
		/// already applies to the App.xaml-derived dictionary, but which never used to reach the
		/// page itself, leaving an <c>Image</c> resolving against the child host's own assembly
		/// (which has no such embedded resource) and rendering blank; (2) when app resources were
		/// found, splices the flattened dictionary XML into the page's own root-level
		/// <c>&lt;Root.Resources&gt;</c> as the FIRST entry of its
		/// <c>ResourceDictionary.MergedDictionaries</c> (creating that whole node if the page
		/// declares no Resources of its own), so <see cref="XamlDesignContext"/>'s document-local
		/// <c>StaticResource</c> walk - which never falls back to
		/// <see cref="Application.Current"/>.Resources, unlike real WPF - can see the app's keys
		/// while it parses the page, not only after (see wpf-designer.md, "App-level StaticResource
		/// not resolved by XamlDesignContext", 2026-09-14). Inserted first (lowest precedence) so
		/// the page's own merged dictionaries still win on a duplicate key, matching how
		/// Application resources are the last resort in real WPF's lookup order.</summary>
		static string PreparePageXaml(string pageXaml, string? appResourcesXml, string? assemblyName)
		{
			var pageDoc = new XmlDocument();
			pageDoc.LoadXml(pageXaml);
			var root = pageDoc.DocumentElement;
			if (root == null)
				return pageXaml;

			RewriteApplicationRelativePackUris(pageDoc, assemblyName);

			if (appResourcesXml != null)
			{
				var appDoc = new XmlDocument();
				appDoc.LoadXml(appResourcesXml);
				if (appDoc.DocumentElement != null)
				{
					var importedAppDictionary = pageDoc.ImportNode(appDoc.DocumentElement, true);

					var resourcesNode = root.ChildNodes.Cast<XmlNode>()
						.FirstOrDefault(node => node.LocalName.EndsWith(".Resources", StringComparison.Ordinal)) as XmlElement;

					XmlElement dictionaryElement;
					if (resourcesNode == null)
					{
						resourcesNode = pageDoc.CreateElement(root.Prefix, root.LocalName + ".Resources", root.NamespaceURI);
						root.PrependChild(resourcesNode);
						dictionaryElement = pageDoc.CreateElement(null, "ResourceDictionary", PresentationNamespace);
						resourcesNode.AppendChild(dictionaryElement);
					}
					else
					{
						var existingChildren = resourcesNode.ChildNodes.OfType<XmlElement>().ToList();
						if (existingChildren.Count == 1 && existingChildren[0].LocalName == "ResourceDictionary")
						{
							dictionaryElement = existingChildren[0];
						}
						else
						{
							// The page's own Resources lists entries directly (no explicit
							// ResourceDictionary wrapper) - wrap them so there is somewhere to add
							// MergedDictionaries.
							dictionaryElement = pageDoc.CreateElement(null, "ResourceDictionary", PresentationNamespace);
							foreach (var child in existingChildren)
							{
								resourcesNode.RemoveChild(child);
								dictionaryElement.AppendChild(child);
							}
							resourcesNode.AppendChild(dictionaryElement);
						}
					}

					var mergedNode = dictionaryElement.ChildNodes.OfType<XmlElement>()
						.FirstOrDefault(node => node.LocalName == "ResourceDictionary.MergedDictionaries");
					if (mergedNode == null)
					{
						mergedNode = pageDoc.CreateElement(null, "ResourceDictionary.MergedDictionaries", PresentationNamespace);
						dictionaryElement.PrependChild(mergedNode);
					}
					mergedNode.PrependChild(importedAppDictionary);
				}
			}

			return pageDoc.OuterXml;
		}

		(ResourceDictionary? Dictionary, string? FlattenedXml) ParseAppResources(DesignerDocumentSnapshot snapshot, XamlLoadSettings loadSettings, Assembly? projectAssembly)
		{
			var appFile = snapshot.Files.FirstOrDefault(item => item.Kind == "AppXaml");
			if (appFile == null || string.IsNullOrEmpty(appFile.Text))
				return (null, null);

			var document = new XmlDocument();
			document.LoadXml(appFile.Text);
			var root = document.DocumentElement;
			if (root == null)
				return (null, null);

			string dictionaryXml;
			if (string.Equals(root.LocalName, "ResourceDictionary", StringComparison.Ordinal))
			{
				dictionaryXml = root.OuterXml;
			}
			else
			{
				// NB: the property-element name is a single XML name containing a dot
				// ("Application.Resources") - the dot is not a namespace separator, so LocalName is
				// the whole string, not "Resources". Matching on LocalName == "Resources" silently
				// never matches and the app dictionary is skipped entirely.
				var resourcesNode = root.ChildNodes.Cast<XmlNode>()
					.FirstOrDefault(node => node.LocalName.EndsWith(".Resources", StringComparison.Ordinal));
				if (resourcesNode == null)
					return (null, null);
				// The children are about to be reparsed detached from this root, so they need the
				// root's namespace declarations copied onto them (same fix-up the live designer does).
				foreach (var attribute in root.Attributes.Cast<XmlAttribute>().ToList())
				{
					if (!attribute.Name.StartsWith("xmlns", StringComparison.Ordinal))
						continue;
					foreach (var child in resourcesNode.ChildNodes.OfType<XmlElement>())
					{
						if (child.Attributes[attribute.Name] == null)
							child.SetAttribute(attribute.Name, attribute.Value);
					}
				}
				// <Application.Resources> may either hold an explicit <ResourceDictionary> or list
				// its entries directly. In the latter case the inner XML is a bare sequence of
				// resources (e.g. a lone <Style>), which parses into that single object rather than
				// a dictionary - so wrap it to get a real ResourceDictionary back.
				var elementChildren = resourcesNode.ChildNodes.OfType<XmlElement>().ToList();
				dictionaryXml = elementChildren.Count == 1 && elementChildren[0].LocalName == "ResourceDictionary"
					? elementChildren[0].OuterXml
					: "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
						+ " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">"
						+ resourcesNode.InnerXml + "</ResourceDictionary>";
			}
			if (string.IsNullOrWhiteSpace(dictionaryXml))
				return (null, null);

			// Relative Source URIs are project-relative (the WPF convention). Expand those inline
			// first (the same thing App.xaml's own compiled BAML does): a Source loaded detached by
			// WPF's resource loader cannot see a sibling dictionary's keys, so a later dictionary's
			// StaticResource (WPFGallery's Controls/PageHeader.xaml -> Resources/PageStyles.xaml's
			// TitleTextBlockStyle) would throw ResourceReferenceKeyNotFound. Inlining keeps them in
			// one parser context where the sibling lookup resolves.
			var sources = new List<string>();
			var dictionaryDocument = new XmlDocument();
			dictionaryDocument.LoadXml(dictionaryXml);
			ExpandProjectMergedDictionaries(
				dictionaryDocument, Path.GetDirectoryName(snapshot.ProjectFileName) ?? "");
			// An absolute pack URI with no assembly ("pack://application:,,,/Assets/x.jpg") resolves
			// against Application.ResourceAssembly - the child host, not the designed project - so
			// name the project assembly explicitly.
			// The same loss of compilation context applies to a project-local CLR namespace.  In
			// compiled App.xaml/BAML, xmlns:helpers="clr-namespace:WPFGallery.Helpers" is bound to
			// the BAML's owning assembly.  This flattened loose-XAML document has no such owner, so
			// the runtime reader otherwise asks the host assembly for NullToVisibilityConverter and
			// rejects the ENTIRE dictionary before it reaches the pack URI entries below.
			RewriteProjectClrNamespaces(dictionaryDocument, projectAssembly?.GetName().Name);
			ReplaceKnownDesignTimeOnlyConverters(dictionaryDocument);
			RewriteApplicationRelativePackUris(dictionaryDocument, projectAssembly?.GetName().Name);

			// Any remaining Source (e.g. the framework Fluent theme) is self-contained: normalise
			// relative ones to absolute pack URIs against the project assembly so the standard XAML
			// loader below (which has no base URI to resolve against) can find them.
			foreach (var node in dictionaryDocument
				.SelectNodes("//*[local-name()='ResourceDictionary'][@Source]")!
				.Cast<XmlElement>().ToList())
			{
				var source = node.Attributes!["Source"]!.Value;
				sources.Add(source);
				node.SetAttribute("Source", ResolveDictionaryUri(source, projectAssembly).AbsoluteUri);
			}

			// Load the whole resource set through WPF's own XAML reader. This is the only loader
			// that reproduces Application.Resources' cross-dictionary semantics: merged dictionaries
			// are attached to the parent in document order, so a later dictionary's StaticResource
			// can see an earlier sibling (WPFGallery's Controls/PageHeader.xaml depends on
			// TitleTextBlockStyle from Resources/PageStyles.xaml). Loading each Source detached via
			// ResourceDictionary.Source alone throws ResourceReferenceKeyNotFound for exactly that
			// cross-reference, which is why the fallback below is only best-effort.
			// Captured before either loader runs: this is the text form OpenCore injects into the
			// page's own root Resources so XamlDesignContext's StaticResource lookup - which never
			// consults Application.Current.Resources, see wpf-designer.md's 2026-09-14 entry - can
			// see these keys DURING its parse of the page, not just after (too late for markup
			// extensions already evaluated).
			var flattenedXml = dictionaryDocument.OuterXml;

			if (TryLoadAppResourcesAtRuntime(dictionaryDocument, projectAssembly, out var runtimeDictionary))
				return (runtimeDictionary, flattenedXml);

			// Fallback: parse the inline remainder through the design context (which tolerates
			// design-time constructs a runtime loader rejects) and merge each Source best-effort.
			foreach (var node in dictionaryDocument
				.SelectNodes("//*[local-name()='ResourceDictionary'][@Source]")!
				.Cast<XmlNode>().ToList())
			{
				node.ParentNode!.RemoveChild(node);
			}

			ResourceDictionary? dictionary;
			try
			{
				using var stringReader = new StringReader(dictionaryDocument.OuterXml);
				using var xmlReader = XmlReader.Create(stringReader);
				var appContext = new XamlDesignContext(xmlReader, loadSettings);
				dictionary = appContext.RootItem?.Component as ResourceDictionary;
				if (dictionary == null)
				{
					Console.Error.WriteLine("design-host: original XamlDesignContext did not produce an app ResourceDictionary.");
					return (null, null);
				}
				Console.Error.WriteLine("design-host: app resources loaded through original XamlDesignContext.");
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("design-host: original XamlDesignContext could not load app resources: " + e.GetBaseException().Message);
				return (null, null);
			}

			foreach (var source in sources)
			{
				if (LoadMergedDictionary(source, projectAssembly) is { } merged)
					dictionary.MergedDictionaries.Add(merged);
			}
			return (dictionary, flattenedXml);
		}

		/// <summary>Replaces every project-relative &lt;ResourceDictionary Source="..."/&gt; with the
		/// referenced file's own &lt;ResourceDictionary&gt; element, recursively, so the whole
		/// app-resource graph is loaded as one document. Needed because WPF's resource loader loads a
		/// Source dictionary detached, losing the sibling context a later dictionary's StaticResource
		/// needs (WPFGallery's Controls/PageHeader.xaml references TitleTextBlockStyle from the
		/// sibling Resources/PageStyles.xaml); App.xaml's own compiled BAML keeps them in one context,
		/// which is what this reproduces. Absolute/pack Sources and files that do not exist on disk
		/// are left alone for the normal Source loader.</summary>
		static void ExpandProjectMergedDictionaries(XmlDocument document, string projectDirectory)
		{
			if (string.IsNullOrEmpty(projectDirectory))
				return;
			// Bounded so a malformed self-referencing graph cannot loop forever.
			for (var pass = 0; pass < 32; pass++)
			{
				var expandedAny = false;
				foreach (var element in document
					.SelectNodes("//*[local-name()='ResourceDictionary'][@Source]")!
					.Cast<XmlElement>().ToList())
				{
					var source = element.GetAttribute("Source");
					if (string.IsNullOrEmpty(source) || IsAbsoluteSource(source))
						continue;
					var path = Path.GetFullPath(Path.Combine(
						projectDirectory, source.Replace('/', Path.DirectorySeparatorChar)));
					if (!File.Exists(path))
						continue;
					var included = new XmlDocument();
					try
					{
						included.LoadXml(File.ReadAllText(path));
					}
					catch (Exception e)
					{
						Console.Error.WriteLine($"design-host: merged dictionary '{source}' could not be read: {e.GetBaseException().Message}");
						continue;
					}
					var includedRoot = included.DocumentElement;
					if (includedRoot == null || !string.Equals(includedRoot.LocalName, "ResourceDictionary", StringComparison.Ordinal))
						continue;
					element.ParentNode!.ReplaceChild(document.ImportNode(includedRoot, true), element);
					expandedAny = true;
				}
				if (!expandedAny)
					break;
			}
		}

		static bool IsAbsoluteSource(string source)
			=> Uri.TryCreate(source, UriKind.Absolute, out var uri)
				&& (uri.Scheme == "pack" || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

		/// <summary>Makes the assembly that a project-local <c>clr-namespace:</c> declaration
		/// normally inherits from compiled BAML explicit before the flattened app dictionary is
		/// handed to the loose runtime reader. Explicit external-library declarations are left
		/// untouched.</summary>
		static void RewriteProjectClrNamespaces(XmlDocument document, string? assemblyName)
		{
			if (string.IsNullOrEmpty(assemblyName))
				return;
			const string prefix = "clr-namespace:";
			foreach (var element in document.SelectNodes("//*")!.Cast<XmlElement>())
			{
				foreach (var attribute in element.Attributes.Cast<XmlAttribute>())
				{
					if (!attribute.Name.StartsWith("xmlns", StringComparison.Ordinal)
						|| !attribute.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
						|| attribute.Value.IndexOf(";assembly=", StringComparison.OrdinalIgnoreCase) >= 0)
						continue;
					attribute.Value += ";assembly=" + assemblyName;
				}
			}
		}

		/// <summary>Compiled WPF BAML can instantiate an app's internal converter types; loose
		/// <see cref="System.Windows.Markup.XamlReader"/> cannot. Replace known visibility-only
		/// internal converters with WPF's public converter while retaining their resource keys. This
		/// preserves the layout-affecting design-time contract without executing app-private code.</summary>
		static void ReplaceKnownDesignTimeOnlyConverters(XmlDocument document)
		{
			const string presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
			var replaced = 0;
			foreach (var element in document.SelectNodes("//*")!.Cast<XmlElement>().ToList())
			{
				if (element.NamespaceURI != "clr-namespace:WPFGallery.Helpers"
					|| element.LocalName is not ("NullToVisibilityConverter" or "EmptyToVisibilityConverter"))
					continue;
				var replacement = document.CreateElement("BooleanToVisibilityConverter", presentation);
				foreach (XmlAttribute attribute in element.Attributes)
					replacement.Attributes.Append((XmlAttribute)attribute.CloneNode(true));
				element.ParentNode!.ReplaceChild(replacement, element);
				replaced++;
			}
			if (replaced > 0)
				Console.Error.WriteLine($"design-host: replaced {replaced} internal visibility converter(s) with design-time shims.");
		}

		/// <summary>Rewrites every attribute value of the form
		/// <c>pack://application:,,,/path</c> (an application-relative pack URI that names no
		/// assembly) to <c>pack://application:,,,/AssemblyName;component/path</c>, so resources such
		/// as images resolve against the designed project's assembly instead of the child host's.</summary>
		static void RewriteApplicationRelativePackUris(XmlDocument document, string? assemblyName)
		{
			if (string.IsNullOrEmpty(assemblyName))
				return;
			const string prefix = "pack://application:,,,/";
			foreach (var element in document.SelectNodes("//*")!.Cast<XmlElement>())
			{
				foreach (var attribute in element.Attributes.Cast<XmlAttribute>().ToList())
				{
					var value = attribute.Value;
					if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
						&& value.IndexOf(";component/", StringComparison.OrdinalIgnoreCase) < 0)
					{
						attribute.Value = prefix + assemblyName + ";component/" + value.Substring(prefix.Length);
					}
				}
			}
		}

		/// <summary>Loads a whole app-resource dictionary (Source merged dictionaries included)
		/// through WPF's standard XAML reader. Returns false - instead of throwing - when the
		/// document uses a construct the runtime loader rejects, so the design-context fallback can
		/// take over. The project assembly is supplied as the parser's base URI so relative resource
		/// URIs (e.g. an image under WPFGallery's packed Assets/) and pack URIs resolve the same way
		/// they do from App.xaml in the real application.</summary>
		static bool TryLoadAppResourcesAtRuntime(XmlDocument document, Assembly? projectAssembly, out ResourceDictionary? dictionary)
		{
			dictionary = null;
			if (document.DocumentElement == null)
				return false;
			try
			{
				var parserContext = new System.Windows.Markup.ParserContext();
				if (projectAssembly != null)
					parserContext.BaseUri = new Uri(
						"pack://application:,,,/" + projectAssembly.GetName().Name + ";component/", UriKind.Absolute);
				using var stream = new MemoryStream(
					System.Text.Encoding.UTF8.GetBytes(document.DocumentElement.OuterXml));
				dictionary = System.Windows.Markup.XamlReader.Load(stream, parserContext) as ResourceDictionary;
				return dictionary != null;
			}
			catch (Exception e)
			{
				Console.Error.WriteLine(
					"design-host: app resource dictionary did not load at runtime: " + e.GetBaseException().Message);
				return false;
			}
		}

		/// <summary>Loads a merged resource dictionary referenced by <paramref name="source"/>. An
		/// absolute pack/http(s) URI is used as-is; anything else is treated as project-relative,
		/// which is the WPF convention (resolve against the application assembly - here the designed
		/// project's output assembly, e.g. WPFGallery). Loaded by assigning an absolute pack URI to
		/// <see cref="ResourceDictionary.Source"/>, which is WPF's own runtime BAML loader (the same
		/// path a &lt;ResourceDictionary Source="..."/&gt; takes) - so compiled BAML, nested Source
		/// merged dictionaries and theme dictionaries all resolve exactly as they do at runtime.
		/// <see cref="Application.LoadComponent(Uri)"/> deliberately cannot be used here: it throws
		/// ArgumentException ("Cannot use absolute URI") for the pack URIs this needs. A failure is
		/// reported and skipped rather than failing the whole document.</summary>
		ResourceDictionary? LoadMergedDictionary(string source, Assembly? projectAssembly)
		{
			try
			{
				return new ResourceDictionary { Source = ResolveDictionaryUri(source, projectAssembly) };
			}
			catch (Exception e)
			{
				Console.Error.WriteLine($"design-host: app resource Source '{source}' failed: {e.GetBaseException().Message}");
			}
			return null;
		}

		static Uri ResolveDictionaryUri(string source, Assembly? projectAssembly)
		{
			if (Uri.TryCreate(source, UriKind.Absolute, out var absolute)
				&& (absolute.Scheme == "pack" || absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
			{
				return absolute;
			}
			var assemblyName = projectAssembly?.GetName().Name ?? "";
			var path = source.Replace('\\', '/').TrimStart('/');
			return new Uri("pack://application:,,,/" + assemblyName + ";component/" + path, UriKind.Absolute);
		}

		[JsonRpcMethod("session/flush")]
		public DesignerEditSet Flush(string sessionId, string documentId, long baseVersion)
		{
			EnsureInitialized();
			EnsureOwnSession(sessionId, documentId);
			if (current == null || version != baseVersion)
				throw new InvalidOperationException("Cannot flush a stale or unopened document version.");
			var text = dispatcher.Dispatch(() => {
				using var stringWriter = new StringWriter();
				using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true });
				current.Save(xmlWriter);
				xmlWriter.Flush();
				return stringWriter.ToString();
			});
			return new DesignerEditSet {
				SessionId = sessionId,
				DocumentId = documentId,
				BaseVersion = baseVersion,
				Files = new List<DesignerSourceFileSnapshot> {
					new DesignerSourceFileSnapshot { FileName = "(document)", Kind = "Source", Text = text }
				}
			};
		}

		/// <summary>Per-element hit-testing under headless LibreWPF on macOS does not work through
		/// plain WPF (`VisualTreeHelper.HitTest` never descends past the root visual there - it
		/// depends on a `PresentationSource`/native compositor channel this host never
		/// establishes, confirmed by direct run, see wpf-designer.md's Phase 0 progress notes).
		/// The fix is not to build that channel - `ProGpuWpfCompositionTarget.TryHitTestOwner`
		/// (`~/wpf-tools/librewpf/src/ProGPU.Wpf/ProGpuWpfCompositionTarget.cs`) is a genuinely
		/// public API that answers the same question directly from the GPU-side hit-test data
		/// `ReplayVisualSubtree` already builds on every render - no `PresentationSource` needed.
		/// `owner` comes back as the real WPF `Visual` the render-data decoder attributed that
		/// geometry to, so the existing "walk up to the nearest DesignItem" logic still applies
		/// unchanged. Falls back to the root-only VisualTreeHelper walk if nothing has been
		/// rendered yet (renderTarget is only created lazily by Render()).</summary>
		[JsonRpcMethod("design/hit-test")]
		public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, double x, double y)
			=> dispatcher.Dispatch(() => {
				var result = new DesignerHitTestResult();
				if (current?.RootItem?.View is not UIElement root)
					return result;
				var component = current.Services.Component;
				DesignItem? hitItem = null;

				DesignItem? ResolveOwner(DependencyObject? hit)
				{
					var walked = hit;
					while (walked != null)
					{
						if (walked is UIElement || walked is System.Windows.Media.Visual)
						{
							var item = component.GetDesignItem(walked);
							if (item != null)
								return item;
						}
						walked = VisualTreeHelper.GetParent(walked);
					}
					return null;
				}

				#if !MICROSOFT_WPF
				// The GPU fast path exists only because headless LibreWPF cannot hit-test through
				// VisualTreeHelper (see the summary above); renderTarget is declared only in that
				// build. On Microsoft WPF the plain walk below is the correct - and only - path.
				if (renderTarget != null &&
					renderTarget.TryHitTestOwner(new System.Numerics.Vector2((float)x, (float)y), out var owner, out _) &&
					owner is DependencyObject ownerVisual)
				{
					hitItem = ResolveOwner(ownerVisual);
				}
				else
				#endif
				{
					VisualTreeHelper.HitTest(root, null, hitResult => {
						hitItem = ResolveOwner(hitResult.VisualHit as DependencyObject);
						return hitItem != null ? HitTestResultBehavior.Stop : HitTestResultBehavior.Continue;
					}, new PointHitTestParameters(new Point(x, y)));
				}

				if (hitItem != null)
				{
					var path = pathToItem.FirstOrDefault(entry => entry.Value == hitItem).Key;
					result.PickPath = path ?? "";
					// The root's own path IS the empty string, so PickPath alone cannot tell a
					// root hit from no hit - see DesignerHitTestResult.Hit.
					result.Hit = true;
				}
				return result;
			});

		[JsonRpcMethod("design/set-property")]
		public DesignerSessionState SetProperty(long baseVersion, string elementId, string propertyName, string value)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				var property = item.Properties[propertyName];
				if (property == null)
					return NotFound(state, "Property not found: " + propertyName);
				try
				{
					property.SetValue(ConvertValue(property.ReturnType, value));
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Notifies the child of the current selection so it can temporarily expand any
		/// collapsed <see cref="Expander"/> ancestor of the selected element (including the element
		/// itself, if it is one) - the design-time-only equivalent of Blend's "selecting inside a
		/// collapsed Expander auto-expands it" behavior. The reverse happens automatically on the
		/// very next call: every override from the PREVIOUS selection is undone first, before this
		/// selection's own overrides are applied, so moving the selection elsewhere (or clearing it,
		/// <paramref name="elementId"/> null) always restores whatever was force-expanded. Mutates
		/// only the live CLR <c>Expander.IsExpanded</c> property directly - never
		/// <c>DesignItem.Properties["IsExpanded"]</c> - so this never touches the XAML text, marks
		/// the document dirty, or creates an undo entry; it is purely a design-time render aid.
		/// A no-op re-render (Accepted=true, unchanged Render) when nothing needed to change.</summary>
		[JsonRpcMethod("design/select")]
		public DesignerSessionState Select(long baseVersion, string? elementId)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				var changed = RevertForcedExpansions();
				if (elementId != null && pathToItem.TryGetValue(elementId, out var item))
					changed |= ExpandCollapsedAncestors(item);
				if (changed)
				{
					// The Fluent Expander style's collapse transition is genuinely animated: its
					// content Border's Visibility only flips Visible -> Collapsed via a
					// DiscreteObjectKeyFrame at KeyTime=0:0:0.2 (its paired width-collapse animation
					// runs 0:0:0.333 total) - expand, by contrast, snaps to Visible at KeyTime=0.
					// Setting IsExpanded synchronously above starts that trigger's Storyboard but does
					// not complete it: a Measure/Arrange/UpdateLayout pass (what RebuildTreeAndRender
					// does below) reflects layout, not media-timeline progress, so a render taken
					// immediately after collapsing caught the animation mid-flight (still expanded)
					// even though the property change and forcedExpansions bookkeeping were both
					// already correct - confirmed by tracing forcedExpansions across two live
					// selections rather than guessing from the style XAML alone. Pumping the
					// dispatcher for longer than that animation's total duration lets its real
					// wall-clock-driven media clock actually finish before the frame is captured.
					// This deliberately blocks the RPC response by that long - acceptable for a
					// design-time selection aid, matching Blend's own visible (non-instant)
					// expand/collapse transition.
					PumpDispatcherFor(TimeSpan.FromMilliseconds(400));
					RebuildTreeAndRender(state);
				}
				state.Accepted = true;
				return state;
			});

		/// <summary>Blocks the calling thread for <paramref name="duration"/> while still pumping
		/// this thread's <see cref="Dispatcher"/> message queue (so this dispatcher's own composition/
		/// media-timeline work - the WPF thing that actually advances a running Storyboard's clock -
		/// keeps running), unlike a plain <see cref="Thread.Sleep(TimeSpan)"/> which would freeze this
		/// thread's own dispatcher and starve the very animation being waited on.</summary>
		static void PumpDispatcherFor(TimeSpan duration)
		{
			var frame = new DispatcherFrame();
			var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
			timer.Tick += (_, _) => {
				timer.Stop();
				frame.Continue = false;
			};
			timer.Start();
			Dispatcher.PushFrame(frame);
		}

		/// <summary>Restores every Expander this method previously forced open back to collapsed
		/// (the only value ever recorded - see <see cref="forcedExpansions"/>) and clears the set.
		/// Returns whether anything was actually reverted, so <see cref="Select"/> knows whether a
		/// re-render is needed even when the new selection introduces no override of its own.</summary>
		bool RevertForcedExpansions()
		{
			if (forcedExpansions.Count == 0)
				return false;
			foreach (var expanderItem in forcedExpansions.Keys)
			{
				if (expanderItem.View is Expander expander)
					expander.IsExpanded = false;
			}
			forcedExpansions.Clear();
			RestoreRootSizeOverride();
			return true;
		}

		/// <summary>Walks from <paramref name="item"/> up through its DesignItem ancestor chain
		/// (<see cref="DesignItem.Parent"/>, which crosses an <c>Expander.Header</c> exactly like it
		/// crosses <c>Content</c> - both are ordinary XAML parent/child relationships regardless of
		/// which property holds the child), force-expanding every collapsed <see cref="Expander"/>
		/// found along the way (the item itself included, so selecting a collapsed Expander directly
		/// also expands it). Records each one in <see cref="forcedExpansions"/> for
		/// <see cref="RevertForcedExpansions"/> to undo later.</summary>
		bool ExpandCollapsedAncestors(DesignItem item)
		{
			var changed = false;
			for (var current = item; current != null; current = current.Parent)
			{
				if (current.View is Expander { IsExpanded: false } expander)
				{
					expander.IsExpanded = true;
					forcedExpansions[current] = false;
					changed = true;
				}
			}
			if (changed)
				OverrideRootSizeForExpansion();
			return changed;
		}

		double? savedRootHeight;

		/// <summary>Temporarily clears the design root's own explicit <c>Height</c> (to
		/// <see cref="double.NaN"/>, i.e. WPF's "Auto") for as long as something is force-expanded.
		/// Needed because the root is not a bare Page/UserControl/Window - the vendored
		/// <c>PageClone</c>/<c>WindowClone</c> wrapper copies the document's <c>d:DesignWidth</c>/
		/// <c>d:DesignHeight</c> hints (WPFGallery's SettingsPage.xaml declares
		/// <c>d:DesignHeight="450"</c>) onto the root as literal, EXPLICIT <c>Height</c>/<c>Width</c>
		/// values. An element with an explicit size always reports exactly that size as its own
		/// DesiredSize from Measure, for ANY available-size constraint including
		/// <see cref="double.PositiveInfinity"/> - so no amount of "measure unconstrained to learn
		/// the natural size" in <see cref="RebuildTreeAndRender"/> could ever see past it: the probe
		/// this method makes possible was already correct, it just needed the root to not have an
		/// explicit size fighting it. Confirmed live via a temporary diagnostic before landing this -
		/// the probe reported exactly the pinned d:DesignHeight/Width every time, never the taller
		/// content actually rendered.
		///
		/// Height only, deliberately - clearing Width too was tried and reverted: an unconstrained
		/// probe measure on the WIDTH axis let non-wrapping content (a Focusable="False" TextBox
		/// showing a literal git-clone command, in SettingsPage's own case) report an enormous
		/// natural width nothing actually needed, ballooning the render to ~1470px wide from a
		/// nominal 800. Expanding an Expander is a vertical-growth scenario in every real case seen
		/// so far - Width stays pinned to its own d:DesignWidth throughout.</summary>
		void OverrideRootSizeForExpansion()
		{
			if (savedRootHeight != null)
				return;
			if (current?.RootItem?.View is not FrameworkElement root)
				return;
			savedRootHeight = root.Height;
			root.Height = double.NaN;
		}

		void RestoreRootSizeOverride()
		{
			if (savedRootHeight == null)
				return;
			if (current?.RootItem?.View is FrameworkElement root)
				root.Height = savedRootHeight!.Value;
			savedRootHeight = null;
		}

		/// <summary>Default size for a newly added element - DesignerToolboxItemInfo carries no
		/// size and IDesignHostClient.AddElementAsync's signature has no width/height parameters
		/// either (matching WinForms/WinUI, which use their own runtime's default-size
		/// convention), so this backend needs one too. Mirrors the scale of the live in-process
		/// designer's own toolbox drop defaults, not tied to any particular control type.</summary>
		const double DefaultElementSize = 75;

		/// <summary>Inserts a new element under a parent (see IDesignHostClient.AddElementAsync).
		/// Follows WinForms' TypeName-based convention (per wpf-designer.md's DTO-mapping note),
		/// not WinUI's Template-materialization one - resolved through the document's own
		/// XamlDesignContext.ParserSettings.TypeFinder, so project-defined and referenced-library
		/// controls (the SurfaceTypeFinder slice) can be added the same way stock controls are.
		/// Built from the two public primitives the real engine's own CreateComponentTool uses
		/// internally (CreateItem + PlacementOperation.TryStartInsertNewComponents) rather than
		/// AddIn/Src's internal AddItemsWithCustomSize wrapper, which is not visible to this
		/// child and additionally hardcodes position to (0,0) - calling the primitives directly
		/// gives real position control for free.</summary>
		[JsonRpcMethod("design/add-element")]
		public DesignerSessionState AddElement(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(parentId, out var parent))
					return NotFound(state, "Parent element not found: " + parentId);
				var type = current!.ParserSettings.TypeFinder.GetType(item.XamlNamespace, item.TypeName);
				if (type == null)
					return NotFound(state, $"Could not resolve type '{item.TypeName}' in namespace '{item.XamlNamespace}'.");
				DesignItem created;
				try
				{
					created = CreateComponentTool.CreateItem(current, type);
					var operation = PlacementOperation.TryStartInsertNewComponents(
						parent, new[] { created }, new[] { new Rect(x, y, DefaultElementSize, DefaultElementSize) }, PlacementType.AddItem);
					if (operation == null)
						return NotFound(state, "The parent element does not accept a new child here.");
					operation.Commit();
					// A blank WPF Menu has no visible/designable entry point.  Match the menu-strip
					// designer experience: dropping a Menu immediately gives the author one editable
					// placeholder item, rather than requiring a separate unavailable MenuItem toolbox
					// entry before a simple menu can be authored.
					if (typeof(Menu).IsAssignableFrom(type))
					{
						var menuItem = CreateComponentTool.CreateItem(current, typeof(MenuItem));
						// NaN width/height, not DefaultElementSize: DefaultPlacementBehavior.SetPosition
						// resizes every AddItem placement to the given Rect's size via ModelTools.Resize,
						// which treats NaN as "leave unset" (Reset()) rather than an explicit value (see
						// its own doc comment). A MenuItem should size to its Header text like every other
						// menu item, not get a fixed 75x75 square baked in as explicit Width/Height -
						// that square size is right for a freeform control dropped on a design canvas
						// (this method's other TryStartInsertNewComponents call, just above), not for an
						// item inside an ItemsControl-based menu/strip.
						var menuOperation = PlacementOperation.TryStartInsertNewComponents(
							created, new[] { menuItem }, new[] { new Rect(0, 0, double.NaN, double.NaN) }, PlacementType.AddItem);
						if (menuOperation != null) {
							menuOperation.Commit();
							menuItem.Properties["Header"].SetValue("Type Here");
						}
					}
					if (!string.IsNullOrEmpty(proposedName))
						created.Name = proposedName;
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				// pathToItem was just rebuilt fresh by RebuildTreeAndRender above, so this reverse
				// lookup (the same pattern HitTest already uses) reflects the item's real, current
				// path - letting a caller select the just-created element without needing a name
				// (see DesignerSessionState.CreatedElementId's own doc comment for why a name isn't
				// an option here, unlike WinForms/WinUI).
				state.CreatedElementId = pathToItem.FirstOrDefault(entry => entry.Value == created).Key;
				state.Accepted = true;
				return state;
			});

		/// <summary>Appends one more <see cref="MenuItem"/> sibling under an existing
		/// <see cref="Menu"/>, <see cref="ContextMenu"/> or <see cref="MenuItem"/> (a submenu), with
		/// its Header set directly rather than left as "Type Here" - the "Type Here" placeholder
		/// text is purely a client-side affordance (see WpfSurfaceDesignerControl's trailing tray/
		/// canvas slot), matching how <see cref="AddElement"/>'s own one-time Menu-drop placeholder
		/// already behaves. <paramref name="header"/> is expected to have already been resolved
		/// through <see cref="StripTypeHereCommit.Resolve"/> by the caller - an empty/whitespace
		/// value here is rejected rather than silently creating a blank item, since a client bug that
		/// forgot to resolve it should surface immediately.</summary>
		[JsonRpcMethod("design/add-menu-item")]
		public DesignerSessionState AddMenuItem(long baseVersion, string parentId, string header)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (string.IsNullOrWhiteSpace(header))
					return NotFound(state, "A MenuItem needs non-empty Header text.");
				if (!pathToItem.TryGetValue(parentId, out var parent))
					return NotFound(state, "Parent element not found: " + parentId);
				if (parent.ComponentType == null
					|| !(typeof(Menu).IsAssignableFrom(parent.ComponentType)
						|| typeof(ContextMenu).IsAssignableFrom(parent.ComponentType)
						|| typeof(MenuItem).IsAssignableFrom(parent.ComponentType)))
					return NotFound(state, "Parent does not accept a new MenuItem: " + parentId);
				DesignItem menuItem;
				try
				{
					menuItem = CreateComponentTool.CreateItem(current, typeof(MenuItem));
					// NaN, not DefaultElementSize - see AddElement's Menu-placeholder call for why a
					// fixed 75x75 square must not be baked in as this MenuItem's explicit Width/Height.
					var operation = PlacementOperation.TryStartInsertNewComponents(
						parent, new[] { menuItem }, new[] { new Rect(0, 0, double.NaN, double.NaN) }, PlacementType.AddItem);
					if (operation == null)
						return NotFound(state, "The parent element does not accept a new child here.");
					operation.Commit();
					menuItem.Properties["Header"].SetValue(header);
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.CreatedElementId = pathToItem.FirstOrDefault(entry => entry.Value == menuItem).Key;
				state.Accepted = true;
				return state;
			});

		/// <summary>Moves an element by <paramref name="delta"/> positions among its siblings in its
		/// parent's collection property (e.g. -1 = swap with the previous sibling, +1 = swap with
		/// the next). Generic over any collection-parented item - not menu-specific - but the only
		/// caller today is the tray's reorder-arrow UI for MenuItem reordering (WinForms drag-reorder
		/// parity's deliberately simpler stand-in, see WpfSurfaceDesignerControl). Goes through the
		/// same <see cref="DesignItem.ParentProperty"/>.CollectionElements list
		/// <see cref="DesignItem.Remove"/> itself uses, so it participates in the same undo
		/// tracking as every other structural edit.</summary>
		[JsonRpcMethod("design/move-element")]
		public DesignerSessionState MoveElement(long baseVersion, string elementId, int delta)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				var parentProperty = item.ParentProperty;
				if (parentProperty == null || !parentProperty.IsCollection)
					return NotFound(state, "Element's parent does not support reordering: " + elementId);
				var siblings = parentProperty.CollectionElements;
				var index = siblings.IndexOf(item);
				var newIndex = index + delta;
				if (index < 0 || newIndex < 0 || newIndex >= siblings.Count)
					return NotFound(state, "Cannot move the element past the start/end of its siblings.");
				try
				{
					siblings.RemoveAt(index);
					siblings.Insert(newIndex, item);
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Appends one more item under an existing <see cref="StatusBar"/> or
		/// <see cref="ToolBar"/> - the "Type Here" insertion-node commit action for those two strip
		/// types (mirroring WinForms' StatusStrip insertion node), analogous to
		/// <see cref="AddMenuItem"/> for Menu/ContextMenu/MenuItem. Item shape differs by container,
		/// matching each control's own natural child type: a StatusBar gets a
		/// <see cref="StatusBarItem"/> with its Content set to <paramref name="text"/>; a ToolBar
		/// gets a <see cref="Separator"/> when <paramref name="text"/> is exactly "-" (matching
		/// WinForms' own Type-Here "-" convention for a ToolStripSeparator), otherwise a
		/// <see cref="Button"/> with its Content set to <paramref name="text"/>.</summary>
		[JsonRpcMethod("design/add-strip-item")]
		public DesignerSessionState AddStripItem(long baseVersion, string parentId, string text)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (string.IsNullOrWhiteSpace(text))
					return NotFound(state, "A strip item needs non-empty text.");
				if (!pathToItem.TryGetValue(parentId, out var parent))
					return NotFound(state, "Parent element not found: " + parentId);
				bool isStatusBar = parent.ComponentType != null && typeof(StatusBar).IsAssignableFrom(parent.ComponentType);
				bool isToolBar = parent.ComponentType != null && typeof(ToolBar).IsAssignableFrom(parent.ComponentType);
				if (!isStatusBar && !isToolBar)
					return NotFound(state, "Parent does not accept a new strip item: " + parentId);
				DesignItem newItem;
				try
				{
					var itemType = isStatusBar ? typeof(StatusBarItem)
						: text.Trim() == "-" ? typeof(Separator) : typeof(Button);
					newItem = CreateComponentTool.CreateItem(current, itemType);
					// NaN, not DefaultElementSize - same reasoning as AddMenuItem: a StatusBarItem/
					// Button/Separator inside a StatusBar/ToolBar should size to its own content,
					// not get a fixed 75x75 square baked in as explicit Width/Height.
					var operation = PlacementOperation.TryStartInsertNewComponents(
						parent, new[] { newItem }, new[] { new Rect(0, 0, double.NaN, double.NaN) }, PlacementType.AddItem);
					if (operation == null)
						return NotFound(state, "The parent element does not accept a new child here.");
					operation.Commit();
					if (itemType != typeof(Separator))
						newItem.Properties["Content"].SetValue(text);
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.CreatedElementId = pathToItem.FirstOrDefault(entry => entry.Value == newItem).Key;
				state.Accepted = true;
				return state;
			});

		[JsonRpcMethod("design/set-bounds")]
		public DesignerSessionState SetBounds(long baseVersion, string elementId, double x, double y, double width, double height)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				try
				{
					// Route through the designer's own PlacementOperation rather than setting
					// Width/Height directly, so x/y (a MOVE) is actually honored and is expressed
					// the way the *container* wants it: Canvas.Left/Top under a Canvas, Margin +
					// alignment under a Grid, and so on. Setting Width/Height alone - what this
					// did originally - silently dropped x/y entirely, so an interactive drag could
					// only ever resize, never move. PlacementType.Resize covers both here: it is
					// the "bounds changed" operation (move alone is PlacementType.Move, but a
					// drag-resize also moves the top-left for the nw/n/w handles, so the general
					// case is a single bounds assignment).
					var operation = PlacementOperation.Start(new[] { item }, PlacementType.Resize);
					try
					{
						var info = operation.PlacedItems[0];
						info.Bounds = new Rect(x, y, width, height);
						operation.CurrentContainerBehavior.SetPosition(info);
						operation.Commit();
					}
					catch
					{
						operation.Abort();
						throw;
					}
				}
				catch (Exception placementFailure)
				{
					// A container with no placement behavior at all (or an element it refuses to
					// place) still supports a plain resize - fall back rather than failing the
					// whole operation, matching how the live in-process designer degrades.
					try
					{
						item.Properties["Width"].SetValue(width);
						item.Properties["Height"].SetValue(height);
					}
					catch (Exception e)
					{
						return NotFound(state, e.GetBaseException().Message + " (placement also failed: "
							+ placementFailure.GetBaseException().Message + ")");
					}
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Reports the given Grid's current row/column track geometry (real post-layout
		/// <c>Offset</c>/<c>ActualHeight</c>/<c>ActualWidth</c> off the live <see cref="Grid"/> -
		/// <c>RowDefinition.Offset</c>/<c>ColumnDefinition.Offset</c> are already cumulative, no
		/// summation needed), for the design surface to draw draggable divider guides over the
		/// rendered frame - this backend's equivalent of the Uno/WinUI designer's own Grid-guide
		/// overlay, which instead reads offsets from its live XAML text editor. Read-only: does
		/// NOT call <see cref="RebuildTreeAndRender"/>, since nothing is mutated.</summary>
		[JsonRpcMethod("design/query-grid-guides")]
		public DesignerGridGuides QueryGridGuides(long baseVersion, string elementId)
			=> dispatcher.Dispatch(() => {
				if (current == null)
					return new DesignerGridGuides { Accepted = false, Error = "No document is open." };
				if (version != baseVersion)
					return new DesignerGridGuides {
						Accepted = false,
						Error = $"Stale base version {baseVersion}; the open document is at version {version}."
					};
				if (!pathToItem.TryGetValue(elementId, out var item))
					return new DesignerGridGuides { Accepted = false, Error = "Element not found: " + elementId };
				if (item.Component is not Grid grid)
					return new DesignerGridGuides { Accepted = false, Error = "Element is not a Grid: " + elementId };
				return new DesignerGridGuides {
					Accepted = true,
					RowTracks = grid.RowDefinitions
						.Select(r => new DesignerGridTrackInfo { Offset = r.Offset, Size = r.ActualHeight })
						.ToList(),
					ColumnTracks = grid.ColumnDefinitions
						.Select(c => new DesignerGridTrackInfo { Offset = c.Offset, Size = c.ActualWidth })
						.ToList()
				};
			});

		/// <summary>Commits a Grid row's/column's new pixel size (a completed divider drag) -
		/// routes through the same <c>DesignItem</c>/<c>DesignItemProperty</c> pipeline as
		/// <see cref="SetProperty"/>, since <c>RowDefinitions</c>/<c>ColumnDefinitions</c> are
		/// themselves represented as a <c>DesignItem</c> collection (each element a DesignItem
		/// wrapping one <see cref="RowDefinition"/>/<see cref="ColumnDefinition"/>), not bypassed
		/// via the live <see cref="Grid"/> object - so this edit gets the same undo/change-
		/// notification coverage every other mutation RPC does.</summary>
		[JsonRpcMethod("design/set-grid-track-size")]
		public DesignerSessionState SetGridTrackSize(long baseVersion, string elementId, bool isRow, int index, double pixels)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				var collection = item.Properties[isRow ? "RowDefinitions" : "ColumnDefinitions"];
				if (collection == null || index < 0 || index >= collection.CollectionElements.Count)
					return NotFound(state, "Row/column index out of range: " + index);
				try
				{
					var definitionItem = collection.CollectionElements[index];
					var property = definitionItem.Properties[
						isRow ? RowDefinition.HeightProperty : ColumnDefinition.WidthProperty];
					property.SetValue(new GridLength(pixels, GridUnitType.Pixel));
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		[JsonRpcMethod("design/delete-elements")]
		public DesignerSessionState DeleteElements(long baseVersion, string[] elementIds)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				// Resolve every id before removing anything: a bad id partway through the list
				// must reject the whole operation, not leave the earlier ones already deleted
				// (the same "reject cannot partially apply" invariant flush/stale-version checks
				// already enforce - confirmed as a real, reproducing bug, not a hypothetical one,
				// by a real run before this fix; see wpf-designer.md's Phase 1 progress notes).
				var items = new List<DesignItem>(elementIds.Length);
				foreach (var id in elementIds)
				{
					if (!pathToItem.TryGetValue(id, out var item))
						return NotFound(state, "Element not found: " + id);
					items.Add(item);
				}
				try
				{
					foreach (var item in items)
						item.Remove();
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		[JsonRpcMethod("design/rename")]
		public DesignerSessionState Rename(long baseVersion, string elementId, string newName)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				try
				{
					item.Name = newName;
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Switches the design-time theme by name, per the WPF-standard convention of
		/// embedded <c>themes/*.xaml</c> resources (see <see cref="ResolveThemes"/>). The theme
		/// dictionary is merged onto the open design's
		/// root and the design re-rendered. Reports <c>Accepted = false</c> (not an exception)
		/// when the project embeds no themes, the name is unknown, or no document is open - the
		/// same "graceful no-op" shape RejectIfStale's own doc comment establishes for every
		/// other mutation.</summary>
		[JsonRpcMethod("design/theme")]
		public DesignerSessionState SetTheme(long baseVersion, string theme)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				state.SupportsThemeSwitch = themeSources != null;
				state.DesignThemes = themeSources?.Keys.ToArray() ?? Array.Empty<string>();
				if (themeSources == null || !themeSources.TryGetValue(theme, out var resourceName))
					return NotFound(state, "No theme named '" + theme + "' is embedded in this project's assembly.");
				if (current?.RootItem?.View is not FrameworkElement root)
					return NotFound(state, "No design is open.");
				try
				{
					using var stream = projectAssembly!.GetManifestResourceStream(resourceName);
					if (stream == null)
						return NotFound(state, "Embedded theme resource not found: " + resourceName);
					using var reader = new StreamReader(stream);
					var dictionary = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(reader.ReadToEnd());
					if (appliedThemeDictionary != null)
						root.Resources.MergedDictionaries.Remove(appliedThemeDictionary);
					root.Resources.MergedDictionaries.Add(dictionary);
					appliedThemeDictionary = dictionary;
					appliedThemeName = theme;
				}
				catch (Exception e)
				{
					return NotFound(state, "Failed to load theme dictionary '" + theme + "': " + e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Resolves the design-time themes of <paramref name="projectAssembly"/> using the
		/// WPF-standard convention: one embedded <c>themes/&lt;name&gt;.xaml</c> resource per
		/// theme, file name (without extension) = theme name. <c>generic.xaml</c> is excluded -
		/// it is the fallback default-style dictionary, not a switchable theme. Clears any
		/// PREVIOUS session's themes when the assembly has none, rather than leaving stale
		/// names dangling.</summary>
		void ResolveThemes(Assembly? projectAssembly)
		{
			themeSources = null;
			this.projectAssembly = projectAssembly;
			if (projectAssembly == null)
				return;
			var themes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var name in projectAssembly.GetManifestResourceNames())
			{
				// The manifest resource name is dot-separated ("WpfThemeFixture.themes.Bright.xaml"),
				// with no directory slashes - the theme directory is the ".themes." segment, the
				// theme file name is the next segment, and "xaml" the one after that.
				var segments = name.Split('.');
				for (int i = 0; i + 2 < segments.Length; i++)
				{
					if (!segments[i].Equals("themes", StringComparison.OrdinalIgnoreCase))
						continue;
					if (!segments[i + 2].Equals("xaml", StringComparison.OrdinalIgnoreCase))
						continue;
					var themeName = segments[i + 1];
					// generic.xaml is the fallback default-style dictionary, not a theme.
					if (themeName.Equals("generic", StringComparison.OrdinalIgnoreCase))
						continue;
					themes[themeName] = name;
					break;
				}
			}
			if (themes.Count > 0)
			{
				themeSources = themes;
			}
		}

		[JsonRpcMethod("ping")]
		public void Ping() { }

		[JsonRpcMethod("shutdown")]
		public void Shutdown() => shutdown.Set();

		public void WaitForShutdown() => shutdown.Wait();
		public void OnParentDisconnected() => shutdown.Set();

		internal void Close()
		{
			dispatcher.Dispatch(() => {
				current = null;
				pathToItem.Clear();
				appliedThemeDictionary = null;
				appliedThemeName = null;
				themeSources = null;
				projectAssembly = null;
				documentId = null;
				return true;
			});
		}

		DesignerSessionState NewState(long baseVersion) => new() { SessionId = sessionId ?? "", DocumentId = documentId ?? "", Version = baseVersion };

		/// <summary>Enforces the DDP's mandatory stale-operation rule ("every mutating operation
		/// carries a base document version; a stale operation is rejected and cannot overwrite
		/// newer source" - see the isolation decision's rule 5 and its review checklist's "Can a
		/// stale request overwrite newer XAML?"). Returns a rejected state when the caller's
		/// baseVersion is not the currently open one, or when no document is open at all; null
		/// means the operation may proceed. Rejection is a normal Accepted == false result rather
		/// than an exception, matching how every other mutation failure on this backend reports.</summary>
		DesignerSessionState? RejectIfStale(long baseVersion)
		{
			if (current == null)
				return NotFound(NewState(baseVersion), "No document is open.");
			if (version != baseVersion)
				return NotFound(NewState(baseVersion),
					$"Stale base version {baseVersion}; the open document is at version {version}.");
			return null;
		}

		static DesignerSessionState NotFound(DesignerSessionState state, string error)
		{
			state.Accepted = false;
			state.Error = error;
			state.Diagnostics.Add(new DesignerDiagnostic { Message = error });
			return state;
		}

		static object ConvertValue(Type targetType, string value)
		{
			if (targetType == typeof(string))
				return value;
			var converter = TypeDescriptor.GetConverter(targetType);
			if (converter != null && converter.CanConvertFrom(typeof(string)))
				return converter.ConvertFromInvariantString(value)!;
			return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
		}

		/// <summary>Builds the Properties pad list for one element. <see cref="DesignItem.Properties"/>
		/// is deliberately NOT enumerated directly here - confirmed by a real run that it only
		/// yields properties some caller has already realized a <c>DesignItemProperty</c> wrapper
		/// for (`XamlModelPropertyCollection` caches wrappers lazily, keyed by name, and its
		/// enumerator only walks that cache) - for a freshly-opened session that's just whatever
		/// this method's own `item.ContentProperty` walk happened to touch ("Children"/"Content"),
		/// not the element's real browsable properties ("Text", "Width", ...). Instead, this
		/// reflects the element's real CLR type via `TypeDescriptor.GetProperties` (which already
		/// filters out non-browsable WPF plumbing members like `Dispatcher`/`TemplatedParent` the
		/// way any .NET property grid would) to get the full candidate name list, then looks each
		/// one up through `item.Properties[name]` - which, unlike enumeration, creates the wrapper
		/// on demand for any valid name (`GetProperty` calls `FindOrCreateProperty`).</summary>
		static List<DesignerPropertyInfo> BuildProperties(DesignItem item)
		{
			if (item.ComponentType == null)
				return new List<DesignerPropertyInfo>();
			var result = new List<DesignerPropertyInfo>();
			foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(item.ComponentType))
			{
				if (!descriptor.IsBrowsable)
					continue;
				DesignItemProperty? property;
				try
				{
					property = item.Properties[descriptor.Name];
				}
				catch (Exception)
				{
					continue;
				}
				if (property == null || property.IsEvent)
					continue;
				result.Add(ToPropertyInfo(property));
			}
			return result;
		}

		/// <summary>Converts one <see cref="DesignItemProperty"/> to the wire shape the
		/// Properties pad reads (designer-common.md "Property and event values"). Deliberately
		/// conservative: only properties with a symmetric string <see cref="TypeConverter"/>
		/// (covers primitives, enums, and every simple XAML-serializable value type) get a real,
		/// editable <see cref="DesignerPropertyInfo.Value"/>; anything else (nested DesignItem
		/// values - Binding, Brush, layout objects, ...) is reported read-only with a best-effort
		/// display string rather than crashing the whole tree build or silently corrupting data
		/// through a lossy round-trip. Widening this to "Xaml"/"Reference" kinds for those nested
		/// values is real future work, not attempted here.</summary>
		static DesignerPropertyInfo ToPropertyInfo(DesignItemProperty property)
		{
			var info = new DesignerPropertyInfo {
				Name = property.Name,
				DisplayName = property.Name,
				Category = string.IsNullOrEmpty(property.Category) ? "Misc" : property.Category,
				TypeName = property.ReturnType?.FullName ?? "",
				ShouldSerialize = property.IsSet
			};
			try
			{
				var value = property.ValueOnInstance;
				if (value == null)
				{
					info.IsNull = true;
					info.Kind = "Null";
					return info;
				}
				// A WPF "object"-typed content property (Content/Header/ToolTip, etc.) holding a
				// plain string is one of the most common editable properties in the Properties pad
				// (e.g. a Button's Content="..."). TypeDescriptor.GetConverter(typeof(object))'s
				// converter reports CanConvertFrom(string) == false (the base TypeConverter only
				// supports InstanceDescriptor round-trips, not arbitrary strings), which fell into
				// the "Unsupported"/read-only branch below purely because of the property's
				// DECLARED type - even though the ACTUAL value is a string and perfectly editable.
				// That silently made DescriptorPropertyDefinition.CreateValueBinding's Value binding
				// OneWay (Mode is keyed off PropertyDescriptor.IsReadOnly), so a Properties-pad edit
				// never reached WpfSurfacePropertyDescriptor.SetValue at all - a real edit appeared
				// to "succeed" (the Xceed PropertyItem's own local DP value changed) while nothing
				// was ever sent to the child, and the file never got marked dirty. Checking the
				// actual runtime value's type first sidesteps the declared-type converter entirely.
				if (value is string stringValue)
				{
					info.Value = stringValue;
					info.Kind = "String";
					return info;
				}
				var converter = property.ReturnType != null ? TypeDescriptor.GetConverter(property.ReturnType) : null;
				if (converter != null && converter.CanConvertTo(typeof(string)) && converter.CanConvertFrom(typeof(string)))
				{
					info.Value = converter.ConvertToInvariantString(value) ?? "";
					info.IsEnum = property.ReturnType!.IsEnum;
					info.Kind = property.ReturnType == typeof(bool) ? "Boolean"
						: info.IsEnum ? "Enum"
						: IsNumericType(property.ReturnType) ? "Number"
						: "String";
				}
				else
				{
					info.Kind = "Unsupported";
					info.IsReadOnly = true;
					info.Value = value.ToString() ?? "";
				}
			}
			catch (Exception)
			{
				// A property whose getter/converter throws (e.g. not resolvable outside a real
				// PresentationSource) must not fail the whole tree build - report it unsupported.
				info.Kind = "Unsupported";
				info.IsReadOnly = true;
			}
			return info;
		}

		static bool IsNumericType(Type type) =>
			type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
			type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
			type == typeof(float) || type == typeof(double) || type == typeof(decimal);

		void EnsureInitialized()
		{
			if (!initialized)
				throw new UnauthorizedAccessException("The designer host has not completed its handshake.");
		}

		void EnsureOwnSession(string requestSessionId, string requestDocumentId)
		{
			if (requestSessionId != sessionId)
				throw new UnauthorizedAccessException("The request's session id does not match this designer host.");
			if (documentId != null && requestDocumentId != documentId)
				throw new InvalidOperationException("The request's document id does not match the open document.");
		}

		/// <summary>Rebuilds the path->DesignItem lookup and the neutral element tree, then
		/// re-renders. Paths become stale after any structural edit (add/remove), so this always
		/// rebuilds the whole table rather than patching it incrementally - matches the DDP rule
		/// that element ids are only meaningful within one generation.</summary>
		void RebuildTreeAndRender(DesignerSessionState state)
		{
			if (current?.RootItem == null)
				return;
			pathToItem = new Dictionary<string, DesignItem>(StringComparer.Ordinal);
			if (current.RootItem.View is FrameworkElement root)
			{
				#if MICROSOFT_WPF
				EnsureNativePresentationSource(root);
				#endif
				// Measure against the viewport, then arrange at the root's OWN desired size - not
				// at the viewport rect. Arranging a root that declares an explicit Width/Height
				// (or otherwise desires less than the viewport) into the larger viewport rect is
				// what WPF's normal Stretch-alignment centering acts on: ArrangeCore clamps the
				// arrange size back down via MinMax (so RenderSize stays correct and looks fine),
				// but ComputeAlignmentOffset still centers that content inside the *viewport*,
				// leaving the root with a non-zero VisualOffset of ((viewport - content) / 2).
				// Render() then sizes its texture from the root's own ActualWidth/ActualHeight,
				// so that offset shifted every rendered pixel relative to the coordinates the
				// element tree reports - the "coordinate mismatch" tracked in wpf-designer.md.
				// Arranging at DesiredSize keeps the root's offset at (0,0), which is also what
				// a design surface wants: show the design at its natural/declared size and let
				// the host's own canvas letterbox around it (DesignViewport/DesignerCanvas).
				//
				// A page/UserControl/Window root has no natural HEIGHT of its own - Measure at
				// (lastWidth, lastHeight) always reports DesiredSize.Height == that constraint,
				// because a Stretch-aligned root (and any ScrollViewer inside it) just fills whatever
				// vertical space it is given rather than growing to fit its content. That is normally
				// fine (a design surface showing "the declared/nominal size" is the point), but it
				// means content that only needs MORE room at this exact moment - e.g. Select's
				// Expander auto-expand just added a tall child inside SettingsPage's own
				// <ScrollViewer> - gets silently clipped by that ScrollViewer's internal scrolling
				// instead of ever reaching the rendered bitmap at all: no amount of scrolling the
				// IDE's own design canvas can reveal pixels that were never rendered in the first
				// place. Probe with an unconstrained-height Measure first to learn how tall the
				// content genuinely wants to be right now (a ScrollViewer given infinite available
				// height has no need to clip, so it reports its child's real DesiredSize.Height
				// instead of virtualizing it), and only grow past the nominal lastHeight when that
				// probe says the content actually needs more - so an ordinary, nothing-expanded
				// render measures identically to before.
				//
				// Width is NOT probed the same way - see OverrideRootSizeForExpansion's own doc
				// comment for why an unconstrained WIDTH probe is actively harmful (non-wrapping
				// content reporting an enormous natural width nothing needed). Width stays pinned to
				// lastWidth unconditionally; only Select's temporary Height override
				// (OverrideRootSizeForExpansion) ever lets this probe see past the root's own
				// explicit d:DesignHeight-derived size.
				root.Measure(new Size(lastWidth, double.PositiveInfinity));
				var natural = root.DesiredSize;
				var effectiveHeight = double.IsInfinity(natural.Height) || double.IsNaN(natural.Height)
					? lastHeight : Math.Max(lastHeight, natural.Height);
				root.Measure(new Size(lastWidth, effectiveHeight));
				var desired = root.DesiredSize;
				root.Arrange(new Rect(0, 0,
					desired.Width > 0 ? desired.Width : lastWidth,
					desired.Height > 0 ? desired.Height : effectiveHeight));
				root.UpdateLayout();
			}
			state.Tree = BuildNode(current.RootItem, current.RootItem, "");
			state.Render = Render(current.RootItem.View as FrameworkElement, state.Tree);
			#if !MICROSOFT_WPF
			if (renderUnavailable)
				state.Diagnostics.Add(new DesignerDiagnostic {
					Severity = "Warning",
					Message = "GPU rendering is unavailable or disabled; showing the bounded software fallback frame."
				});
			#endif
			state.ComponentCount = pathToItem.Count;
		}

		#if MICROSOFT_WPF
		void EnsureNativePresentationSource(FrameworkElement root)
		{
			if (ReferenceEquals(renderPresentationRoot, root))
				return;
			if (renderPresentationSource == null)
			{
				// Off-screen popup/tool window: it participates in WPF composition but is neither
				// visible nor taskbar-addressable. Its 4096px client area exceeds normal designer
				// roots; the root is still measured/arranged at its own desired size below.
				var parameters = new HwndSourceParameters("OpenDevelop WPF Design Render") {
					PositionX = -32000, PositionY = -32000, Width = 4096, Height = 4096,
					WindowStyle = unchecked((int)0x80000000), // WS_POPUP
					ExtendedWindowStyle = 0x00000080 // WS_EX_TOOLWINDOW
				};
				renderPresentationSource = new HwndSource(parameters);
			}
			renderPresentationSource.RootVisual = root;
			renderPresentationRoot = root;
			Console.Error.WriteLine("WpfDesign.SurfaceHost: attached design root to isolated native WPF presentation source.");
		}
		#endif

		DesignerElementNode BuildNode(DesignItem item, DesignItem root, string path)
		{
			pathToItem[path] = item;
			var node = new DesignerElementNode {
				Id = path,
				Name = string.IsNullOrEmpty(item.Name) ? null : item.Name,
				Type = item.ComponentType?.Name ?? "",
				Path = path,
				Properties = BuildProperties(item)
			};
			if (item.View is FrameworkElement element && root.View is Visual rootVisual)
			{
				node.Width = element.ActualWidth;
				node.Height = element.ActualHeight;
				// See DesignerElementNode.IsVisible for why clients need this before drawing any
				// overlay positioned from X/Y.
				//
				// Deliberately NOT UIElement.IsVisible, even though that is WPF's own effective
				// visibility: it also requires the element to be connected to a live presentation
				// source, which this OFFSCREEN design host never has - it reported false for the
				// entire tree, including plainly visible elements, which would have suppressed
				// every overlay. Folding Visibility up the chain works regardless of whether
				// anything is being presented.
				node.IsVisible = IsEffectivelyVisible(element, rootVisual);
				try
				{
					var offset = element.TransformToAncestor(rootVisual).Transform(new Point(0, 0));
					node.X = offset.X;
					node.Y = offset.Y;
				}
				catch (InvalidOperationException)
				{
					// Not connected to the root's visual tree (shouldn't happen post-layout, but
					// don't let a geometry edge case fail the whole tree build). It also means the
					// element is not on screen - a TabControl realises only the selected tab's
					// content, so an unselected tab's children land here - and its X/Y are
					// therefore meaningless, exactly the case an overlay must skip.
					node.IsVisible = false;
				}
			}
			var contentProperty = item.ContentProperty;
			if (contentProperty != null)
			{
				if (contentProperty.IsCollection)
				{
					var index = 0;
					foreach (var child in contentProperty.CollectionElements)
					{
						var childPath = path.Length == 0 ? index.ToString(CultureInfo.InvariantCulture) : path + "," + index;
						node.Children.Add(BuildNode(child, root, childPath));
						index++;
					}
				}
				else if (contentProperty.Value != null)
				{
					var childPath = path.Length == 0 ? "0" : path + ",0";
					node.Children.Add(BuildNode(contentProperty.Value, root, childPath));
				}
			}
			AppendAttachedContextMenu(item, root, path, node);
			AppendHeaderContent(item, root, path, node);
			return node;
		}

		/// <summary>Adds the one detached strip that WPF deliberately keeps outside an owner's
		/// visual/content tree. An assigned <see cref="ContextMenu"/> is a regular design item
		/// (and its <see cref="MenuItem"/> children therefore have editable Header values), but it
		/// is reached through the owner's ContextMenu property rather than ContentProperty. Without
		/// this explicit edge it is invisible to the designer Outline and Properties pads. This is
		/// intentionally limited to ContextMenu; it is not a general traversal of object-valued
		/// properties.</summary>
		void AppendAttachedContextMenu(DesignItem item, DesignItem root, string path, DesignerElementNode node)
		{
			DesignItem? contextMenu;
			try
			{
				contextMenu = item.Properties["ContextMenu"]?.Value;
			}
			catch (Exception)
			{
				return;
			}
			if (contextMenu?.ComponentType == null || !typeof(ContextMenu).IsAssignableFrom(contextMenu.ComponentType))
				return;
			var contextMenuPath = path.Length == 0 ? "@context-menu" : path + ",@context-menu";
			node.Children.Add(BuildNode(contextMenu, root, contextMenuPath));
		}

		/// <summary>Adds a <see cref="HeaderedContentControl"/>/<see cref="HeaderedItemsControl"/>'s
		/// object-valued <c>Header</c> as an outline child, under a synthetic "Header" grouping node.
		/// <c>Header</c> is a separate property from <see cref="DesignItem.ContentProperty"/> (which
		/// for <c>Expander</c> et al. is <c>Content</c>, not <c>Header</c>) and unrelated to a
		/// header/expander's collapsed state - it is always realized in the visual tree regardless -
		/// so an element-valued Header (e.g. an <c>&lt;Expander.Header&gt;</c> containing an
		/// <c>Image</c>, as in WPFGallery's SettingsPage.xaml About section) was structurally
		/// excluded from <see cref="BuildNode"/>'s traversal, same shape of gap as
		/// <see cref="AppendAttachedContextMenu"/> fixes for ContextMenu. A plain string/data-template
		/// Header (the common case) has no DesignItem value and is skipped here exactly like a null
		/// ContentProperty.Value.
		///
		/// Unlike ContextMenu (whose own node is already self-explanatory), Header content is
		/// typically ordinary elements (a Grid, a TextBlock) that look identical to the owner's real
		/// Content sitting right next to it in the flattened tree - without a label there is no way
		/// to tell which is which. The grouping node has no backing DesignItem (deliberately absent
		/// from <c>pathToItem</c> - every RPC that resolves an element id already reports "not found"
		/// gracefully for an unknown id, so selecting this node client-side is harmless), just a type
		/// label the Outline pad renders like any other unnamed node.</summary>
		void AppendHeaderContent(DesignItem item, DesignItem root, string path, DesignerElementNode node)
		{
			DesignItem? header;
			try
			{
				header = item.Properties["Header"]?.Value;
			}
			catch (Exception)
			{
				return;
			}
			if (header == null)
				return;
			var headerPath = path.Length == 0 ? "@header" : path + ",@header";
			var headerGroupNode = new DesignerElementNode {
				Id = headerPath + "-group",
				Type = "Header",
				Path = headerPath,
			};
			headerGroupNode.Children.Add(BuildNode(header, root, headerPath));
			node.Children.Add(headerGroupNode);
		}

		/// <summary>Whether an element is actually on screen, by folding <c>Visibility</c> up the
		/// visual tree to (but excluding) the design root.
		///
		/// <c>UIElement.IsVisible</c> would be the obvious answer and is WRONG here: it additionally
		/// requires connection to a live presentation source, which this offscreen host never has,
		/// so it reports false for the whole tree - plainly visible elements included. The root is
		/// excluded for a related reason: a design root is not a normally-presented element, and if
		/// it reported collapsed then every node would come back invisible and a client filtering
		/// on this would draw no overlays at all.</summary>
		static bool IsEffectivelyVisible(DependencyObject element, DependencyObject root)
		{
			for (var current = element; current != null && current != root;
				current = VisualTreeHelper.GetParent(current))
			{
				if (current is UIElement visual && visual.Visibility != Visibility.Visible)
					return false;
			}
			return true;
		}

		/// <summary>Headless GPU-composited render (see wpf-designer.md's Phase 1 progress notes).
		/// RenderTargetBitmap always calls into the Windows-only native wpfgfx_cor3 compositor,
		/// confirmed absent under LibreWPF on macOS. ProGpuWpfCompositionTarget is LibreWPF's
		/// real, public, ordinary-managed-API portable render path instead: ReplayVisualSubtree
		/// walks a real WPF visual directly (no manual DrawingContext calls needed), Render
		/// composites it into a GpuTexture, ReadPixels reads it back to managed memory. Confirmed
		/// working end to end by a real run - an earlier attempt wrongly concluded this path
		/// produced no visible output, based on sampling pixel coordinates that assumed the
		/// document's content would be centered; the actual rendered content was simply
		/// elsewhere in the frame, which a scan across the whole buffer (comparing against the
		/// pixel at (0,0) as the background) revealed. If the underlying WebGPU backend itself is
		/// genuinely unavailable, this fails the same way RenderTargetBitmap did - caught below
		/// and rendering is skipped rather than failing session/open, exactly as before.</summary>
		unsafe DesignerRenderFrame? Render(FrameworkElement? element, DesignerElementNode? tree)
		{
#if MICROSOFT_WPF
			if (element == null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
				return null;
			var stopwatch = Stopwatch.StartNew();
			var width = (int)Math.Ceiling(element.ActualWidth);
			var height = (int)Math.Ceiling(element.ActualHeight);
			try {
				var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
				bitmap.Render(element);
				var pixels = new byte[width * height * 4];
				bitmap.CopyPixels(pixels, width * 4, 0);
				// Do not confuse transparent WPF content with a black compositor failure.  Pbgra32
				// stores transparent pixels as (0,0,0,0), and a page which intentionally paints only
				// black text/lines over a transparent Page has RGB==0 for every pixel while still being
				// a perfectly valid preview.  The old RGB-only test rejected exactly that shape.
				// A genuine RenderTargetBitmap black frame is opaque black everywhere (A=255 too).
				var opaqueBlack = true;
				var nonTransparentPixels = 0;
				for (var i = 0; i < pixels.Length; i += 4) {
					if (pixels[i + 3] != 0)
						nonTransparentPixels++;
					if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0 || pixels[i + 3] != 255) {
						opaqueBlack = false;
					}
				}
				if (opaqueBlack)
					throw new NativeWpfRenderFailure("Native WPF RenderTargetBitmap returned an opaque all-black frame.");
				Console.Error.WriteLine($"WpfDesign.SurfaceHost: native frame {width}x{height}, non-transparent pixels={nonTransparentPixels}.");
				var data = DesignerFrameCodec.EncodeDeflateBase64(pixels);
				stopwatch.Stop();
				return new DesignerRenderFrame {
					Sequence = ++frameSequence, Width = width, Height = height, Dpi = 1,
					Data = data, RenderMs = stopwatch.Elapsed.TotalMilliseconds
				};
			} catch (NativeWpfRenderFailure) {
				throw;
			} catch (Exception e) {
				Console.Error.WriteLine("WpfDesign.SurfaceHost: native WPF render failed: " + e);
				return null;
			}
#else
			if (element == null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
				return null;
			var stopwatch = Stopwatch.StartNew();
			var width = (uint)Math.Ceiling(element.ActualWidth);
			var height = (uint)Math.Ceiling(element.ActualHeight);
			if (renderUnavailable)
				return FallbackFrame(tree, (int)width, (int)height, stopwatch);
			byte[] rgbaPixels;
			try
			{
				renderTarget ??= GpuCompositionTarget.CreateHeadless();
				var texture = new GpuTexture(renderTarget.Context, width, height,
					TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc,
					"WpfDesign.SurfaceHost render target");
				renderTarget.ReplayVisualSubtree(element, width, height);
				renderTarget.Render(width, height, width, height, 1f, texture.ViewPtr);
				// ProGPU's readback may poll an unavailable device for 30 seconds. Keep the
				// dispatcher/RPC bounded: the GPU composition is still attempted, but an
				// unresponsive readback falls back after a small, deterministic budget.
				var readback = System.Threading.Tasks.Task.Run(() => {
					try { return texture.ReadPixels(); }
					finally { texture.Dispose(); }
				});
				if (!readback.Wait(TimeSpan.FromSeconds(2)))
				{
					renderUnavailable = true;
					_ = readback.ContinueWith(task => _ = task.Exception,
						System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
					Console.Error.WriteLine("WpfDesign.SurfaceHost: ProGPU frame readback exceeded 2 seconds; using software fallback frames.");
					return FallbackFrame(tree, (int)width, (int)height, stopwatch);
				}
				rgbaPixels = readback.GetAwaiter().GetResult();
			}
			catch (Exception e)
			{
				// Not narrowed to DllNotFoundException like the old RenderTargetBitmap catch:
				// this is a different native backend (WebGPU/GPU driver) with its own unknown
				// failure shapes on a machine without one - fail soft the same way regardless.
				renderUnavailable = true;
				Console.Error.WriteLine("WpfDesign.SurfaceHost: ProGPU render unavailable, using software fallback frames: " + e);
				return FallbackFrame(tree, (int)width, (int)height, stopwatch);
			}
			// ReadPixels returns RGBA byte order (Rgba8Unorm); DesignerRenderFrame.Data's
			// established wire shape (matching WinUI/Uno) is BGRA - swap R/B in place rather than
			// widen the protocol, since this is purely an encoding detail of this one backend.
			for (var i = 0; i + 2 < rgbaPixels.Length; i += 4)
				(rgbaPixels[i], rgbaPixels[i + 2]) = (rgbaPixels[i + 2], rgbaPixels[i]);
			var data = DesignerFrameCodec.EncodeDeflateBase64(rgbaPixels);
			stopwatch.Stop();
			return new DesignerRenderFrame {
				Sequence = ++frameSequence,
				Width = (int)width,
				Height = (int)height,
				Dpi = 1,
				Data = data,
				RenderMs = stopwatch.Elapsed.TotalMilliseconds
			};
#endif
		}

#if MICROSOFT_WPF
		sealed class NativeWpfRenderFailure : Exception
		{
			public NativeWpfRenderFailure(string message) : base(message) { }
		}
#endif

		/// <summary>Small managed fallback for hosts whose GPU backend cannot complete a pixel
		/// readback. It intentionally uses the common BGRA frame format so the existing remote
		/// presentation path needs no designer-specific transport branch.</summary>
		DesignerRenderFrame FallbackFrame(DesignerElementNode? tree, int width, int height, Stopwatch stopwatch)
		{
			var pixels = new byte[checked(width * height * 4)];
			var themeHash = appliedThemeName?.GetHashCode(StringComparison.Ordinal) ?? 0;
			var tint = (byte)((version + themeHash) & 0x3f);
			for (var i = 0; i < pixels.Length; i += 4)
			{
				pixels[i] = (byte)(235 - tint / 4);
				pixels[i + 1] = (byte)(235 - tint / 8);
				pixels[i + 2] = 235;
				pixels[i + 3] = 255;
			}
			// Paint the actual laid-out element backgrounds in tree order. This is intentionally
			// conservative (it does not attempt text, templates, or effects), but preserves the
			// geometry and colour information that a remote surface needs when GPU composition is
			// unavailable.
			if (tree != null)
				foreach (var node in Flatten(tree))
				{
					if (pathToItem.TryGetValue(node.Id, out var item) && item.View is FrameworkElement element
						&& TryGetBackground(element, out var color))
						PaintFallbackRect(pixels, width, height, node, color);
					// Templates often have no local Background (Button is the common case), yet their
					// arranged bounds are authoritative. Draw a subtle outline so an OOP headless
					// fallback remains a usable designer canvas rather than a blank white page.
					PaintFallbackOutline(pixels, width, height, node, 110, 110, 110);
				}

			// Keep a visible frame edge and a version/theme-sensitive marker. The latter makes a
			// successful mutation or design-theme switch observable without GPU readback.
			for (var x = 0; x < width; x++)
				PaintFallbackPixel(pixels, width, height, x, 0, 96, 96, 96);
			for (var y = 0; y < height; y++)
				PaintFallbackPixel(pixels, width, height, 0, y, 96, 96, 96);
			var markerWidth = Math.Min(width - 1, 12 + (int)(version & 0x1f));
			for (var x = 1; x <= markerWidth; x++)
				for (var y = 1; y < Math.Min(height, 5); y++)
					PaintFallbackPixel(pixels, width, height, x, y, 180, 110, 45);
			stopwatch.Stop();
			return new DesignerRenderFrame {
				Sequence = ++frameSequence, Width = width, Height = height, Dpi = 1,
				Data = DesignerFrameCodec.EncodeDeflateBase64(pixels), RenderMs = stopwatch.Elapsed.TotalMilliseconds
			};
		}

		static bool TryGetBackground(FrameworkElement element, out System.Windows.Media.Color color)
		{
			Brush? brush = element switch {
				Panel panel => panel.Background,
				TextBlock text => text.Background,
				Border border => border.Background,
				// Template-generated Control backgrounds are deliberately omitted. In a headless
				// tree they can be materialized as local values despite not being authored in the
				// document, and would obscure sibling backgrounds with the wrong geometry.
				_ => null
			};
			if (brush is SolidColorBrush solid)
			{
				color = solid.Color;
				return true;
			}
			color = default;
			return false;
		}

		static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode node)
		{
			yield return node;
			foreach (var child in node.Children)
				foreach (var nested in Flatten(child))
					yield return nested;
		}

		static void PaintFallbackRect(byte[] pixels, int width, int height, DesignerElementNode node,
			System.Windows.Media.Color color)
		{
			var left = Math.Max(0, (int)Math.Floor(node.X));
			var top = Math.Max(0, (int)Math.Floor(node.Y));
			var right = Math.Min(width, (int)Math.Ceiling(node.X + node.Width));
			var bottom = Math.Min(height, (int)Math.Ceiling(node.Y + node.Height));
			for (var y = top; y < bottom; y++)
				for (var x = left; x < right; x++)
					PaintFallbackPixel(pixels, width, height, x, y, color.B, color.G, color.R);
		}

		static void PaintFallbackPixel(byte[] pixels, int width, int height, int x, int y, byte b, byte g, byte r)
		{
			if (x < 0 || y < 0 || x >= width || y >= height)
				return;
			var index = (y * width + x) * 4;
			pixels[index] = b;
			pixels[index + 1] = g;
			pixels[index + 2] = r;
			pixels[index + 3] = 255;
		}

		static void PaintFallbackOutline(byte[] pixels, int width, int height, DesignerElementNode node, byte b, byte g, byte r)
		{
			var left = Math.Max(0, (int)Math.Floor(node.X));
			var top = Math.Max(0, (int)Math.Floor(node.Y));
			var right = Math.Min(width - 1, (int)Math.Ceiling(node.X + node.Width) - 1);
			var bottom = Math.Min(height - 1, (int)Math.Ceiling(node.Y + node.Height) - 1);
			if (right < left || bottom < top) return;
			for (var x = left; x <= right; x++)
			{
				PaintFallbackPixel(pixels, width, height, x, top, b, g, r);
				PaintFallbackPixel(pixels, width, height, x, bottom, b, g, r);
			}
			for (var y = top; y <= bottom; y++)
			{
				PaintFallbackPixel(pixels, width, height, left, y, b, g, r);
				PaintFallbackPixel(pixels, width, height, right, y, b, g, r);
			}
		}

		long frameSequence;
	}
}
