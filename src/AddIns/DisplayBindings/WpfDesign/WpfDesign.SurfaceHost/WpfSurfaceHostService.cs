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
		// Tree rebuilds happen for every small placement edit. TypeDescriptor metadata is invariant
		// per CLR type, so do not reflect the same property/event table once per element per frame.
		static readonly Dictionary<Type, PropertyDescriptor[]> browsablePropertiesByType = new();
		static readonly Dictionary<Type, EventDescriptor[]> browsableEventsByType = new();

		static PropertyDescriptor[] BrowsableProperties(Type type)
		{
			if (!browsablePropertiesByType.TryGetValue(type, out var descriptors))
				browsablePropertiesByType[type] = descriptors = TypeDescriptor.GetProperties(type).Cast<PropertyDescriptor>().Where(descriptor => descriptor.IsBrowsable).ToArray();
			return descriptors;
		}

		static EventDescriptor[] BrowsableEvents(Type type)
		{
			if (!browsableEventsByType.TryGetValue(type, out var descriptors))
				browsableEventsByType[type] = descriptors = TypeDescriptor.GetEvents(type).Cast<EventDescriptor>().Where(descriptor => descriptor.IsBrowsable).ToArray();
			return descriptors;
		}
		readonly string expectedToken;
		readonly WpfHeadlessDispatcher dispatcher;
		readonly ManualResetEventSlim shutdown = new(false);

		string? sessionId;
		string? documentId;
		bool initialized;
		long version;

		XamlDesignContext? current;
		// The exact text `current` was parsed from, and whether the model's PositionXmlElement
		// line info still points into it (a full XmlDocument.Save rewrites that line info to the
		// saved output's coordinates, so after one the minimal patch can no longer be trusted).
		string parsedText = "";
		bool parsedPositionsValid;
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
		// GPU rendering is nonetheless the default - it is what produces the real design frame,
		// and the "GPU composition starting" line below (written BEFORE the blocking call, to
		// stderr, which is routed to OpenDevelop's Output pad) is the diagnostic that survives a
		// hang: if the designer stops responding, its Output pad names the exact stage that
		// stalled. Set OPENDEVELOP_WPF_GPU_RENDER=0 to force the bounded software fallback frame.
		static readonly bool portableGpuRenderingEnabled = !IsGpuRenderingDisabledByEnvironment();
		static bool IsGpuRenderingDisabledByEnvironment()
		{
			var value = Environment.GetEnvironmentVariable("OPENDEVELOP_WPF_GPU_RENDER");
			if (string.IsNullOrEmpty(value))
				return false;
			return value == "0"
				|| String.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
				|| String.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
				|| String.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
		}
		bool renderUnavailable = !portableGpuRenderingEnabled;
		bool fallbackAnnounced;
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
		public DesignerSessionState Open(DesignerDocumentRequest request)
			=> dispatcher.Dispatch(() => OpenCore(request.Snapshot));

		[JsonRpcMethod("session/update")]
		public DesignerSessionState Update(DesignerDocumentRequest request)
			=> dispatcher.Dispatch(() => OpenCore(request.Snapshot));

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
				var (appResources, _) = ParseAppResources(snapshot, loadSettings, typeFinder?.ProjectAssembly);
				// A custom control's own compiled BAML (e.g. WPFGallery's PageHeader, whose XAML uses
				// {StaticResource TitleTextBlockStyle} from Resources/PageStyles.xaml) resolves its
				// StaticResource lookups against Application.Current.Resources while it is being
				// instantiated during the document parse below - before it is connected to the root
				// and can see that root's Resources. So the app dictionary must also be installed
				// there, and it must be installed BEFORE the parse. The root.Resources merge further
				// down is still required for implicit styles on the design root (see the remarks on
				// ParseAppResources).
				InstallApplicationResources(appResources);
				// The page text is parsed exactly as the IDE sent it. It must never be rewritten here:
				// XamlDesignContext keeps the parsed XmlDocument as the document model, so anything
				// spliced into the text (app resources, rewritten pack URIs) was saved back into the
				// user's file on the next flush. App-level StaticResource keys resolve through the
				// parser's Application.Current.Resources fallback (installed just above), and
				// application-relative pack URIs are mapped to the project assembly at value-conversion
				// time by SurfaceTypeFinder.ConvertUriToLocalUri.
				using var stringReader = new StringReader(xaml);
				using var xmlReader = XmlReader.Create(stringReader);
				current = new XamlDesignContext(xmlReader, loadSettings);
				parsedText = xaml;
				parsedPositionsValid = true;
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
				// needed alongside) the Application.Current install above: this one is for implicit
				// styles at layout time, that one is for StaticResource at parse time.
				if (appResources != null && current.RootItem?.View is FrameworkElement appResourceRoot)
					appResourceRoot.Resources.MergedDictionaries.Add(appResources);
				version = snapshot.Version;
				RepairUnappliedStyles(current.RootItem?.View as FrameworkElement);
				RebuildTreeAndRender(state);
				// The parser reports recoverable errors (an unresolvable type, a bad attribute value)
				// to XamlErrorService instead of throwing, and can then produce no root item at all.
				// Surface them - accepting such a document silently returned a session with no
				// element tree and no reason.
				var parseErrors = current.Services.GetService<XamlErrorService>()?.Errors;
				if (parseErrors != null)
				{
					foreach (var error in parseErrors)
						state.Diagnostics.Add(new DesignerDiagnostic {
							Message = error.Message, FileName = snapshot.PrimaryFileName,
							Line = error.Line, Column = error.Column,
							// XamlErrorService identifies a point, not a token span. Preserve the
							// explicit one-character range rather than fabricating a wider location.
							EndColumn = error.Column > 0 ? error.Column + 1 : 0
						});
				}
				state.Accepted = current.RootItem != null;
				if (!state.Accepted)
					state.Error = parseErrors?.FirstOrDefault()?.Message ?? "The document has no designable root element.";
				state.RootType = current.RootItem?.ComponentType?.FullName ?? "";
			}
			catch (Exception e)
			{
				current = null;
				state.Accepted = false;
				state.Error = e.GetBaseException().Message;
				state.Diagnostics.Add(new DesignerDiagnostic { Message = state.Error, FileName = snapshot.PrimaryFileName });
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

		/// <summary>
		/// Re-applies any Style that XamlDom assigned but WPF refused to apply.
		///
		/// XamlDom does not build the <c>Binding</c> of a <c>MultiDataTrigger</c>/<c>DataTrigger</c>
		/// condition, so those arrive as null. WPF validates them when the Style is applied and
		/// throws ("Must have non-null value for 'Binding'", or an ArgumentNullException for 'key'
		/// from the condition's resource lookup). The exception happens inside the StyleProperty
		/// change callback, where XamlDom swallows it: the local value stays set, but
		/// FrameworkElement's internal style cache is never filled, so the element renders
		/// COMPLETELY UNSTYLED with no error anywhere.
		///
		/// WPFGallery's MainWindow is the reproducer: BorderlessButtonStyle carries a HighContrast
		/// MultiDataTrigger, so every caption button lost MinWidth/Background/Template, collapsed to
		/// the bare glyph width (14px) and painted nothing - the "white boxes" bug. Detect it by the
		/// tell-tale state (local value is a Style, but the Style property reads null), drop only
		/// the unusable trigger conditions, and force a real re-assignment so the callback runs.
		/// A design surface loses nothing that matters: those triggers are HighContrast/hover state.
		/// </summary>
		static void RepairUnappliedStyles(FrameworkElement? root)
		{
			if (root == null)
				return;
			try
			{
				var rebuilt = new Dictionary<Style, Style?>();
				var repaired = 0;
				foreach (var element in EnumerateLogicalTree(root))
				{
					var local = element.ReadLocalValue(FrameworkElement.StyleProperty);
					if (local is not Style style || element.Style != null)
						continue;
					var replacement = RebuildStyleWithoutUnusableTriggers(style, rebuilt);
					if (replacement == null)
						continue;
					try
					{
						// Assigning the value that is ALREADY the local value is a no-op, so the
						// change callback (the thing that actually applies the style) would not run.
						element.ClearValue(FrameworkElement.StyleProperty);
						element.Style = replacement;
						if (element.Style != null)
							repaired++;
					}
					catch (Exception applyError)
					{
						Console.Error.WriteLine("design-host: could not re-apply style on " +
							element.GetType().Name + ": " + applyError.GetBaseException().Message);
					}
				}
				if (repaired > 0)
					Console.Error.WriteLine($"design-host: re-applied {repaired} style(s) that WPF had rejected (unusable trigger bindings).");
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("design-host: style repair pass failed: " + e.GetBaseException().Message);
			}
		}

		/// <summary>
		/// Returns a copy of <paramref name="style"/> (and of its BasedOn chain) that omits the
		/// triggers WPF cannot validate. A copy rather than an edit because both Style.Triggers and
		/// its TriggerCollection are already sealed by the time the failed application returns
		/// ("After a 'TriggerCollection' is in use (sealed), it cannot be modified"). Setters and
		/// the surviving triggers are re-used as-is: they are immutable once sealed.
		/// Returns null when there is nothing to repair.
		/// </summary>
		static Style? RebuildStyleWithoutUnusableTriggers(Style? style, Dictionary<Style, Style?> cache)
		{
			if (style == null)
				return null;
			if (cache.TryGetValue(style, out var cached))
				return cached;
			// Guard against a cyclic BasedOn chain before recursing.
			cache[style] = null;

			var repairedBase = RebuildStyleWithoutUnusableTriggers(style.BasedOn, cache);
			var unusable = style.Triggers.Where(IsUnusableTrigger).ToList();
			if (unusable.Count == 0 && repairedBase == null)
				return null;

			var clone = new Style(style.TargetType, repairedBase ?? style.BasedOn);
			foreach (SetterBase setter in style.Setters)
				clone.Setters.Add(setter);
			foreach (TriggerBase trigger in style.Triggers)
			{
				if (!IsUnusableTrigger(trigger))
					clone.Triggers.Add(trigger);
			}
			foreach (var key in style.Resources.Keys)
				clone.Resources[key] = style.Resources[key];

			cache[style] = clone;
			return clone;
		}

		static bool IsUnusableTrigger(TriggerBase trigger) => trigger switch {
			System.Windows.MultiDataTrigger multi => multi.Conditions.Any(c => c.Binding == null),
			System.Windows.DataTrigger data => data.Binding == null,
			_ => false
		};

		static IEnumerable<FrameworkElement> EnumerateLogicalTree(FrameworkElement root)
		{
			yield return root;
			foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<FrameworkElement>())
				foreach (var nested in EnumerateLogicalTree(child))
					yield return nested;
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
				// Patch the parsed text rather than regenerating it, so a one-attribute edit is a
				// one-attribute change in the user's file (see MinimalXamlTextPatcher).
				var model = (current.RootItem as XamlDesignItem)?.XamlObject.XmlElement.OwnerDocument;
				if (parsedPositionsValid && model != null)
				{
					var patched = MinimalXamlTextPatcher.TryPatch(parsedText, model);
					if (patched != null)
						return patched;
				}
				parsedPositionsValid = false;
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
				DesignItemProperty? property;
				try
				{
					// An unknown name throws from the DDP lookup; reject it like any other bad edit.
					property = FindFrameworkAttachedProperty(propertyName) is { } attached
						? item.Properties.GetAttachedProperty(attached)
						: item.Properties[propertyName];
				}
				catch (ArgumentException e) { return NotFound(state, e.Message); }
				if (property == null)
					return NotFound(state, "Property not found: " + propertyName);
				try
				{
					// An image URI is written as the authored text: converting it here, without the
					// document's base URI, cannot resolve a relative path, and the converter's own
					// text for a loaded image would be an absolute one. DDP parses the attribute
					// text into the live value with the document's context.
					if (property.ReturnType != null && typeof(ImageSource).IsAssignableFrom(property.ReturnType) && !string.IsNullOrWhiteSpace(value))
						property.SetValue(value.Trim());
					else
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

		/// <summary>Resolves an <c>Owner.Member</c> name to a WPF framework attached property
		/// (<c>DockPanel.Dock</c>, <c>Grid.Row</c>...), so <c>design/set-property</c> can write it the
		/// way XAML spells it. Only System.Windows.Controls owners are recognised; any other name
		/// keeps the ordinary property lookup.</summary>
		static DependencyProperty? FindFrameworkAttachedProperty(string propertyName)
		{
			var dot = propertyName.IndexOf('.');
			if (dot <= 0 || dot != propertyName.LastIndexOf('.') || dot == propertyName.Length - 1)
				return null;
			var owner = typeof(FrameworkElement).Assembly.GetType("System.Windows.Controls." + propertyName.Substring(0, dot));
			var field = owner?.GetField(propertyName.Substring(dot + 1) + "Property", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
			return field?.GetValue(null) as DependencyProperty;
		}

		/// <summary>Sets or clears a design-time-only flag - <c>Hidden</c> (<c>d:IsHidden</c>) or
		/// <c>Locked</c> (<c>d:IsLocked</c>) - as the in-process Outline's eye and lock toggles do.
		/// Clearing resets the attribute rather than writing False.</summary>
		[JsonRpcMethod("design/set-design-time-flag")]
		public DesignerSessionState SetDesignTimeFlag(long baseVersion, string elementId, string flag, bool value)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item) || elementId.Length == 0 || item.View is not UIElement)
					return NotFound(state, "Not a design-time flaggable element: " + elementId);
				var dependencyProperty = flag switch {
					"Hidden" => ICSharpCode.WpfDesign.XamlDom.DesignTimeProperties.IsHiddenProperty,
					"Locked" => ICSharpCode.WpfDesign.XamlDom.DesignTimeProperties.IsLockedProperty,
					_ => null
				};
				if (dependencyProperty == null)
					return NotFound(state, "Unknown design-time flag: " + flag);
				try
				{
					var property = item.Properties.GetAttachedProperty(dependencyProperty);
					if (value)
						property.SetValue(true);
					else if (property.IsSet)
						property.Reset();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Visual Studio's Edit Style → Create Empty: adds <c>&lt;Style x:Key="ButtonStyle1"
		/// TargetType="Button"/&gt;</c> to the document root's Resources and points the element's Style
		/// at it, in one undo group. Nothing is copied from the design host's own theme (the reason
		/// "Edit a Copy" stays deferred). Refused for the root (its resources cannot style itself with a
		/// StaticResource) and for an element that already sets a Style.</summary>
		[JsonRpcMethod("design/create-empty-style")]
		public DesignerSessionState CreateEmptyStyle(long baseVersion, string elementId)
			=> CreateEmptyResource(baseVersion, elementId, "Style", "Style", () => new Style(),
				item => item.Component is FrameworkElement);

		/// <summary>Visual Studio's Edit Template → Create Empty: the same shape as
		/// <see cref="CreateEmptyStyle"/> for a Control's Template, so the control renders nothing until
		/// the user fills <c>&lt;ControlTemplate x:Key="ButtonTemplate1" TargetType="Button"/&gt;</c>.</summary>
		[JsonRpcMethod("design/create-empty-template")]
		public DesignerSessionState CreateEmptyTemplate(long baseVersion, string elementId)
			=> CreateEmptyResource(baseVersion, elementId, "Template", "Template", () => new ControlTemplate(),
				item => item.Component is Control);

		/// <summary>Edit Style → Create Based On Current: for an element whose Style is
		/// <c>{StaticResource K}</c>, adds <c>&lt;Style x:Key="ButtonStyle1" TargetType="Button"
		/// BasedOn="{StaticResource K}"/&gt;</c> to the root's Resources and points the element at it.
		/// The project-authored style is inherited, not copied, so nothing comes from the design
		/// host's theme (the reason "Edit a Copy" stays deferred).</summary>
		[JsonRpcMethod("design/create-based-on-style")]
		public DesignerSessionState CreateBasedOnStyle(long baseVersion, string elementId)
			=> CreateEmptyResource(baseVersion, elementId, "Style", "Style", () => new Style(),
				item => item.Component is FrameworkElement, basedOnCurrent: true);

		/// <summary>Edit Style → Edit a Copy, from the <b>project's</b> selected design theme (never the
		/// child runtime's built-in theme, which under LibreWPF is not the Microsoft one the project
		/// ships with): copies the theme's implicit Style for the element's type
		/// (<c>TargetType="Button"</c> / <c>{x:Type Button}</c>, no x:Key) into the root's Resources
		/// as <c>ButtonStyle1</c> and points the element at it, in one undo group. Refused when no
		/// project theme is selected, the theme has no implicit style for the type, or the element
		/// already sets a Style.</summary>
		[JsonRpcMethod("design/copy-theme-style")]
		public DesignerSessionState CopyThemeStyle(long baseVersion, string elementId)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId ?? "", out var item) || current?.RootItem is not XamlDesignItem root || item == root
					|| item.Component is not FrameworkElement)
					return NotFound(state, "Edit a Copy needs a non-root element: " + elementId);
				var styleProperty = item.Properties["Style"];
				if (styleProperty.IsSet)
					return NotFound(state, "The element already sets a Style.");
				if (appliedThemeName == null || themeSources == null || !themeSources.TryGetValue(appliedThemeName, out var resourceName))
					return NotFound(state, "Select a project design theme first; Edit a Copy does not copy the designer's built-in theme.");
				var type = item.Component.GetType();
				System.Xml.Linq.XElement? source;
				try
				{
					using var stream = projectAssembly!.GetManifestResourceStream(resourceName);
					if (stream == null)
						return NotFound(state, "Embedded theme resource not found: " + resourceName);
					var theme = System.Xml.Linq.XDocument.Load(stream);
					System.Xml.Linq.XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
					bool IsTypeExtension(string? text) => text != null
						&& System.Text.RegularExpressions.Regex.IsMatch(text, @"^\{x:Type\s+(\w+:)?" + type.Name + @"\s*\}$");
					bool Names(string? text) => text != null && (text == type.Name || text.EndsWith(":" + type.Name, StringComparison.Ordinal)
						|| IsTypeExtension(text));
					// Implicit: no x:Key and a matching TargetType, or keyed by {x:Type T} (what an
					// implicit style's key is); a style keyed by a plain name is an explicit style.
					source = theme.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Style"
						&& (e.Attribute(xaml + "Key") is not { } k ? Names((string?)e.Attribute("TargetType")) : IsTypeExtension(k.Value)));
					if (source == null)
						return NotFound(state, "The '" + appliedThemeName + "' theme has no implicit Style for " + type.Name + ".");
					source = new System.Xml.Linq.XElement(source);
					// Keep every namespace the theme root declares so prefixes in the copy resolve.
					foreach (var declaration in theme.Root!.Attributes().Where(a => a.IsNamespaceDeclaration))
						if (source.Attribute(declaration.Name) == null)
							source.Add(new System.Xml.Linq.XAttribute(declaration));
					source.SetAttributeValue(xaml + "Key", null);
				}
				catch (Exception e) { return NotFound(state, "Failed to read the theme: " + e.GetBaseException().Message); }
				var taken = new HashSet<string>(ResourceKeys(item, _ => true), StringComparer.Ordinal);
				var index = 1;
				while (taken.Contains(type.Name + "Style" + index))
					index++;
				var key = type.Name + "Style" + index;
				try
				{
					using var changeGroup = item.OpenGroup("Edit a Copy of " + type.Name + " Style");
					var parsed = ICSharpCode.WpfDesign.XamlDom.XamlParser.ParseSnippet(root.XamlObject, source.ToString(System.Xml.Linq.SaveOptions.DisableFormatting), current.ParserSettings);
					if (parsed == null || ((XamlComponentService)item.Services.Component).RegisterXamlComponentRecursive(parsed) is not { } style)
						return NotFound(state, "The theme's Style could not be parsed.");
					root.Properties["Resources"].CollectionElements.Add(style);
					style.Key = key;
					styleProperty.SetValue(new StaticResourceExtension());
					styleProperty.Value!.Properties["ResourceKey"].SetValue(key);
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		DesignerSessionState CreateEmptyResource(long baseVersion, string elementId, string propertyName, string keySuffix,
			Func<object> create, Func<DesignItem, bool> accepts, bool basedOnCurrent = false)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				var title = basedOnCurrent ? "Create " + propertyName + " Based On Current" : "Create Empty " + propertyName;
				if (!pathToItem.TryGetValue(elementId ?? "", out var item) || current?.RootItem is not { } root || item == root
					|| !accepts(item))
					return NotFound(state, title + " is not available for: " + elementId);
				var targetProperty = item.Properties[propertyName];
				string? baseKey = null;
				if (basedOnCurrent)
				{
					baseKey = targetProperty.IsSet && targetProperty.Value?.Component is StaticResourceExtension
						? targetProperty.Value.Properties["ResourceKey"].ValueOnInstance as string : null;
					if (string.IsNullOrEmpty(baseKey))
						return NotFound(state, "The element's " + propertyName + " is not a {StaticResource key} reference.");
				}
				else if (targetProperty.IsSet)
					return NotFound(state, "The element already sets a " + propertyName + ".");
				var type = item.Component.GetType();
				var taken = new HashSet<string>(ResourceKeys(item, _ => true), StringComparer.Ordinal);
				var index = 1;
				while (taken.Contains(type.Name + keySuffix + index))
					index++;
				var key = type.Name + keySuffix + index;
				try
				{
					using var changeGroup = item.OpenGroup(title);
					var resource = item.Services.Component.RegisterComponentForDesigner(create());
					root.Properties["Resources"].CollectionElements.Add(resource);
					// Only once the resource is in the document can DDP resolve the x: prefix for its key
					// (set earlier it is written as a plain, invalid Key attribute); TargetType is a
					// property, not a constructor argument DDP would serialize.
					resource.Key = key;
					// As text, spelled like the element's own tag ("Button", "local:MyButton"), which
					// the document already proves resolves; a Type value would serialize as an
					// invalid <RuntimeType/> element.
					var tag = ((XamlDesignItem)item).XamlObject.XmlElement;
					resource.Properties["TargetType"].SetValue(string.IsNullOrEmpty(tag.Prefix) ? tag.LocalName : tag.Prefix + ":" + tag.LocalName);
					if (baseKey != null)
					{
						resource.Properties["BasedOn"].SetValue(new StaticResourceExtension());
						resource.Properties["BasedOn"].Value!.Properties["ResourceKey"].SetValue(baseKey);
						targetProperty.Reset();
					}
					targetProperty.SetValue(new StaticResourceExtension());
					targetProperty.Value!.Properties["ResourceKey"].SetValue(key);
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Replaces a Brush-typed property's value with the brush the Properties-pad brush
		/// editor built (the in-process BrushEditor's solid/linear/radial choices), as one change.
		/// A solid brush is written as attribute text; a gradient as a property element.</summary>
		[JsonRpcMethod("design/set-brush")]
		public DesignerSessionState SetBrush(long baseVersion, string elementId, string propertyName, DesignerBrushInfo brush)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				DesignItemProperty? property;
				try
				{
					property = FindFrameworkAttachedProperty(propertyName) is { } attached
						? item.Properties.GetAttachedProperty(attached)
						: item.Properties[propertyName];
				}
				catch (ArgumentException e) { return NotFound(state, e.Message); }
				if (property?.ReturnType == null || !typeof(Brush).IsAssignableFrom(property.ReturnType))
					return NotFound(state, "Not a Brush property: " + propertyName);
				// A drawing or visual brush keeps its content: only its tile settings are rewritten, in place.
				if (brush.Kind is "Drawing" or "Visual")
				{
					if (property.Value is not { } existing
						|| !(brush.Kind == "Drawing" ? existing.Component is DrawingBrush : existing.Component is VisualBrush))
						return NotFound(state, "The property does not hold a " + brush.Kind.ToLowerInvariant() + " brush.");
					if (!string.IsNullOrEmpty(brush.Stretch) && !Enum.IsDefined(typeof(Stretch), brush.Stretch)
						|| !(brush.Opacity >= 0 && brush.Opacity <= 1))
						return NotFound(state, "A tile brush needs a Stretch value and an opacity from 0 to 1.");
					if (TileSettingsError(brush) is { } contentTileError)
						return NotFound(state, contentTileError);
					if (BrushTransformsError(brush) is { } contentTransformError)
						return NotFound(state, contentTransformError);
					try
					{
						using var contentGroup = item.OpenGroup("Set " + propertyName);
						ApplyTileSettings(existing, brush);
						ApplyBrushTransforms(item, existing, brush);
						contentGroup.Commit();
					}
					catch (Exception e) when (e is ArgumentException or FormatException or NotSupportedException or InvalidOperationException or OverflowException)
					{
						return NotFound(state, e.GetBaseException().Message);
					}
					RebuildTreeAndRender(state);
					state.Accepted = true;
					return state;
				}
				// Validate against the dictionaries as they are now, not a previous rebuild's cache.
				brushKeysByScope.Clear();
				colorKeysByScope.Clear();
				if (brush.Kind == "Resource" && !BrushResourceKeys(item).Contains(brush.ResourceKey))
					return NotFound(state, "Not a Brush resource in scope: " + brush.ResourceKey);
				if (brush.Kind == "ColorResource" && !ColorResourceKeys(item).Contains(brush.ResourceKey))
					return NotFound(state, "Not a Color resource in scope: " + brush.ResourceKey);
				if (brush.Kind == "System" && !IsSystemBrushKey(brush.ResourceKey))
					return NotFound(state, "Not a SystemColors brush key: " + brush.ResourceKey);
				if (brush.Kind == "Image" && (string.IsNullOrWhiteSpace(brush.ImageSource)
					|| !string.IsNullOrEmpty(brush.Stretch) && !Enum.IsDefined(typeof(Stretch), brush.Stretch)
					|| !(brush.Opacity >= 0 && brush.Opacity <= 1)))
					return NotFound(state, "An image brush needs an image source, a Stretch value and an opacity from 0 to 1.");
				if (brush.Kind == "Image" && TileSettingsError(brush) is { } tileError)
					return NotFound(state, tileError);
				if (BrushTransformsError(brush) is { } transformError)
					return NotFound(state, transformError);
				try
				{
					var value = brush.Kind is "Resource" or "System" or "Image" or "ColorResource" ? null : CreateBrush(brush);
					using var changeGroup = item.OpenGroup("Set " + propertyName);
					if (property.IsSet)
						property.Reset();
					if (brush.Kind == "Resource")
					{
						property.SetValue(new StaticResourceExtension());
						property.Value!.Properties["ResourceKey"].SetValue(brush.ResourceKey);
					}
					// A system brush follows theme changes, so it is a DynamicResource of the
					// SystemColors key, as Visual Studio writes it.
					// An image brush keeps its ImageSource as the authored URI text (resolved by the
					// parser against the document, as an Image.Source is), and writes only
					// non-default settings.
					// A color resource is a SolidColorBrush element whose Color references the key.
					else if (brush.Kind == "ColorResource")
					{
						var solid = item.Services.Component.RegisterComponentForDesigner(new SolidColorBrush());
						var reference = item.Services.Component.RegisterComponentForDesigner(new StaticResourceExtension());
						reference.Properties["ResourceKey"].SetValue(brush.ResourceKey);
						solid.Properties["Color"].SetValue(reference);
						if (brush.Opacity != 1)
							solid.Properties["Opacity"].SetValue(brush.Opacity);
						property.SetValue(solid);
					}
					else if (brush.Kind == "Image")
					{
						var image = item.Services.Component.RegisterComponentForDesigner(new ImageBrush());
						image.Properties["ImageSource"].SetValue(brush.ImageSource.Trim());
						if (!string.IsNullOrEmpty(brush.Stretch) && brush.Stretch != nameof(Stretch.Fill))
							image.Properties["Stretch"].SetValue(Enum.Parse<Stretch>(brush.Stretch));
						if (brush.Opacity != 1)
							image.Properties["Opacity"].SetValue(brush.Opacity);
						// Tile settings are written as the authored text, only when set.
						foreach (var (name, text) in new[] { ("TileMode", brush.TileMode), ("AlignmentX", brush.AlignmentX), ("AlignmentY", brush.AlignmentY),
							("Viewport", brush.Viewport), ("ViewportUnits", brush.ViewportUnits), ("Viewbox", brush.Viewbox), ("ViewboxUnits", brush.ViewboxUnits) })
							if (!string.IsNullOrWhiteSpace(text))
								image.Properties[name].SetValue(text.Trim());
						property.SetValue(image);
					}
					else if (brush.Kind == "System")
					{
						property.SetValue(new DynamicResourceExtension());
						var key = item.Services.Component.RegisterComponentForDesigner(new System.Windows.Markup.StaticExtension());
						key.Properties["Member"].SetValue("SystemColors." + brush.ResourceKey);
						property.Value!.Properties["ResourceKey"].SetValue(key);
					}
					// A solid brush with a transform has to be an element (an attribute cannot hold a transform).
					else if (brush.Kind == "Solid" && (brush.Transform?.Count > 0 || brush.RelativeTransform?.Count > 0))
					{
						var solidItem = item.Services.Component.RegisterComponentForDesigner(new SolidColorBrush());
						solidItem.Properties["Color"].SetValue((System.Windows.Media.Color)ColorConverter.ConvertFromString(brush.Color));
						if (brush.Opacity != 1)
							solidItem.Properties["Opacity"].SetValue(brush.Opacity);
						property.SetValue(solidItem);
					}
					// "No brush" is an explicit {x:Null}, not a reset: it overrides a style's brush.
					else if (value == null)
						property.SetValue(item.Services.Component.RegisterComponentForDesigner(new System.Windows.Markup.NullExtension()));
					else
						property.SetValue(value);
					// A brush with pixels of its own carries its transforms; they are set on the item the property now holds.
					if (brush.Kind is not ("Null" or "Resource" or "System") && property.Value is { } writtenBrush)
						ApplyBrushTransforms(item, writtenBrush, brush);
					changeGroup.Commit();
				}
				catch (Exception e) when (e is ArgumentException or FormatException or NotSupportedException or InvalidOperationException or OverflowException)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Edits the path of an existing <see cref="System.Windows.Data.Binding"/> through
		/// its nested design item. Unlike <see cref="SetProperty"/>, this never hands a markup
		/// extension string to a string-valued dependency property: doing that serializes the braces
		/// literally and destroys the Binding. The child owns both type validation and the nested
		/// object-model mutation, so rejection leaves the source untouched.</summary>
		[JsonRpcMethod("design/set-binding-path")]
		public DesignerSessionState SetBindingPath(long baseVersion, string elementId, string propertyName, string path, int childIndex = -1)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				if (String.IsNullOrWhiteSpace(propertyName))
					return NotFound(state, "A Binding property name is required.");
				var property = item.Properties[propertyName];
				if (property?.Value is not DesignItem bindingItem)
					return NotFound(state, "Property is not an editable Binding: " + propertyName);
				if (childIndex >= 0)
				{
					var children = bindingItem.Properties["Bindings"]?.CollectionElements;
					if (children == null || childIndex >= children.Count
						|| children[childIndex].Component is not System.Windows.Data.Binding)
						return NotFound(state, "Binding child is not an editable Binding: " + propertyName);
					bindingItem = children[childIndex];
				}
				else if (bindingItem.Component is not System.Windows.Data.Binding)
					return NotFound(state, "Property is not an editable Binding: " + propertyName);
				try
				{
					// Mutate Path on the Binding's nested DesignItem, not Binding.Path on its live CLR
					// instance. The former updates XamlDom and participates in native undo/flush.
					bindingItem.Properties["Path"].SetValue(path ?? "");
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		[JsonRpcMethod("design/set-binding-mode")]
		public DesignerSessionState SetBindingMode(long baseVersion, string elementId, string propertyName, string mode, int childIndex = -1)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale) return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item)) return NotFound(state, "Element not found: " + elementId);
				if (!Enum.IsDefined(typeof(System.Windows.Data.BindingMode), mode)
					|| !Enum.TryParse<System.Windows.Data.BindingMode>(mode, ignoreCase: false, out var parsed))
					return NotFound(state, "Invalid Binding mode: " + mode);
				DesignItem? bindingItem;
				if (childIndex >= 0)
				{
					if (!TryGetEditableBindingItems(item, propertyName, out var bindingsProperty) || bindingsProperty == null
						|| childIndex >= bindingsProperty.CollectionElements.Count)
						return NotFound(state, "Binding child is not editable: " + propertyName);
					bindingItem = bindingsProperty.CollectionElements[childIndex];
				}
				else
					bindingItem = item.Properties[propertyName]?.Value as DesignItem;
				if (bindingItem?.Component is not System.Windows.Data.Binding)
					return NotFound(state, "Property is not an editable Binding: " + propertyName);
				try { bindingItem.Properties["Mode"].SetValue(parsed); }
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state); state.Accepted = true; return state;
			});

		[JsonRpcMethod("design/set-binding-update-source-trigger")]
		public DesignerSessionState SetBindingUpdateSourceTrigger(long baseVersion, string elementId, string propertyName, string trigger, int childIndex = -1)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale) return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item)) return NotFound(state, "Element not found: " + elementId);
				if (!Enum.IsDefined(typeof(System.Windows.Data.UpdateSourceTrigger), trigger)
					|| !Enum.TryParse<System.Windows.Data.UpdateSourceTrigger>(trigger, ignoreCase: false, out var parsed))
					return NotFound(state, "Invalid Binding UpdateSourceTrigger: " + trigger);
				DesignItem? bindingItem;
				if (childIndex >= 0)
				{
					if (!TryGetEditableBindingItems(item, propertyName, out var bindingsProperty) || bindingsProperty == null
						|| childIndex >= bindingsProperty.CollectionElements.Count)
						return NotFound(state, "Binding child is not editable: " + propertyName);
					bindingItem = bindingsProperty.CollectionElements[childIndex];
				}
				else
					bindingItem = item.Properties[propertyName]?.Value as DesignItem;
				if (bindingItem?.Component is not System.Windows.Data.Binding)
					return NotFound(state, "Property is not an editable Binding: " + propertyName);
				try { bindingItem.Properties["UpdateSourceTrigger"].SetValue(parsed); }
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state); state.Accepted = true; return state;
			});

		[JsonRpcMethod("design/set-binding-element-name")]
		public DesignerSessionState SetBindingElementName(long baseVersion, string elementId, string propertyName, string elementName, int childIndex = -1)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale) return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item)) return NotFound(state, "Element not found: " + elementId);
				DesignItem? bindingItem;
				if (childIndex >= 0)
				{
					if (!TryGetEditableBindingItems(item, propertyName, out var bindingsProperty) || bindingsProperty == null
						|| childIndex >= bindingsProperty.CollectionElements.Count)
						return NotFound(state, "Binding child is not editable: " + propertyName);
					bindingItem = bindingsProperty.CollectionElements[childIndex];
				}
				else
					bindingItem = item.Properties[propertyName]?.Value as DesignItem;
				if (bindingItem is null || bindingItem.Component is not System.Windows.Data.Binding binding
					|| !HasUnsetBindingSource(bindingItem) || binding.RelativeSource != null)
					return NotFound(state, "Property is not an editable ElementName Binding: " + propertyName);
				if (!string.IsNullOrWhiteSpace(elementName))
				{
					elementName = elementName.Trim();
					try { XmlConvert.VerifyNCName(elementName); }
					catch (Exception) { return NotFound(state, "Invalid Binding ElementName: " + elementName); }
				}
				try
				{
					if (string.IsNullOrWhiteSpace(elementName))
						bindingItem.Properties["ElementName"].Reset();
					else
						bindingItem.Properties["ElementName"].SetValue(elementName);
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state); state.Accepted = true; return state;
			});

		[JsonRpcMethod("design/set-binding-relative-source")]
		public DesignerSessionState SetBindingRelativeSource(long baseVersion, string elementId, string propertyName, string relativeSource)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale) return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item)) return NotFound(state, "Element not found: " + elementId);
				var property = item.Properties[propertyName];
				if (property?.Value is not DesignItem bindingItem || bindingItem.Component is not System.Windows.Data.Binding binding
					|| !HasUnsetBindingSource(bindingItem) || (binding.RelativeSource != null
						&& binding.RelativeSource.Mode is not (System.Windows.Data.RelativeSourceMode.Self or System.Windows.Data.RelativeSourceMode.TemplatedParent)))
					return NotFound(state, "Property is not an editable RelativeSource Binding: " + propertyName);
				try
				{
					switch (relativeSource)
					{
						case "": bindingItem.Properties["RelativeSource"].Reset(); break;
						case "Self":
							bindingItem.Properties["ElementName"].Reset();
							bindingItem.Properties["RelativeSource"].SetValue(System.Windows.Data.RelativeSource.Self);
							break;
						case "TemplatedParent":
							bindingItem.Properties["ElementName"].Reset();
							bindingItem.Properties["RelativeSource"].SetValue(System.Windows.Data.RelativeSource.TemplatedParent);
							break;
						default: return NotFound(state, "Invalid Binding RelativeSource: " + relativeSource);
					}
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state); state.Accepted = true; return state;
			});

		/// <summary>Edits only the level of an existing <c>RelativeSource FindAncestor</c> Binding.
		/// Switching an ordinary Binding to FindAncestor would also require an ancestor type, so that
		/// larger edit is deliberately outside this narrow, lossless mutation.</summary>
		[JsonRpcMethod("design/set-binding-ancestor-level")]
		public DesignerSessionState SetBindingAncestorLevel(long baseVersion, string elementId, string propertyName, int level)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale) return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item)) return NotFound(state, "Element not found: " + elementId);
				var property = item.Properties[propertyName];
				if (property?.Value is not DesignItem bindingItem || bindingItem.Component is not System.Windows.Data.Binding binding
					|| binding.RelativeSource?.Mode != System.Windows.Data.RelativeSourceMode.FindAncestor
					|| binding.RelativeSource.AncestorType == null)
					return NotFound(state, "Property is not an editable FindAncestor Binding: " + propertyName);
				if (level < 1 || level > 1000)
					return NotFound(state, "Binding FindAncestor level must be between 1 and 1000.");
				try { bindingItem.Properties["RelativeSource"].Value!.Properties["AncestorLevel"].SetValue(level); }
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state); state.Accepted = true; return state;
			});

		/// <summary>Sets the source of an existing Binding only when XamlDom still proves that it is
		/// unset or an authored <c>{StaticResource key}</c>.  Binding.Source is an object slot: the
		/// runtime Binding can flatten an inline object or <c>x:Static</c> form to an apparent null,
		/// so those forms must never be guessed or overwritten through a resource picker.</summary>
		[JsonRpcMethod("design/set-binding-source")]
		public DesignerSessionState SetBindingSource(long baseVersion, string elementId, string propertyName, string resourceKey)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale) return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item)) return NotFound(state, "Element not found: " + elementId);
				var property = item.Properties[propertyName];
				if (property?.Value is not DesignItem bindingItem || bindingItem.Component is not System.Windows.Data.Binding binding
					|| !CanEditBindingSource(binding) || !TryGetStaticResourceBindingSource(bindingItem.Properties["Source"], out _))
					return NotFound(state, "Property is not an editable StaticResource Binding Source: " + propertyName);
				if (!string.IsNullOrEmpty(resourceKey) && !BindingSourceResourceKeys(item).Contains(resourceKey))
					return NotFound(state, "Not a resource in scope: " + resourceKey);
				try
				{
					var source = bindingItem.Properties["Source"];
					if (string.IsNullOrEmpty(resourceKey))
						source.Reset();
					else
					{
						bindingItem.Properties["ElementName"].Reset();
						bindingItem.Properties["RelativeSource"].Reset();
						source.SetValue(new StaticResourceExtension());
						source.Value!.Properties["ResourceKey"].SetValue(resourceKey);
					}
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state); state.Accepted = true; return state;
			});

		[JsonRpcMethod("design/set-binding-notify-on-validation-error")]
		public DesignerSessionState SetBindingNotifyOnValidationError(long baseVersion, string elementId, string propertyName, bool notify)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale) return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item)) return NotFound(state, "Element not found: " + elementId);
				var property = item.Properties[propertyName];
				if (property?.Value is not DesignItem bindingItem || bindingItem.Component is not System.Windows.Data.Binding)
					return NotFound(state, "Property is not an editable Binding: " + propertyName);
				try
				{
					if (notify)
						bindingItem.Properties["NotifyOnValidationError"].SetValue(true);
					else
						bindingItem.Properties["NotifyOnValidationError"].Reset();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state); state.Accepted = true; return state;
			});

		// These are the Binding's boolean options that XamlDom represents as independent
		// scalar properties.  They are deliberately a closed list: a generic nested-property
		// setter would let the Properties pad overwrite object-valued Binding members whose
		// authored form we cannot prove lossless.
		static readonly string[] bindingValidationOptionNames = {
			"ValidatesOnDataErrors", "ValidatesOnExceptions", "ValidatesOnNotifyDataErrors",
			"BindsDirectlyToSource", "IsAsync", "NotifyOnSourceUpdated", "NotifyOnTargetUpdated"
		};
		static readonly HashSet<string> bindingValidationOptions = new(bindingValidationOptionNames, StringComparer.Ordinal);

		[JsonRpcMethod("design/set-binding-validation-option")]
		public DesignerSessionState SetBindingValidationOption(long baseVersion, string elementId, string propertyName, string optionName, bool enabled)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale) return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item)) return NotFound(state, "Element not found: " + elementId);
				if (!bindingValidationOptions.Contains(optionName)) return NotFound(state, "Unsupported Binding validation option: " + optionName);
				var property = item.Properties[propertyName];
				if (property?.Value is not DesignItem bindingItem || bindingItem.Component is not System.Windows.Data.Binding)
					return NotFound(state, "Property is not an editable Binding: " + propertyName);
				try
				{
					if (enabled)
						bindingItem.Properties[optionName].SetValue(true);
					else
						bindingItem.Properties[optionName].Reset();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state); state.Accepted = true; return state;
			});

		static readonly string[] bindingTextOptionNames = { "StringFormat", "FallbackValue", "TargetNullValue", "ConverterParameter", "BindingGroupName", "XPath" };
		static readonly HashSet<string> bindingTextOptions = new(bindingTextOptionNames, StringComparer.Ordinal);

		/// <summary>Sets a whitelisted text option on an existing Binding. An empty value resets the
		/// nested property so no redundant empty assignment is serialized; a non-string authored value
		/// (for example an x:Static FallbackValue) is never overwritten through this text path.</summary>
		[JsonRpcMethod("design/set-binding-text-option")]
		public DesignerSessionState SetBindingTextOption(long baseVersion, string elementId, string propertyName, string optionName, string value)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale) return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item)) return NotFound(state, "Element not found: " + elementId);
				if (!bindingTextOptions.Contains(optionName)) return NotFound(state, "Unsupported Binding text option: " + optionName);
				var property = item.Properties[propertyName];
				if (property?.Value is not DesignItem bindingItem || bindingItem.Component is not System.Windows.Data.Binding)
					return NotFound(state, "Property is not an editable Binding: " + propertyName);
				var option = bindingItem.Properties[optionName];
				if (!IsTextBindingOption(option))
					return NotFound(state, "Binding " + optionName + " is not a text value");
				try
				{
					if (string.IsNullOrEmpty(value))
						option.Reset();
					else
						option.SetValue(value);
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state); state.Accepted = true; return state;
			});

		// An unset FallbackValue/TargetNullValue reports DependencyProperty.UnsetValue, not null. An
		// authored text argument parsed from markup arrives wrapped in a string DesignItem, whereas a
		// nested markup extension (x:Static, StaticResource) has a non-string component.
		static bool IsTextBindingOption(DesignItemProperty option)
			=> option != null
				&& (option.Value == null || option.Value.Component is string)
				&& (option.ValueOnInstance is null or string || option.ValueOnInstance == DependencyProperty.UnsetValue);

		/// <summary>Sets an existing Binding's Converter to <c>{StaticResource key}</c>, where the key
		/// must name an IValueConverter resource in the element's scope; an empty key resets it.
		/// A converter authored in another form (an inline object, DynamicResource) is never
		/// overwritten through this path.</summary>
		[JsonRpcMethod("design/set-binding-converter")]
		public DesignerSessionState SetBindingConverter(long baseVersion, string elementId, string propertyName, string resourceKey)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale) return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item)) return NotFound(state, "Element not found: " + elementId);
				var property = item.Properties[propertyName];
				if (property?.Value is not DesignItem bindingItem || bindingItem.Component is not System.Windows.Data.Binding)
					return NotFound(state, "Property is not an editable Binding: " + propertyName);
				var converter = bindingItem.Properties["Converter"];
				if (!TryGetStaticResourceConverterKey(converter, out _))
					return NotFound(state, "Binding Converter is not a StaticResource reference");
				if (!string.IsNullOrEmpty(resourceKey) && !ConverterResourceKeys(item).Contains(resourceKey))
					return NotFound(state, "Not an IValueConverter resource in scope: " + resourceKey);
				try
				{
					if (string.IsNullOrEmpty(resourceKey))
						converter.Reset();
					else
					{
						converter.SetValue(new StaticResourceExtension());
						converter.Value!.Properties["ResourceKey"].SetValue(resourceKey);
					}
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state); state.Accepted = true; return state;
			});

		/// <summary>The current Converter's StaticResource key ("" when unset). False for any other
		/// authored form, which must stay read-only.</summary>
		static bool TryGetStaticResourceConverterKey(DesignItemProperty converter, out string key)
		{
			key = "";
			if (!converter.IsSet)
				return true;
			if (converter.Value?.Component is StaticResourceExtension && converter.Value.Properties["ResourceKey"].ValueOnInstance is string resourceKey)
			{
				key = resourceKey;
				return true;
			}
			return false;
		}

		static bool CanEditBindingSource(System.Windows.Data.Binding binding)
			=> binding.RelativeSource == null
				|| binding.RelativeSource.Mode is System.Windows.Data.RelativeSourceMode.Self or System.Windows.Data.RelativeSourceMode.TemplatedParent;

		/// <summary>Unlike the runtime Binding.Source value, XamlDom's set bit preserves whether an
		/// authored source exists at all. ElementName and RelativeSource must not overwrite an inline,
		/// x:Static, DynamicResource, or StaticResource source that happens to look null at runtime.</summary>
		static bool HasUnsetBindingSource(DesignItem bindingItem)
			=> bindingItem.Properties["Source"] is { IsSet: false };

		/// <summary>Reads source syntax from the nested XamlDom property, not the runtime Binding.Source.
		/// The latter cannot distinguish an unset source from several authored object forms.</summary>
		static bool TryGetStaticResourceBindingSource(DesignItemProperty source, out string key)
		{
			key = "";
			if (!source.IsSet)
				return true;
			if (source.Value?.Component is StaticResourceExtension && source.Value.Properties["ResourceKey"].ValueOnInstance is string resourceKey)
			{
				key = resourceKey;
				return true;
			}
			return false;
		}

		static List<string> BindingSourceResourceKeys(DesignItem item)
			=> ResourceKeys(item, _ => true).OrderBy(key => key, StringComparer.Ordinal).ToList();

		/// <summary>String keys of IValueConverter resources visible to <paramref name="item"/>, in name
		/// order (a ResourceDictionary enumerates its keys in hash order).</summary>
		static List<string> ConverterResourceKeys(DesignItem item)
			=> ResourceKeys(item, value => value is System.Windows.Data.IValueConverter).OrderBy(key => key, StringComparer.Ordinal).ToList();

		/// <summary>Brush resource keys per resource scope - the nearest element (inclusive) that has
		/// Resources of its own, or null for only the application's - for one tree rebuild: elements
		/// sharing a scope share the walk. <see cref="RebuildTreeAndRender"/> clears it, since an edit
		/// can change any dictionary.</summary>
		static readonly Dictionary<object, List<string>> brushKeysByScope = new();
		static readonly Dictionary<object, List<string>> colorKeysByScope = new();
		static readonly object applicationScope = new();

		/// <summary>String keys of Brush resources visible to <paramref name="item"/>, in name order
		/// (a ResourceDictionary enumerates its keys in hash order).</summary>
		static List<string> BrushResourceKeys(DesignItem item) => ScopedResourceKeys(item, brushKeysByScope, value => value is Brush);

		/// <summary>String keys of Color resources visible to <paramref name="item"/>, in name order:
		/// the brush editor's color resources (a SolidColorBrush whose Color is one of them).</summary>
		static List<string> ColorResourceKeys(DesignItem item) => ScopedResourceKeys(item, colorKeysByScope, value => value is System.Windows.Media.Color);

		static List<string> ScopedResourceKeys(DesignItem item, Dictionary<object, List<string>> cache, Func<object?, bool> matches)
		{
			object scope = applicationScope;
			for (var current = item; current != null; current = current.Parent)
			{
				if ((current.Component as FrameworkElement)?.Resources is { } resources
					&& (resources.Count > 0 || resources.MergedDictionaries.Count > 0))
				{
					scope = current;
					break;
				}
			}
			if (!cache.TryGetValue(scope, out var keys))
				cache[scope] = keys = ResourceKeys(item, matches).OrderBy(key => key, StringComparer.Ordinal).ToList();
			return keys;
		}

		/// <summary>String keys of the resources visible to <paramref name="item"/> whose value matches:
		/// its own and its ancestors' Resources (merged dictionaries included), then the application's.</summary>
		static List<string> ResourceKeys(DesignItem item, Func<object?, bool> matches)
		{
			var keys = new List<string>();
			void Collect(ResourceDictionary? dictionary)
			{
				if (dictionary == null)
					return;
				foreach (var key in dictionary.Keys)
				{
					if (key is string name && !keys.Contains(name) && matches(dictionary[key]))
						keys.Add(name);
				}
				foreach (var merged in dictionary.MergedDictionaries)
					Collect(merged);
			}
			for (var current = item; current != null; current = current.Parent)
				Collect((current.Component as FrameworkElement)?.Resources);
			Collect(Application.Current?.Resources);
			return keys;
		}

		/// <summary>Sets the bounded millisecond debounce delay on an existing Binding. Zero is
		/// represented as a design-model reset so the default is not redundantly serialized.</summary>
		[JsonRpcMethod("design/set-binding-delay")]
		public DesignerSessionState SetBindingDelay(long baseVersion, string elementId, string propertyName, int delay)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale) return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item)) return NotFound(state, "Element not found: " + elementId);
				if (delay < 0) return NotFound(state, "Binding Delay must be non-negative: " + delay);
				var property = item.Properties[propertyName];
				if (property?.Value is not DesignItem bindingItem || bindingItem.Component is not System.Windows.Data.Binding)
					return NotFound(state, "Property is not an editable Binding: " + propertyName);
				try
				{
					if (delay == 0)
						bindingItem.Properties["Delay"].Reset();
					else
						bindingItem.Properties["Delay"].SetValue(delay);
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state); state.Accepted = true; return state;
			});

		/// <summary>Removes an explicit local value through the design model instead of assigning a
		/// guessed CLR default. This preserves inherited/default-property semantics and produces the
		/// same native undo transaction as an in-process WPF designer reset.</summary>
		[JsonRpcMethod("design/reset-property")]
		public DesignerSessionState ResetProperty(long baseVersion, string elementId, string propertyName)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				DesignItemProperty? property;
				try
				{
					property = FindFrameworkAttachedProperty(propertyName) is { } attached
						? item.Properties.GetAttachedProperty(attached)
						: item.Properties[propertyName];
				}
				catch (ArgumentException e) { return NotFound(state, e.Message); }
				if (property == null)
					return NotFound(state, "Property not found: " + propertyName);
				if (!property.IsSet)
					return NotFound(state, "Property is not explicitly set: " + propertyName);
				try
				{
					property.Reset();
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Sets or clears a XAML event attribute through the design model. The host does
		/// not generate a code-behind method: callers supply an existing handler name, so no user
		/// code is run or authored inside this isolated process.</summary>
		[JsonRpcMethod("design/set-event")]
		public DesignerSessionState SetEvent(long baseVersion, string elementId, string eventName, string handlerName)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				var property = item.Properties[eventName];
				if (property == null || !property.IsEvent)
					return NotFound(state, "Event not found: " + eventName);
				try
				{
					if (string.IsNullOrWhiteSpace(handlerName))
						property.Reset();
					else
						property.SetValue(handlerName);
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
		public DesignerSessionState AddElement(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, DesignerDropTarget dropTarget)
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

		/// <summary>Serializes elements for the IDE's clipboard, as the in-process designer's Copy
		/// did (<c>XamlEditOperations.Copy</c>): one snippet per element, skipping an element whose
		/// ancestor is copied too. Read-only; the root cannot be copied.</summary>
		[JsonRpcMethod("design/copy-elements")]
		public DesignerCopyResult CopyElements(long baseVersion, string[] elementIds)
			=> dispatcher.Dispatch(() => {
				var result = new DesignerCopyResult();
				if (current == null || version != baseVersion)
				{
					result.Error = $"Stale base version {baseVersion}; the open document is at version {version}.";
					return result;
				}
				var items = new List<DesignItem>();
				foreach (var id in elementIds ?? Array.Empty<string>())
				{
					if (id.Length == 0 || !pathToItem.TryGetValue(id, out var item) || item is not XamlDesignItem)
					{
						result.Error = "Element not found or not copyable: " + id;
						return result;
					}
					items.Add(item);
				}
				foreach (var item in items)
				{
					var ancestorCopied = false;
					for (var parent = item.Parent; parent != null && !ancestorCopied; parent = parent.Parent)
						ancestorCopied = items.Contains(parent);
					if (!ancestorCopied)
						result.Snippets.Add(ICSharpCode.WpfDesign.XamlDom.XamlStaticTools.GetXaml(((XamlDesignItem)item).XamlObject));
				}
				result.Accepted = result.Snippets.Count > 0;
				return result;
			});

		/// <summary>Pastes clipboard snippets as the in-process designer's Paste did
		/// (<c>XamlEditOperations.Paste</c>): into <paramref name="targetId"/> or the nearest
		/// ancestor whose content collection accepts them, or into an empty single content. A pasted
		/// name already used in the document is made unique, since a duplicate <c>x:Name</c> would
		/// break the document. One undo group; the new elements' ids are in CreatedElementIds.</summary>
		[JsonRpcMethod("design/paste-elements")]
		public DesignerSessionState PasteElements(long baseVersion, string targetId, string[] snippets)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(targetId ?? "", out var target) || current?.RootItem is not XamlDesignItem rootItem)
					return NotFound(state, "Paste target not found: " + targetId);
				var usedNames = new HashSet<string>(pathToItem.Values.Select(item => item.Name).Where(name => !string.IsNullOrEmpty(name))!, StringComparer.Ordinal);
				var pasted = new List<DesignItem>();
				try
				{
					foreach (var snippet in (snippets ?? Array.Empty<string>()).Where(text => !string.IsNullOrWhiteSpace(text)))
					{
						var obj = ICSharpCode.WpfDesign.XamlDom.XamlParser.ParseSnippet(rootItem.XamlObject, UniqueNames(snippet, usedNames), current.ParserSettings);
						if (obj != null && ((XamlComponentService)target.Services.Component).RegisterXamlComponentRecursive(obj) is { } registered)
							pasted.Add(registered);
					}
				}
				catch (Exception e) { return NotFound(state, "The clipboard is not pastable XAML: " + e.GetBaseException().Message); }
				if (pasted.Count == 0)
					return NotFound(state, "Nothing to paste.");
				// The nearest container, starting at the target, that can take every pasted element.
				var container = target;
				while (container != null && !CanTake(container, pasted))
					container = container.Parent;
				if (container == null)
					return NotFound(state, "No container at or above the selection can hold the pasted elements.");
				try
				{
					using var changeGroup = container.Context.OpenGroup("Paste " + pasted.Count + " elements", pasted);
					if (container.ContentProperty.IsCollection)
						foreach (var item in pasted)
							container.ContentProperty.CollectionElements.Add(item);
					else
						container.ContentProperty.SetValue(pasted[0]);
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.CreatedElementIds = pasted.Select(item => pathToItem.FirstOrDefault(entry => entry.Value == item).Key).Where(id => id != null).ToList();
				state.Accepted = true;
				return state;
			});

		/// <summary>Pastes snippets into one exact Canvas at the context-menu pointer. Unlike ordinary
		/// Paste this never walks to an ancestor: the visible Canvas that supplied the coordinate must
		/// accept every item. A multi-item clipboard keeps its relative Canvas offsets while its
		/// bounding box's upper-left corner is anchored at <paramref name="x"/>, <paramref name="y"/>.
		/// The insertion and every attached-position edit form one native undo group.</summary>
		[JsonRpcMethod("design/paste-elements-at")]
		public DesignerSessionState PasteElementsAt(long baseVersion, string canvasId, string[] snippets, double x, double y)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!double.IsFinite(x) || !double.IsFinite(y))
					return NotFound(state, "Paste coordinates must be finite.");
				if (!pathToItem.TryGetValue(canvasId ?? "", out var canvas) || canvas.Component is not Canvas || canvas.ContentProperty?.IsCollection != true)
					return NotFound(state, "Paste Here target is not a Canvas: " + canvasId);
				if (ParsePastedSnippets(canvas, snippets, out var error) is not { } pasted)
					return NotFound(state, error!);
				if (!CanTake(canvas, pasted))
					return NotFound(state, "The Canvas cannot hold the pasted elements.");
				var sourceLeft = pasted.Min(item => CanvasCoordinate(item, isLeft: true));
				var sourceTop = pasted.Min(item => CanvasCoordinate(item, isLeft: false));
				try
				{
					using var changeGroup = canvas.Context.OpenGroup("Paste at pointer", pasted);
					foreach (var item in pasted)
						canvas.ContentProperty.CollectionElements.Add(item);
					foreach (var item in pasted)
					{
						item.Properties.GetAttachedProperty(Canvas.LeftProperty).SetValue(x + CanvasCoordinate(item, isLeft: true) - sourceLeft);
						item.Properties.GetAttachedProperty(Canvas.TopProperty).SetValue(y + CanvasCoordinate(item, isLeft: false) - sourceTop);
					}
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.CreatedElementIds = pasted.Select(item => pathToItem.FirstOrDefault(entry => entry.Value == item).Key).Where(id => id != null).ToList();
				state.Accepted = state.CreatedElementIds.Count == pasted.Count;
				return state;
			});

		/// <summary>Pastes snippets into one exact Grid at the context-menu pointer: every pasted
		/// element joins the Grid's children and is assigned the cell whose measured row and column
		/// contain <paramref name="x"/>, <paramref name="y"/> (Grid-local). Row/column 0 resets the
		/// attached property rather than writing a default; spans, margins and alignment stay as
		/// copied. A point outside the Grid is rejected, never clamped. One native undo group.</summary>
		[JsonRpcMethod("design/paste-elements-into-cell")]
		public DesignerSessionState PasteElementsIntoCell(long baseVersion, string gridId, string[] snippets, double x, double y)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(gridId ?? "", out var gridItem) || gridItem.Component is not Grid grid || gridItem.ContentProperty?.IsCollection != true)
					return NotFound(state, "Paste Here target is not a Grid: " + gridId);
				if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > grid.ActualWidth || y > grid.ActualHeight)
					return NotFound(state, "The paste point is outside the Grid.");
				var row = TrackAt(grid.RowDefinitions.Select(definition => definition.ActualHeight).ToList(), y);
				var column = TrackAt(grid.ColumnDefinitions.Select(definition => definition.ActualWidth).ToList(), x);
				if (ParsePastedSnippets(gridItem, snippets, out var error) is not { } pasted)
					return NotFound(state, error!);
				if (!CanTake(gridItem, pasted))
					return NotFound(state, "The Grid cannot hold the pasted elements.");
				try
				{
					using var changeGroup = gridItem.Context.OpenGroup("Paste into cell", pasted);
					foreach (var item in pasted)
						gridItem.ContentProperty.CollectionElements.Add(item);
					foreach (var item in pasted)
					{
						SetOrReset(item.Properties.GetAttachedProperty(Grid.RowProperty), row);
						SetOrReset(item.Properties.GetAttachedProperty(Grid.ColumnProperty), column);
					}
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.CreatedElementIds = pasted.Select(item => pathToItem.FirstOrDefault(entry => entry.Value == item).Key).Where(id => id != null).ToList();
				state.Accepted = state.CreatedElementIds.Count == pasted.Count;
				return state;
			});

		static void SetOrReset(DesignItemProperty property, int index)
		{
			if (index == 0)
				property.Reset();
			else
				property.SetValue(index);
		}

		/// <summary>The measured track containing a Grid-local offset; a Grid without definitions
		/// has the single implicit track 0, and the far edge belongs to the last track.</summary>
		static int TrackAt(IReadOnlyList<double> sizes, double offset)
		{
			var start = 0d;
			for (var index = 0; index < sizes.Count; index++)
			{
				start += sizes[index];
				if (offset < start)
					return index;
			}
			return Math.Max(0, sizes.Count - 1);
		}

		/// <summary>Parses clipboard snippets into registered design items for
		/// <paramref name="container"/>, uniquifying every <c>x:Name</c> against the document; null
		/// with <paramref name="error"/> when nothing pastable was given.</summary>
		List<DesignItem>? ParsePastedSnippets(DesignItem container, string[]? snippets, out string? error)
		{
			error = null;
			if (current?.RootItem is not XamlDesignItem rootItem)
			{
				error = "The document is not loaded.";
				return null;
			}
			var usedNames = new HashSet<string>(pathToItem.Values.Select(item => item.Name).Where(name => !string.IsNullOrEmpty(name))!, StringComparer.Ordinal);
			var pasted = new List<DesignItem>();
			try
			{
				foreach (var snippet in (snippets ?? Array.Empty<string>()).Where(text => !string.IsNullOrWhiteSpace(text)))
				{
					var obj = ICSharpCode.WpfDesign.XamlDom.XamlParser.ParseSnippet(rootItem.XamlObject, UniqueNames(snippet, usedNames), current.ParserSettings);
					if (obj != null && ((XamlComponentService)container.Services.Component).RegisterXamlComponentRecursive(obj) is { } registered)
						pasted.Add(registered);
				}
			}
			catch (Exception e)
			{
				error = "The clipboard is not pastable XAML: " + e.GetBaseException().Message;
				return null;
			}
			if (pasted.Count == 0)
			{
				error = "Nothing to paste.";
				return null;
			}
			return pasted;
		}

		static double CanvasCoordinate(DesignItem item, bool isLeft)
		{
			var coordinate = item.Component is UIElement element
				? isLeft ? Canvas.GetLeft(element) : Canvas.GetTop(element)
				: double.NaN;
			return double.IsFinite(coordinate) ? coordinate : 0;
		}

		/// <summary>Whether <paramref name="container"/>'s content can take all <paramref name="items"/>:
		/// a collection whose element type accepts them (only UI elements into a Panel), or a single
		/// content that is empty and of a fitting type.</summary>
		static bool CanTake(DesignItem container, IReadOnlyList<DesignItem> items)
		{
			var content = container.ContentProperty;
			if (content == null)
				return false;
			if (content.IsCollection)
				return content.ReturnType != null
					&& ICSharpCode.WpfDesign.XamlDom.CollectionSupport.CanCollectionAdd(content.ReturnType, items.Select(item => item.Component))
					&& (container.Component is not Panel || items.All(item => item.Component is UIElement));
			return items.Count == 1 && !content.IsSet && content.ReturnType != null && content.ReturnType.IsInstanceOfType(items[0].Component);
		}

		/// <summary>Renames every <c>x:Name</c>/<c>Name</c> in a snippet that the document (or an
		/// earlier pasted snippet) already uses: <c>button1</c> becomes <c>button2</c>, <c>ok</c>
		/// becomes <c>ok1</c>.</summary>
		static string UniqueNames(string snippet, HashSet<string> usedNames)
			// Only a real x:Name or Name attribute: not preceded by '.', ':' or a name character, so
			// an attached AutomationProperties.Name or a d:Name keeps its value. Either quote.
			=> System.Text.RegularExpressions.Regex.Replace(snippet, "(?<![\\w.:])(?<attr>(?:x:)?Name\\s*=\\s*)(?<quote>[\"'])(?<name>[^\"']+)\\k<quote>", match => {
				var name = match.Groups["name"].Value;
				if (usedNames.Add(name))
					return match.Value;
				var stem = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
				var suffix = 1;
				while (!usedNames.Add(stem + suffix))
					suffix++;
				var quote = match.Groups["quote"].Value;
				return match.Groups["attr"].Value + quote + stem + suffix + quote;
			});

		/// <summary>Moves elements into a container, as dragging them in the in-process Outline did
		/// (<c>OutlineNodeBase.Insert</c>): into the container's content property - a collection
		/// right after <paramref name="afterId"/> (or first when it is null), a single content only
		/// when it is empty, where the in-process designer silently replaced what was there. The
		/// root, an element moved into itself or its own descendant, a single content whose type
		/// cannot hold it, or a non-UIElement into a Panel is rejected; the move is one undo group
		/// and leaves the first moved element in <see cref="DesignerSessionState.MovedElementId"/>.</summary>
		[JsonRpcMethod("design/move-into")]
		public DesignerSessionState MoveInto(long baseVersion, string[] elementIds, string targetId, string? afterId)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(targetId, out var target))
					return NotFound(state, "Target not found: " + targetId);
				var content = target.ContentProperty;
				if (content == null)
					return NotFound(state, "The target has no content property: " + targetId);
				var items = new List<DesignItem>();
				foreach (var id in elementIds ?? Array.Empty<string>())
				{
					if (id.Length == 0 || !pathToItem.TryGetValue(id, out var moved))
						return NotFound(state, "Element not found or not movable: " + id);
					for (var ancestor = target; ancestor != null; ancestor = ancestor.Parent)
						if (ancestor == moved)
							return NotFound(state, "An element cannot be moved into itself: " + id);
					items.Add(moved);
				}
				if (items.Count == 0)
					return NotFound(state, "Nothing to move.");
				DesignItem? after = null;
				// The anchor must be in the content collection itself: the Outline also shows a target's
				// ContextMenu and Header as children, and IndexOf would put the element first.
				if (afterId != null && (!pathToItem.TryGetValue(afterId, out after) || !content.IsCollection
					|| !content.CollectionElements.Contains(after) || items.Contains(after)))
					return NotFound(state, "The insertion point is not a remaining child of the target: " + afterId);
				// IsSet, not Value: text content (Content="First") has no DesignItem Value but is still
				// content that a move must not overwrite.
				if (!content.IsCollection && (items.Count > 1 || content.IsSet && (content.Value == null || !items.Contains(content.Value))))
					return NotFound(state, "The target holds a single child and is not empty.");
				if (!content.IsCollection && content.ReturnType != null && !items.All(moved => content.ReturnType.IsInstanceOfType(moved.Component)))
					return NotFound(state, "The target's content cannot hold that element.");
				if (content.IsCollection && target.Component is Panel && !items.All(moved => moved.Component is UIElement))
					return NotFound(state, "A panel holds only UI elements.");
				try
				{
					using var changeGroup = target.Context.OpenGroup("Move in outline", items);
					foreach (var moved in items)
						moved.Remove();
					if (content.IsCollection)
					{
						var index = after == null ? 0 : content.CollectionElements.IndexOf(after) + 1;
						foreach (var moved in items)
							content.CollectionElements.Insert(index++, moved);
					}
					else
						content.SetValue(items[0]);
					// Grid/Canvas/Dock attached values describe a particular parent. Leaving them on a
					// moved child makes later moves unexpectedly reuse an old layout position; preserve
					// only the properties whose owner is the new parent's actual layout contract.
					foreach (var moved in items)
						ClearStaleLayoutAttachedProperties(moved, target.Component);
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.MovedElementId = pathToItem.FirstOrDefault(entry => entry.Value == items[0]).Key;
				state.Accepted = true;
				return state;
			});

		static void ClearStaleLayoutAttachedProperties(DesignItem item, object? newParent)
		{
			var allowed = new HashSet<DependencyProperty>(ParentLayoutAttachedProperties(newParent));
			foreach (var property in LayoutAttachedProperties)
			{
				if (allowed.Contains(property))
					continue;
				var attached = item.Properties.GetAttachedProperty(property);
				if (attached.IsSet)
					attached.Reset();
			}
		}

		/// <summary>Clones one Outline element into an exact drop target. Unlike clipboard Paste this
		/// never walks to an ancestor: Ctrl-drag must either land where its insertion rail indicated or
		/// fail. The clone gets unique names, is inserted after <paramref name="afterId"/> (or first),
		/// and is one native undo group.</summary>
		[JsonRpcMethod("design/clone-into")]
		public DesignerSessionState CloneInto(long baseVersion, string elementId, string targetId, string? afterId)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId ?? "", out var source) || source is not XamlDesignItem sourceItem || source.Parent == null)
					return NotFound(state, "Element not found or not copyable: " + elementId);
				if (!pathToItem.TryGetValue(targetId ?? "", out var target))
					return NotFound(state, "Target not found: " + targetId);
				// Keep the in-process Outline's structural guard: copying a container into itself or a
				// descendant would recursively expand the document even though it is not a reference cycle.
				for (var ancestor = target; ancestor != null; ancestor = ancestor.Parent)
					if (ancestor == source)
						return NotFound(state, "An element cannot be copied into itself: " + elementId);
				var content = target.ContentProperty;
				if (content == null)
					return NotFound(state, "The target has no content property: " + targetId);
				DesignItem? after = null;
				if (afterId != null && (!pathToItem.TryGetValue(afterId, out after) || !content.IsCollection || !content.CollectionElements.Contains(after)))
					return NotFound(state, "The insertion point is not a child of the target: " + afterId);
				if (!content.IsCollection && content.IsSet)
					return NotFound(state, "The target holds a single child and is not empty.");

				DesignItem? clone;
				try
				{
					var usedNames = new HashSet<string>(pathToItem.Values.Select(item => item.Name).Where(name => !string.IsNullOrEmpty(name))!, StringComparer.Ordinal);
					var snippet = ICSharpCode.WpfDesign.XamlDom.XamlStaticTools.GetXaml(sourceItem.XamlObject);
					var parsed = current?.RootItem is XamlDesignItem rootItem
						? ICSharpCode.WpfDesign.XamlDom.XamlParser.ParseSnippet(rootItem.XamlObject, UniqueNames(snippet, usedNames), current.ParserSettings)
						: null;
					clone = parsed == null ? null : ((XamlComponentService)target.Services.Component).RegisterXamlComponentRecursive(parsed);
				}
				catch (Exception e) { return NotFound(state, "The element could not be cloned: " + e.GetBaseException().Message); }
				if (clone == null || !CanTake(target, new[] { clone }))
					return NotFound(state, "The target's content cannot hold that element.");
				try
				{
					using var changeGroup = target.Context.OpenGroup("Copy in outline", new[] { clone });
					if (content.IsCollection)
						content.CollectionElements.Insert(after == null ? 0 : content.CollectionElements.IndexOf(after) + 1, clone);
					else
						content.SetValue(clone);
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.CreatedElementIds = pathToItem.FirstOrDefault(entry => entry.Value == clone).Key is { } createdId
					? new List<string> { createdId } : new List<string>();
				state.Accepted = state.CreatedElementIds.Count == 1;
				return state;
			});

		/// <summary>Changes an element's stacking order among its siblings, as the in-process
		/// designer's RightClickContextMenu did: "front"/"back" move it to the end/start of the
		/// parent's collection (drawn last/first), "forward"/"backward" by one. The host owns the
		/// collection, so the order is not inferred from the snapshot's child list.</summary>
		[JsonRpcMethod("design/set-z-order")]
		public DesignerSessionState SetZOrder(long baseVersion, string elementId, string placement)
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
				var newIndex = placement switch {
					"front" => siblings.Count - 1,
					"back" => 0,
					"forward" => Math.Min(index + 1, siblings.Count - 1),
					"backward" => Math.Max(index - 1, 0),
					_ => -1
				};
				if (index < 0 || newIndex < 0)
					return NotFound(state, "Unsupported z-order placement: " + placement);
				if (newIndex == index)
					return NotFound(state, placement is "front" or "forward" ? "The element is already in front." : "The element is already at the back.");
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
				state.MovedElementId = pathToItem.FirstOrDefault(entry => entry.Value == item).Key;
				state.Accepted = true;
				return state;
			});

		static readonly Dictionary<string, Type> wrapContainerTypes = new(StringComparer.Ordinal) {
			["Canvas"] = typeof(Canvas), ["Grid"] = typeof(Grid), ["Viewbox"] = typeof(Viewbox)
		};

		/// <summary>Wraps sibling elements in a new Canvas, Grid or Viewbox, keeping their on-screen
		/// placement - the in-process designer's Wrap context commands, through the same
		/// <see cref="ICSharpCode.WpfDesign.Designer.ModelTools.WrapItemsNewContainer"/>. A Viewbox
		/// takes one child. The new container is returned as <see cref="DesignerSessionState.CreatedElementId"/>.</summary>
		[JsonRpcMethod("design/wrap-elements")]
		public DesignerSessionState WrapElements(long baseVersion, string[] elementIds, string containerType)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!wrapContainerTypes.TryGetValue(containerType, out var type))
					return NotFound(state, "Unsupported wrap container: " + containerType);
				if (elementIds == null || elementIds.Length == 0)
					return NotFound(state, "No elements to wrap.");
				if (type == typeof(Viewbox) && elementIds.Length != 1)
					return NotFound(state, "A Viewbox wraps exactly one element.");
				var items = new List<DesignItem>();
				foreach (var id in elementIds.Distinct())
				{
					if (!pathToItem.TryGetValue(id, out var item))
						return NotFound(state, "Element not found: " + id);
					if (item.Parent == null)
						return NotFound(state, "The root element cannot be wrapped.");
					items.Add(item);
				}
				if (items.Any(item => item.Parent != items[0].Parent))
					return NotFound(state, "Only elements with the same parent can be wrapped together.");
				DesignItem? container;
				try
				{
					var parent = items[0].Parent!;
					// The same collection is used to find the first index and to re-insert below.
					var parentContent = parent.ContentProperty is { IsCollection: true } contentProperty ? contentProperty : null;
					var firstIndex = parentContent != null && items.All(item => item.ParentProperty == parentContent)
						? items.Min(item => parentContent.CollectionElements.IndexOf(item)) : -1;
					using var changeGroup = items[0].OpenGroup("Wrap in " + containerType);
					// doInsert:false, then insert as ModelTools does but without its final
					// Selection.SetSelectedComponents: selecting raises the in-process selection
					// adorners, which need a DesignPanel this headless host does not have.
					var wrapped = ICSharpCode.WpfDesign.Designer.ModelTools.WrapItemsNewContainer(items, type, doInsert: false);
					if (wrapped == null)
						return NotFound(state, "The parent cannot place a new " + containerType + ".");
					container = wrapped.Item1;
					var insert = PlacementOperation.TryStartInsertNewComponents(parent, new[] { container }, new[] { wrapped.Item2 }, PlacementType.AddItem);
					if (insert == null)
						return NotFound(state, "The parent cannot place a new " + containerType + ".");
					// A single wrapped element keeps its place in the parent's child order.
					if (items.Count == 1 && firstIndex >= 0 && parentContent!.CollectionElements.Remove(container))
						parentContent.CollectionElements.Insert(Math.Min(firstIndex, parentContent.CollectionElements.Count), container);
					insert.Commit();
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.CreatedElementId = pathToItem.FirstOrDefault(entry => entry.Value == container).Key;
				state.Accepted = true;
				return state;
			});

		/// <summary>Removes a Grid or Canvas, moving its children into its parent at the same
		/// on-screen positions - the in-process Unwrap context command, through
		/// <see cref="ICSharpCode.WpfDesign.Designer.ModelTools.UnwrapItemsFromContainer"/>.</summary>
		[JsonRpcMethod("design/unwrap-element")]
		public DesignerSessionState UnwrapElement(long baseVersion, string elementId)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var container))
					return NotFound(state, "Element not found: " + elementId);
				if (container.Component is not (Grid or Canvas))
					return NotFound(state, "Only a Grid or Canvas can be unwrapped.");
				if (container.Parent?.ContentProperty is not { IsCollection: true })
					return NotFound(state, "The container's parent cannot hold its children.");
				try
				{
					using var changeGroup = container.OpenGroup("Unwrap");
					ICSharpCode.WpfDesign.Designer.ModelTools.UnwrapItemsFromContainer(container);
					// ModelTools returns silently when the container cannot place; disposing the
					// uncommitted group then rolls back without leaving an empty undo entry.
					if (container.Parent != null)
						return NotFound(state, "The container could not be unwrapped.");
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
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

		/// <summary>Applies all bounds under one <see cref="PlacementOperation"/>. A group drag is
		/// one user edit: validating every id before starting means an invalid target leaves the
		/// document untouched, and committing once preserves the designer's native undo boundary.</summary>
		[JsonRpcMethod("design/set-bounds-batch")]
		public DesignerSessionState SetBoundsBatch(long baseVersion, DesignerBoundsEdit[] edits)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (edits == null || edits.Length == 0)
					return NotFound(state, "At least one bounds edit is required.");
				if (edits.Any(edit => string.IsNullOrEmpty(edit.ElementId)))
					return NotFound(state, "A bounds edit has no element id.");
				var distinct = edits.GroupBy(edit => edit.ElementId, StringComparer.Ordinal).ToArray();
				if (distinct.Any(group => group.Count() != 1))
					return NotFound(state, "A bounds batch cannot contain the same element twice.");
				var items = new DesignItem[edits.Length];
				for (var i = 0; i < edits.Length; i++)
				{
					if (!pathToItem.TryGetValue(edits[i].ElementId, out var item))
						return NotFound(state, "Element not found: " + edits[i].ElementId);
					items[i] = item;
				}
				try
				{
					// PlacementOperation filters descendants and groups by container, with no
					// public item identity on PlacementInformation. Apply one explicit target at
					// a time rather than pairing two unrelated indices, but keep all of those
					// native placements in one design-model group: a later failure rolls back
					// earlier targets and the user gets exactly one undo entry.
					using var changeGroup = items[0].OpenGroup("Set multiple bounds");
					for (var i = 0; i < items.Length; i++)
					{
						var edit = edits[i];
						try
						{
							var operation = PlacementOperation.Start(new[] { items[i] }, PlacementType.Resize);
							try
							{
								var info = operation.PlacedItems.Single();
								info.Bounds = new Rect(edit.X, edit.Y, edit.Width, edit.Height);
								operation.CurrentContainerBehavior.SetPosition(info);
								operation.Commit();
							}
							catch { operation.Abort(); throw; }
						}
						catch (Exception placementFailure)
						{
							// Match the single-element RPC: a container that cannot place still
							// permits a size-only edit rather than rejecting the whole gesture. Keep
							// the original placement failure visible for diagnostics.
							items[i].Properties["Width"].SetValue(edit.Width);
							items[i].Properties["Height"].SetValue(edit.Height);
							state.Diagnostics.Add(new DesignerDiagnostic {
								Severity = "Warning",
								Message = "Placement failed; applied size-only bounds fallback: " + placementFailure.GetBaseException().Message
							});
						}
					}
					changeGroup.Commit();
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Writes an edge that the common canvas displayed from actual WPF layout. This is
		/// intentionally distinct from <see cref="SetBounds"/>: a Canvas can be authored with
		/// Right/Bottom rather than Left/Top, and replacing the effective anchor during an edge drag
		/// would make a later parent resize behave differently. The public measurements describe the
		/// visible border, while Canvas positions the margin box, so conversions deliberately account
		/// for the relevant margin edge. Grid edges are simply Thickness members.
		/// Every edit is one design-model group, preserving the native undo boundary.</summary>
		[JsonRpcMethod("design/set-layout-inset")]
		public DesignerSessionState SetLayoutInset(long baseVersion, string elementId, string kind, string edge, double value)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				if (!double.IsFinite(value))
					return NotFound(state, "Layout inset value must be finite.");
				if (item.View is FrameworkElement transformed
					&& (transformed.LayoutTransform.Value != Matrix.Identity || transformed.RenderTransform.Value != Matrix.Identity))
					return NotFound(state, "Layout insets are unavailable for transformed elements.");
				if (edge is not ("Left" or "Top" or "Right" or "Bottom"))
					return NotFound(state, "Unknown layout inset edge: " + edge);
				try
				{
					using var changeGroup = item.OpenGroup("Set layout inset");
					if (kind == "Margin" && item.Parent?.Component is Grid && item.View is FrameworkElement element)
					{
						var margin = element.Margin;
						margin = edge switch {
							"Left" => new Thickness(value, margin.Top, margin.Right, margin.Bottom),
							"Top" => new Thickness(margin.Left, value, margin.Right, margin.Bottom),
							"Right" => new Thickness(margin.Left, margin.Top, value, margin.Bottom),
							_ => new Thickness(margin.Left, margin.Top, margin.Right, value)
						};
						item.Properties["Margin"].SetValue(margin);
					}
					else if (kind == "CanvasPosition" && item.Parent?.Component is Canvas canvas && item.View is FrameworkElement canvasElement)
					{
						var left = item.Properties.GetAttachedProperty(Canvas.LeftProperty);
						var right = item.Properties.GetAttachedProperty(Canvas.RightProperty);
						var top = item.Properties.GetAttachedProperty(Canvas.TopProperty);
						var bottom = item.Properties.GetAttachedProperty(Canvas.BottomProperty);
						var margin = canvasElement.Margin;
						// Canvas itself gives Left/Top precedence when both opposing properties are
						// authored. Match that effective anchor rather than accepting a source edit
						// which has no visual effect.
						var useLeft = left.IsSet || !right.IsSet;
						var useTop = top.IsSet || !bottom.IsSet;
						switch (edge)
						{
							case "Left":
								if (useLeft) left.SetValue(value - margin.Left);
								else right.SetValue(canvas.ActualWidth - value - canvasElement.ActualWidth - margin.Right);
								break;
							case "Right":
								if (useLeft) left.SetValue(canvas.ActualWidth - value - canvasElement.ActualWidth - margin.Left);
								else right.SetValue(value - margin.Right);
								break;
							case "Top":
								if (useTop) top.SetValue(value - margin.Top);
								else bottom.SetValue(canvas.ActualHeight - value - canvasElement.ActualHeight - margin.Bottom);
								break;
							default:
								if (useTop) top.SetValue(canvas.ActualHeight - value - canvasElement.ActualHeight - margin.Top);
								else bottom.SetValue(value - margin.Bottom);
								break;
						}
					}
					else
						return NotFound(state, "The element's current layout does not support " + kind + " insets.");
					changeGroup.Commit();
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Switches one Grid edge between its alignment anchor and Stretch. The current
		/// rendered rectangle is measured before changing the source, so the click changes only the
		/// layout representation, not what the designer shows. This deliberately applies only to a
		/// Grid child: Canvas has independent attached-property anchor semantics.</summary>
		[JsonRpcMethod("design/toggle-layout-inset-anchor")]
		public DesignerSessionState ToggleLayoutInsetAnchor(long baseVersion, string elementId, string edge)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item) || item.Parent?.Component is not Grid grid || item.View is not FrameworkElement element)
					return NotFound(state, "The element is not a Grid child.");
				if (edge is not ("Left" or "Top" or "Right" or "Bottom"))
					return NotFound(state, "Unknown layout inset edge: " + edge);
				if (element.LayoutTransform.Value != Matrix.Identity || element.RenderTransform.Value != Matrix.Identity)
					return NotFound(state, "Layout insets are unavailable for transformed elements.");
				if (edge is "Left" or "Right"
					&& (element.MinWidth > 0 || !double.IsPositiveInfinity(element.MaxWidth)))
					return NotFound(state, "Layout anchor toggles are unavailable for width-constrained elements.");
				if (edge is "Top" or "Bottom"
					&& (element.MinHeight > 0 || !double.IsPositiveInfinity(element.MaxHeight)))
					return NotFound(state, "Layout anchor toggles are unavailable for height-constrained elements.");
				try
				{
					var origin = element.TranslatePoint(new Point(), grid);
					var bounds = new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
					var column = Grid.GetColumn(element);
					var row = Grid.GetRow(element);
					var cellLeft = GridOffset(grid.ColumnDefinitions, column, grid.ActualWidth);
					var cellRight = GridOffset(grid.ColumnDefinitions, column + Math.Max(1, Grid.GetColumnSpan(element)), grid.ActualWidth);
					var cellTop = GridOffset(grid.RowDefinitions, row, grid.ActualHeight);
					var cellBottom = GridOffset(grid.RowDefinitions, row + Math.Max(1, Grid.GetRowSpan(element)), grid.ActualHeight);
					var margin = element.Margin;
					using var changeGroup = item.OpenGroup("Toggle layout inset anchor");
					switch (edge)
					{
						case "Left":
							ToggleHorizontal(item, element, HorizontalAlignment.Left, bounds, cellLeft, cellRight, margin);
							break;
						case "Right":
							ToggleHorizontal(item, element, HorizontalAlignment.Right, bounds, cellLeft, cellRight, margin);
							break;
						case "Top":
							ToggleVertical(item, element, VerticalAlignment.Top, bounds, cellTop, cellBottom, margin);
							break;
						default:
							ToggleVertical(item, element, VerticalAlignment.Bottom, bounds, cellTop, cellBottom, margin);
							break;
					}
					changeGroup.Commit();
				}
				catch (Exception e)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		static double GridOffset(IList<ColumnDefinition> definitions, int index, double extent)
			=> index <= 0 ? 0 : index >= definitions.Count ? extent : definitions[index].Offset;
		static double GridOffset(IList<RowDefinition> definitions, int index, double extent)
			=> index <= 0 ? 0 : index >= definitions.Count ? extent : definitions[index].Offset;

		static void ToggleHorizontal(DesignItem item, FrameworkElement element, HorizontalAlignment anchor, Rect bounds, double cellLeft, double cellRight, Thickness margin)
		{
			if (element.HorizontalAlignment == anchor)
			{
				item.Properties["Margin"].SetValue(new Thickness(bounds.Left - cellLeft, margin.Top, cellRight - bounds.Right, margin.Bottom));
				item.Properties["HorizontalAlignment"].SetValue(HorizontalAlignment.Stretch);
				item.Properties["Width"].Reset();
			}
			else
			{
				item.Properties["Margin"].SetValue(anchor == HorizontalAlignment.Left
					? new Thickness(bounds.Left - cellLeft, margin.Top, 0, margin.Bottom)
					: new Thickness(0, margin.Top, cellRight - bounds.Right, margin.Bottom));
				item.Properties["HorizontalAlignment"].SetValue(anchor);
				item.Properties["Width"].SetValue(bounds.Width);
			}
		}

		static void ToggleVertical(DesignItem item, FrameworkElement element, VerticalAlignment anchor, Rect bounds, double cellTop, double cellBottom, Thickness margin)
		{
			if (element.VerticalAlignment == anchor)
			{
				item.Properties["Margin"].SetValue(new Thickness(margin.Left, bounds.Top - cellTop, margin.Right, cellBottom - bounds.Bottom));
				item.Properties["VerticalAlignment"].SetValue(VerticalAlignment.Stretch);
				item.Properties["Height"].Reset();
			}
			else
			{
				item.Properties["Margin"].SetValue(anchor == VerticalAlignment.Top
					? new Thickness(margin.Left, bounds.Top - cellTop, margin.Right, 0)
					: new Thickness(margin.Left, 0, margin.Right, cellBottom - bounds.Bottom));
				item.Properties["VerticalAlignment"].SetValue(anchor);
				item.Properties["Height"].SetValue(bounds.Height);
			}
		}

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
				return BuildGridTracks(grid);
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

		/// <summary>Changes one Grid track's unit, as the in-process rail's GridUnitSelector does
		/// (Fixed/Star/Auto). The numeric part is kept, except that leaving Auto - whose value is
		/// a meaningless 1 - takes the track's measured size for Pixel and 1 for Star.</summary>
		[JsonRpcMethod("design/set-grid-track-unit")]
		public DesignerSessionState SetGridTrackUnit(long baseVersion, string elementId, bool isRow, int index, string unit)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item) || item.Component is not Grid grid)
					return NotFound(state, "Element is not a Grid: " + elementId);
				if (!Enum.TryParse<GridUnitType>(unit, ignoreCase: false, out var unitType) || !Enum.IsDefined(unitType))
					return NotFound(state, "Unknown grid unit: " + unit);
				var collection = item.Properties[isRow ? "RowDefinitions" : "ColumnDefinitions"];
				if (index < 0 || index >= collection.CollectionElements.Count)
					return NotFound(state, "Row/column index out of range: " + index);
				var definition = collection.CollectionElements[index];
				var property = definition.Properties[isRow ? RowDefinition.HeightProperty : ColumnDefinition.WidthProperty];
				var oldLength = property.GetConvertedValueOnInstance<GridLength>();
				if (oldLength.GridUnitType == unitType)
					return NotFound(state, "The track already uses " + unit + ".");
				var actual = isRow ? ((RowDefinition)definition.Component).ActualHeight : ((ColumnDefinition)definition.Component).ActualWidth;
				var newLength = unitType switch {
					GridUnitType.Auto => GridLength.Auto,
					_ when !oldLength.IsAuto => new GridLength(oldLength.Value, unitType),
					GridUnitType.Pixel => new GridLength(Math.Max(1, Math.Round(actual)), GridUnitType.Pixel),
					_ => new GridLength(1, GridUnitType.Star)
				};
				try
				{
					property.SetValue(newLength);
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Splits one measured Grid track at its local pixel <paramref name="position"/>.
		/// The definition insertion and every affected attached Row/Column/Span update share one
		/// design-model change group, so a rejected request cannot leave child indices half-shifted.
	/// Child placement follows the rendered child bounds: a child which occupies the first half of
	/// the split cell extends its span, while one wholly in the second half moves with that half.
	/// This preserves the pre-split visual placement rather than merely preserving logical indices.</summary>
		[JsonRpcMethod("design/split-grid-track")]
		public DesignerSessionState SplitGridTrack(long baseVersion, string elementId, bool isRow, double position)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item) || item.Component is not Grid grid)
					return NotFound(state, "Element is not a Grid: " + elementId);
				var definitions = isRow ? grid.RowDefinitions.Cast<DefinitionBase>().ToArray() : grid.ColumnDefinitions.Cast<DefinitionBase>().ToArray();
				if (definitions.Length == 0)
					return NotFound(state, "A Grid needs an explicit row/column before it can be split.");
				var splitIndex = Array.FindIndex(definitions, definition => {
					var offset = isRow ? ((RowDefinition)definition).Offset : ((ColumnDefinition)definition).Offset;
					var size = isRow ? ((RowDefinition)definition).ActualHeight : ((ColumnDefinition)definition).ActualWidth;
					return position > offset && position < offset + size;
				});
				if (splitIndex < 0)
					return NotFound(state, "The split position must be inside a Grid track.");
				try {
					using var changeGroup = item.OpenGroup(isRow ? "Split grid row" : "Split grid column");
					var collection = item.Properties[isRow ? "RowDefinitions" : "ColumnDefinitions"];
					var oldDefinition = definitions[splitIndex];
					var offset = isRow ? ((RowDefinition)oldDefinition).Offset : ((ColumnDefinition)oldDefinition).Offset;
					var actual = isRow ? ((RowDefinition)oldDefinition).ActualHeight : ((ColumnDefinition)oldDefinition).ActualWidth;
					var oldLength = isRow ? ((RowDefinition)oldDefinition).Height : ((ColumnDefinition)oldDefinition).Width;
					var ratio = (position - offset) / actual;
					if (oldLength.IsAuto) oldLength = new GridLength(actual, GridUnitType.Pixel);
					var first = new GridLength(oldLength.Value * ratio, oldLength.GridUnitType);
					var second = new GridLength(oldLength.Value - first.Value, oldLength.GridUnitType);
					var inserted = item.Services.Component.RegisterComponentForDesigner(isRow ? new RowDefinition() : new ColumnDefinition());
					collection.CollectionElements.Insert(splitIndex + 1, inserted);
					collection.CollectionElements[splitIndex].Properties[isRow ? RowDefinition.HeightProperty : ColumnDefinition.WidthProperty].SetValue(first);
					inserted.Properties[isRow ? RowDefinition.HeightProperty : ColumnDefinition.WidthProperty].SetValue(second);
					FixGridChildIndicesAfterSplit(item, splitIndex,
						isRow ? Grid.RowProperty : Grid.ColumnProperty,
						isRow ? Grid.RowSpanProperty : Grid.ColumnSpanProperty);
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Updates logical Grid indices after inserting a track: children after the split
		/// move with it, while a child spanning the split retains its leading cell and gains one span.</summary>
		static void FixGridChildIndicesAfterSplit(DesignItem gridItem, int splitIndex,
			DependencyProperty indexProperty, DependencyProperty spanProperty)
		{
			foreach (var child in gridItem.Properties["Children"].CollectionElements)
			{
				var index = child.Properties.GetAttachedProperty(indexProperty);
				var spanPropertyValue = child.Properties.GetAttachedProperty(spanProperty);
				var start = index.GetConvertedValueOnInstance<int>();
				var span = Math.Max(1, spanPropertyValue.GetConvertedValueOnInstance<int>());
				if (start > splitIndex) {
					index.SetValue(start + 1);
					continue;
				}
				if (splitIndex >= start + span)
					continue;
				// The split lies inside this child's declared cell span. Keep its leading cell
				// and extend the span over the new track.
				spanPropertyValue.SetValue(span + 1);
			}
		}

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
		static List<DesignerPropertyInfo> BuildProperties(DesignItem item, out List<DesignerBindingInfo> bindings)
		{
			bindings = new List<DesignerBindingInfo>();
			if (item.ComponentType == null)
				return new List<DesignerPropertyInfo>();
			var result = new List<DesignerPropertyInfo>();
			foreach (var descriptor in BrowsableProperties(item.ComponentType))
			{
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
				if (property.Value is DesignItem reference && reference.Component is System.Windows.Data.BindingBase)
				{
					bindings.Add(new DesignerBindingInfo { Property = descriptor.Name });
					if (reference.Component is System.Windows.Data.Binding binding)
					{
						result.Add(new DesignerPropertyInfo {
							Name = descriptor.Name + ".Mode", DisplayName = descriptor.Name + " Mode",
							Category = "Binding", TypeName = typeof(System.Windows.Data.BindingMode).FullName ?? "",
							Kind = "Enum", IsEnum = true, EditorKind = "BindingMode",
							Value = binding.Mode.ToString(), BindingPropertyName = descriptor.Name,
							ShouldSerialize = false,
							AllowedValues = Enum.GetNames(typeof(System.Windows.Data.BindingMode)).ToList()
						});
						result.Add(new DesignerPropertyInfo {
							Name = descriptor.Name + ".UpdateSourceTrigger", DisplayName = descriptor.Name + " Update Source Trigger",
							Category = "Binding", TypeName = typeof(System.Windows.Data.UpdateSourceTrigger).FullName ?? "",
							Kind = "Enum", IsEnum = true, EditorKind = "BindingUpdateSourceTrigger",
							Value = binding.UpdateSourceTrigger.ToString(), BindingPropertyName = descriptor.Name,
							ShouldSerialize = false,
							AllowedValues = Enum.GetNames(typeof(System.Windows.Data.UpdateSourceTrigger)).ToList()
						});
						if (HasUnsetBindingSource(reference) && binding.RelativeSource == null)
						{
							result.Add(new DesignerPropertyInfo {
								Name = descriptor.Name + ".ElementName", DisplayName = descriptor.Name + " Element Name",
								Category = "Binding", TypeName = typeof(string).FullName ?? "",
								Kind = "String", EditorKind = "BindingElementName",
								Value = binding.ElementName ?? "", BindingPropertyName = descriptor.Name,
								ShouldSerialize = false
							});
						}
						if (HasUnsetBindingSource(reference) && (binding.RelativeSource == null
							|| binding.RelativeSource.Mode is System.Windows.Data.RelativeSourceMode.Self or System.Windows.Data.RelativeSourceMode.TemplatedParent))
						{
							result.Add(new DesignerPropertyInfo {
								Name = descriptor.Name + ".RelativeSource", DisplayName = descriptor.Name + " Relative Source",
								Category = "Binding", TypeName = typeof(string).FullName ?? "",
								Kind = "Enum", EditorKind = "BindingRelativeSource",
								Value = binding.RelativeSource?.Mode.ToString() ?? "", BindingPropertyName = descriptor.Name,
								ShouldSerialize = false,
								AllowedValues = new List<string> { "", "Self", "TemplatedParent" }
							});
						}
						if (binding.RelativeSource?.Mode == System.Windows.Data.RelativeSourceMode.FindAncestor
							&& binding.RelativeSource.AncestorType != null)
						{
							result.Add(new DesignerPropertyInfo {
								Name = descriptor.Name + ".AncestorLevel", DisplayName = descriptor.Name + " FindAncestor Level",
								Category = "Binding", TypeName = typeof(int).FullName ?? "",
								Kind = "Number", EditorKind = "BindingAncestorLevel",
								Value = binding.RelativeSource.AncestorLevel.ToString(CultureInfo.InvariantCulture), BindingPropertyName = descriptor.Name,
								ShouldSerialize = false
							});
						}
						var sourceProperty = reference.Properties["Source"];
						if (sourceProperty != null && CanEditBindingSource(binding)
							&& TryGetStaticResourceBindingSource(sourceProperty, out var sourceKey))
						{
							var choices = new List<string> { "" };
							choices.AddRange(BindingSourceResourceKeys(item));
							if (!choices.Contains(sourceKey))
								choices.Add(sourceKey);
							result.Add(new DesignerPropertyInfo {
								Name = descriptor.Name + ".Source", DisplayName = descriptor.Name + " Source",
								Category = "Binding", TypeName = typeof(string).FullName ?? "",
								Kind = "Enum", EditorKind = "BindingSource",
								Value = sourceKey, BindingPropertyName = descriptor.Name,
								ShouldSerialize = false, AllowedValues = choices
							});
						}
						result.Add(new DesignerPropertyInfo {
							Name = descriptor.Name + ".NotifyOnValidationError", DisplayName = descriptor.Name + " Notify On Validation Error",
							Category = "Binding", TypeName = typeof(bool).FullName ?? "",
							Kind = "Boolean", EditorKind = "BindingNotifyOnValidationError",
							Value = binding.NotifyOnValidationError.ToString(CultureInfo.InvariantCulture), BindingPropertyName = descriptor.Name,
							ShouldSerialize = false
						});
						foreach (var optionName in bindingValidationOptionNames)
						{
							var option = reference.Properties[optionName];
							if (option?.ValueOnInstance is not bool enabled)
								continue;
							result.Add(new DesignerPropertyInfo {
								Name = descriptor.Name + "." + optionName, DisplayName = descriptor.Name + " " + optionName,
								Category = "Binding", TypeName = typeof(bool).FullName ?? "",
								Kind = "Boolean", EditorKind = "BindingValidationOption",
								Value = enabled.ToString(CultureInfo.InvariantCulture), BindingPropertyName = descriptor.Name,
								BindingOptionName = optionName, ShouldSerialize = false
							});
						}
						var converterProperty = reference.Properties["Converter"];
						if (converterProperty != null && TryGetStaticResourceConverterKey(converterProperty, out var converterKey))
						{
							var choices = new List<string> { "" };
							choices.AddRange(ConverterResourceKeys(item));
							if (!choices.Contains(converterKey))
								choices.Add(converterKey);
							result.Add(new DesignerPropertyInfo {
								Name = descriptor.Name + ".Converter", DisplayName = descriptor.Name + " Converter",
								Category = "Binding", TypeName = typeof(string).FullName ?? "",
								Kind = "Enum", EditorKind = "BindingConverter",
								Value = converterKey, BindingPropertyName = descriptor.Name,
								ShouldSerialize = false, AllowedValues = choices
							});
						}
						foreach (var optionName in bindingTextOptionNames)
						{
							var option = reference.Properties[optionName];
							if (!IsTextBindingOption(option))
								continue;
							result.Add(new DesignerPropertyInfo {
								Name = descriptor.Name + "." + optionName, DisplayName = descriptor.Name + " " + optionName,
								Category = "Binding", TypeName = typeof(string).FullName ?? "",
								Kind = "String", EditorKind = "BindingTextOption",
								Value = option.ValueOnInstance as string ?? "", BindingPropertyName = descriptor.Name,
								BindingOptionName = optionName, ShouldSerialize = false
							});
						}
						result.Add(new DesignerPropertyInfo {
							Name = descriptor.Name + ".Delay", DisplayName = descriptor.Name + " Delay",
							Category = "Binding", TypeName = typeof(int).FullName ?? "",
							Kind = "Number", EditorKind = "BindingDelay",
							Value = binding.Delay.ToString(CultureInfo.InvariantCulture), BindingPropertyName = descriptor.Name,
							ShouldSerialize = false
						});
					}
					else if (reference.Component is System.Windows.Data.MultiBinding or System.Windows.Data.PriorityBinding)
					{
						var children = reference.Properties["Bindings"]?.CollectionElements;
						if (children != null && children.All(child => child.Component is System.Windows.Data.Binding))
						{
							result.Add(new DesignerPropertyInfo {
								Name = descriptor.Name + ".Bindings", DisplayName = descriptor.Name + " Bindings",
								Category = "Binding", TypeName = typeof(System.Windows.Data.BindingBase).FullName ?? "",
								Kind = "Collection", EditorKind = "BindingCollection", IsReadOnly = true,
								Value = "(" + children.Count + (children.Count == 1 ? " binding)" : " bindings)"),
								CollectionItems = children.Select(child => ((System.Windows.Data.Binding)child.Component).Path?.Path ?? "").ToList(),
								BindingPropertyName = descriptor.Name, ShouldSerialize = children.Count > 0
							});
							for (var index = 0; index < children.Count; index++)
							{
								if (children[index].Component is not System.Windows.Data.Binding child)
									continue;
								result.Add(new DesignerPropertyInfo {
									Name = descriptor.Name + ".Bindings[" + index.ToString(CultureInfo.InvariantCulture) + "].Path",
									DisplayName = descriptor.Name + " Binding " + (index + 1).ToString(CultureInfo.InvariantCulture) + " Path",
									Category = "Binding", TypeName = typeof(string).FullName ?? "",
									Kind = "String", EditorKind = "BindingChildPath", Value = child.Path?.Path ?? "",
									BindingPropertyName = descriptor.Name, BindingChildIndex = index, ShouldSerialize = false
								});
								result.Add(new DesignerPropertyInfo {
									Name = descriptor.Name + ".Bindings[" + index.ToString(CultureInfo.InvariantCulture) + "].Mode",
									DisplayName = descriptor.Name + " Binding " + (index + 1).ToString(CultureInfo.InvariantCulture) + " Mode",
									Category = "Binding", TypeName = typeof(System.Windows.Data.BindingMode).FullName ?? "",
									Kind = "Enum", IsEnum = true, EditorKind = "BindingChildMode", Value = child.Mode.ToString(),
									BindingPropertyName = descriptor.Name, BindingChildIndex = index, ShouldSerialize = false,
									AllowedValues = Enum.GetNames(typeof(System.Windows.Data.BindingMode)).ToList()
								});
				result.Add(new DesignerPropertyInfo {
					Name = descriptor.Name + ".Bindings[" + index.ToString(CultureInfo.InvariantCulture) + "].UpdateSourceTrigger",
					DisplayName = descriptor.Name + " Binding " + (index + 1).ToString(CultureInfo.InvariantCulture) + " Update Source Trigger",
					Category = "Binding", TypeName = typeof(System.Windows.Data.UpdateSourceTrigger).FullName ?? "",
					Kind = "Enum", IsEnum = true, EditorKind = "BindingChildUpdateSourceTrigger", Value = child.UpdateSourceTrigger.ToString(),
					BindingPropertyName = descriptor.Name, BindingChildIndex = index, ShouldSerialize = false,
					AllowedValues = Enum.GetNames(typeof(System.Windows.Data.UpdateSourceTrigger)).ToList()
				});
				if (HasUnsetBindingSource(children[index]) && child.RelativeSource == null)
				{
					result.Add(new DesignerPropertyInfo {
						Name = descriptor.Name + ".Bindings[" + index.ToString(CultureInfo.InvariantCulture) + "].ElementName",
						DisplayName = descriptor.Name + " Binding " + (index + 1).ToString(CultureInfo.InvariantCulture) + " Element Name",
						Category = "Binding", TypeName = typeof(string).FullName ?? "",
						Kind = "String", EditorKind = "BindingChildElementName", Value = child.ElementName ?? "",
						BindingPropertyName = descriptor.Name, BindingChildIndex = index, ShouldSerialize = false
					});
				}
							}
						}
					}
				}
				result.Add(MarkResourceReference(MarkImageSource(ToPropertyInfo(property), property), property));
			}
			// The in-process grid lists the attached layout properties the parent panel defines,
			// named as XAML spells them so design/set-property and reset-property accept them.
			foreach (var attached in ParentLayoutAttachedProperties(item.Parent?.Component))
			{
				var info = ToPropertyInfo(item.Properties.GetAttachedProperty(attached));
				info.Name = info.DisplayName = attached.OwnerType.Name + "." + attached.Name;
				info.Category = "Layout";
				result.Add(info);
			}
			foreach (var info in result.Where(info => info.EditorKind == "ResourceReference"))
			{
				var type = item.Properties[info.Name]?.ReturnType;
				if (type != null)
					info.ResourceReferenceKeys = ResourceKeys(item, value => value != null && type.IsInstanceOfType(value)).OrderBy(key => key, StringComparer.Ordinal).ToList();
			}
			// Every brush editor row, attached ones included, offers the same in-scope Brush resources.
			List<string>? brushKeys = null, colorKeys = null;
			foreach (var info in result.Where(info => info.EditorKind == "Brush"))
			{
				info.BrushResourceKeys = brushKeys ??= BrushResourceKeys(item);
				info.ColorResourceKeys = colorKeys ??= ColorResourceKeys(item);
			}
			if (TryGetEditableItems(item, out var itemsProperty, out var itemKind) && itemsProperty != null && itemKind != null)
			{
				result.RemoveAll(info => info.Name == "Items");
				var texts = itemsProperty.CollectionElements.Select(element => element.Properties[itemKind.TextProperty].ValueOnInstance as string ?? "").ToList();
				result.Add(new DesignerPropertyInfo {
					Name = "Items", DisplayName = "Items", Category = "Common",
					TypeName = typeof(ItemCollection).FullName ?? "",
					Kind = "Collection", EditorKind = "ItemsCollection", IsReadOnly = true,
					Value = "(" + texts.Count + (texts.Count == 1 ? " item)" : " items)"),
					CollectionItems = texts, ShouldSerialize = texts.Count > 0
				});
			}
			return result;
		}

		static readonly DependencyProperty[] LayoutAttachedProperties = {
			Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty,
			Canvas.LeftProperty, Canvas.TopProperty, Canvas.RightProperty, Canvas.BottomProperty,
			DockPanel.DockProperty
		};

		static IEnumerable<DependencyProperty> ParentLayoutAttachedProperties(object? parent) => parent switch {
			Grid => new[] { Grid.RowProperty, Grid.ColumnProperty, Grid.RowSpanProperty, Grid.ColumnSpanProperty },
			Canvas => new[] { Canvas.LeftProperty, Canvas.TopProperty, Canvas.RightProperty, Canvas.BottomProperty },
			DockPanel => new[] { DockPanel.DockProperty },
			_ => Array.Empty<DependencyProperty>()
		};

		/// <summary>An editable item kind: the container each item must be, and the property holding
		/// its text (Content for list items, Header for tab pages).</summary>
		sealed record EditableItemKind(Type ContainerType, string TextProperty);

		/// <summary>The item containers a plain-list editor handles; other ItemsControls (TreeView,
		/// menus) have nested or command semantics this text list cannot express.</summary>
		static readonly Dictionary<Type, EditableItemKind> editableItemContainers = new() {
			[typeof(ListBox)] = new(typeof(ListBoxItem), "Content"),
			[typeof(ComboBox)] = new(typeof(ComboBoxItem), "Content"),
			[typeof(ListView)] = new(typeof(ListViewItem), "Content"),
			[typeof(TabControl)] = new(typeof(TabItem), "Header")
		};

		/// <summary>True when the element's Items are a plain, editable list: no ItemsSource, and every
		/// item is the control's own container type holding plain text (or nothing) in its text
		/// property (Content, or a TabItem's Header).</summary>
		static bool TryGetEditableItems(DesignItem item, out DesignItemProperty? itemsProperty, out EditableItemKind? kind)
		{
			itemsProperty = null;
			kind = null;
			if (item.Component is not ItemsControl control || !editableItemContainers.TryGetValue(control.GetType(), out kind))
				return false;
			if (item.Properties["ItemsSource"]?.IsSet == true)
				return false;
			var items = item.Properties["Items"];
			if (items == null || !items.IsCollection)
				return false;
			var expected = kind;
			// A tab page's Content is its body and stays untouched; only its Header text is edited.
			if (!items.CollectionElements.All(element => element.Component?.GetType() == expected.ContainerType
				&& element.Properties[expected.TextProperty] is var text && (!text.IsSet || text.Value == null && text.ValueOnInstance is string)))
				return false;
			itemsProperty = items;
			return true;
		}

		/// <summary>Replaces a ListBox/ComboBox/ListView's plain items (or a TabControl's page headers) in one undo group. Each entry
		/// names the item's previous index (moved items keep their other authored state) or -1 for
		/// a new container item; items not named are removed.</summary>
		[JsonRpcMethod("design/set-items")]
		public DesignerSessionState SetItems(long baseVersion, string elementId, DesignerCollectionItemEdit[] items)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var element))
					return NotFound(state, "Element not found: " + elementId);
				if (!TryGetEditableItems(element, out var itemsProperty, out var kind) || itemsProperty == null || kind == null)
					return NotFound(state, "The element's Items are not an editable plain list.");
				items ??= Array.Empty<DesignerCollectionItemEdit>();
				var existing = itemsProperty.CollectionElements.ToList();
				var sources = items.Where(entry => entry.SourceIndex >= 0).Select(entry => entry.SourceIndex).ToList();
				if (sources.Any(index => index >= existing.Count) || sources.Distinct().Count() != sources.Count)
					return NotFound(state, "Invalid item source index.");
				try
				{
					using var changeGroup = element.OpenGroup("Edit Items");
					var collection = itemsProperty.CollectionElements;
					collection.Clear();
					foreach (var entry in items)
					{
						var designItem = entry.SourceIndex >= 0
							? existing[entry.SourceIndex]
							: element.Services.Component.RegisterComponentForDesigner(Activator.CreateInstance(kind.ContainerType)!);
						collection.Add(designItem);
						var content = designItem.Properties[kind.TextProperty];
						if (string.IsNullOrEmpty(entry.Text))
							content.Reset();
						else if (!Equals(content.ValueOnInstance, entry.Text) || !content.IsSet)
							content.SetValue(entry.Text);
					}
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		/// <summary>Edits the direct child collection of a MultiBinding or PriorityBinding where
		/// every existing child is a plain Binding. Existing source indices retain their complete
		/// design items (Mode, Source, converters and other authored settings); new entries create
		/// only a blank Binding whose Path is set below. A complex/non-Binding child makes the whole
		/// collection unavailable rather than risking a lossy graph rewrite.</summary>
		[JsonRpcMethod("design/set-binding-items")]
		public DesignerSessionState SetBindingItems(long baseVersion, string elementId, string propertyName, DesignerCollectionItemEdit[] items)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var element))
					return NotFound(state, "Element not found: " + elementId);
				if (!TryGetEditableBindingItems(element, propertyName, out var bindingsProperty) || bindingsProperty == null)
					return NotFound(state, "The Binding collection is not safely editable: " + propertyName);
				items ??= Array.Empty<DesignerCollectionItemEdit>();
				if (items.Length == 0)
					return NotFound(state, "A composite Binding must contain at least one Binding.");
				var existing = bindingsProperty.CollectionElements.ToList();
				var sources = items.Where(entry => entry.SourceIndex >= 0).Select(entry => entry.SourceIndex).ToList();
				if (items.Any(entry => entry.SourceIndex < -1) || sources.Any(index => index >= existing.Count)
					|| sources.Distinct().Count() != sources.Count)
					return NotFound(state, "Invalid Binding source index.");
				try
				{
					using var changeGroup = element.OpenGroup("Edit Bindings");
					var collection = bindingsProperty.CollectionElements;
					collection.Clear();
					foreach (var entry in items)
					{
						var binding = entry.SourceIndex >= 0
							? existing[entry.SourceIndex]
							: element.Services.Component.RegisterComponentForDesigner(new System.Windows.Data.Binding());
						collection.Add(binding);
						var path = binding.Properties["Path"];
						if (string.IsNullOrEmpty(entry.Text))
							path.Reset();
						else if (!Equals(path.ValueOnInstance, entry.Text) || !path.IsSet)
							path.SetValue(entry.Text);
					}
					changeGroup.Commit();
				}
				catch (Exception e) { return NotFound(state, e.GetBaseException().Message); }
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		static bool TryGetEditableBindingItems(DesignItem element, string propertyName, out DesignItemProperty? bindingsProperty)
		{
			bindingsProperty = null;
			if (String.IsNullOrWhiteSpace(propertyName)
				|| element.Properties[propertyName]?.Value is not DesignItem binding
				|| binding.Component is not (System.Windows.Data.MultiBinding or System.Windows.Data.PriorityBinding))
				return false;
			var children = binding.Properties["Bindings"]?.CollectionElements;
			if (children == null || !children.All(child => child.Component is System.Windows.Data.Binding))
				return false;
			bindingsProperty = binding.Properties["Bindings"];
			return true;
		}

		/// <summary>Publishes WPF's browsable XAML events separately from ordinary properties.
		/// Handler names are source text, never runtime delegates, so listing them cannot invoke
		/// project code.</summary>
		static List<DesignerEventInfo> BuildEvents(DesignItem item)
		{
			if (item.ComponentType == null)
				return new List<DesignerEventInfo>();
			var result = new List<DesignerEventInfo>();
			foreach (var descriptor in BrowsableEvents(item.ComponentType))
			{
				try
				{
					var property = item.Properties[descriptor.Name];
					if (property?.IsEvent == true)
						result.Add(new DesignerEventInfo {
							Name = descriptor.Name,
							Category = string.IsNullOrEmpty(descriptor.Category) ? "Events" : descriptor.Category,
							HandlerTypeName = descriptor.EventType?.FullName ?? "",
							Handler = property.TextValue ?? ""
						});
				}
				catch { /* one lazy event must not prevent the rest of the snapshot */ }
			}
			return result;
		}

		/// <summary>The property types an UNSET property is offered the resource picker for: references to a shared Style, template or
		/// context menu are what a resource is normally for. (A property that already holds a reference gets it whatever its type.)</summary>
		static bool IsResourceReferenceType(Type? type)
			=> type != null && (type == typeof(Style) || typeof(FrameworkTemplate).IsAssignableFrom(type) || type == typeof(ContextMenu));

		static DesignerPropertyInfo MarkResourceReference(DesignerPropertyInfo info, DesignItemProperty property)
		{
			if (string.IsNullOrEmpty(info.EditorKind) && !property.IsSet && IsResourceReferenceType(property.ReturnType))
			{
				info.Kind = "Reference";
				info.IsReadOnly = true;
				info.IsNull = false;
				info.EditorKind = "ResourceReference";
				info.Value = "";
			}
			return info;
		}

		/// <summary>An unset or attribute-text <see cref="ImageSource"/> (Image.Source, Window.Icon...)
		/// gets the IDE's project-image picker. The picker only offers candidates; the write is the
		/// usual design/set-property, which keeps an image URI as its authored text.</summary>
		static DesignerPropertyInfo MarkImageSource(DesignerPropertyInfo info, DesignItemProperty property)
		{
			if (string.IsNullOrEmpty(info.EditorKind) && !info.IsReadOnly && info.Kind is "String" or "Null"
				&& property.ReturnType != null && typeof(ImageSource).IsAssignableFrom(property.ReturnType))
				info.EditorKind = "ImageSource";
			return info;
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
				// DesignItemProperty.Value is the design-model value.  ValueOnInstance can flatten
				// a Binding to its runtime string representation, which is useful for display but
				// loses the distinction between editable literal text and an object reference.
				if (property.Value is DesignItem reference)
				{
					// Binding has one deliberately narrow structured editor: its Path. The host
					// changes that nested design item through design/set-binding-path; exposing
					// arbitrary markup here would let a Properties-pad string edit turn the
					// Binding into literal text. Other nested values stay read-only References.
					if (reference.Component is System.Windows.Data.Binding binding)
					{
						info.Kind = "String";
						info.EditorKind = "BindingPath";
						info.Value = binding.Path?.Path ?? "";
						return info;
					}
					// A brush written as an element (a gradient, or a SolidColorBrush element) has no
					// lossless text form, but the brush editor can round-trip the plain shapes.
					if (reference.Component is StaticResourceExtension
						&& reference.Properties["ResourceKey"].ValueOnInstance is string brushKey
						&& property.ReturnType != null && typeof(Brush).IsAssignableFrom(property.ReturnType))
					{
						info.Kind = "Brush";
						info.IsReadOnly = true;
						info.EditorKind = "Brush";
						info.Brush = new DesignerBrushInfo { Kind = "Resource", ResourceKey = brushKey };
						info.Value = "{StaticResource " + brushKey + "}";
						return info;
					}
					if (reference.Component is DynamicResourceExtension
						&& reference.Properties["ResourceKey"].Value?.Component is System.Windows.Markup.StaticExtension { Member: { } member }
						&& SystemBrushMember(member) is { } systemKey
						&& property.ReturnType != null && typeof(Brush).IsAssignableFrom(property.ReturnType))
					{
						info.Kind = "Brush";
						info.IsReadOnly = true;
						info.EditorKind = "Brush";
						info.Brush = new DesignerBrushInfo { Kind = "System", ResourceKey = systemKey };
						info.Value = "{DynamicResource {x:Static SystemColors." + systemKey + "}}";
						return info;
					}
					if (reference.Component is System.Windows.Markup.NullExtension
						&& property.ReturnType != null && typeof(Brush).IsAssignableFrom(property.ReturnType))
					{
						info.Kind = "Null";
						info.IsNull = true;
						info.IsReadOnly = true;
						info.EditorKind = "Brush";
						info.Brush = new DesignerBrushInfo { Kind = "Null" };
						info.Value = "No brush";
						return info;
					}
					if (reference.Component is SolidColorBrush && DescribeColorResourceBrush(reference) is { } colorResource
						&& property.ReturnType != null && typeof(Brush).IsAssignableFrom(property.ReturnType))
					{
						info.Kind = "Brush";
						info.IsReadOnly = true;
						info.EditorKind = "Brush";
						info.Brush = colorResource;
						info.Value = BrushDisplayText(colorResource);
						return info;
					}
					if (reference.Component is ImageBrush && DescribeImageBrush(reference) is { } image
						&& property.ReturnType != null && typeof(Brush).IsAssignableFrom(property.ReturnType))
					{
						info.Kind = "Brush";
						info.IsReadOnly = true;
						info.EditorKind = "Brush";
						info.Brush = image;
						info.Value = BrushDisplayText(image);
						return info;
					}
					if (reference.Component is DrawingBrush or VisualBrush && DescribeContentTileBrush(reference) is { } contentBrush
						&& property.ReturnType != null && typeof(Brush).IsAssignableFrom(property.ReturnType))
					{
						info.Kind = "Brush";
						info.IsReadOnly = true;
						info.EditorKind = "Brush";
						info.Brush = contentBrush;
						info.Value = BrushDisplayText(contentBrush);
						return info;
					}
					if (reference.Component is Brush elementBrush && TryDescribeBrushTransforms(reference, out var elementTransform, out var elementRelative)
						&& DescribeBrush(elementBrush, ignoreTransforms: true) is { } described)
					{
						described.Transform = elementTransform;
						described.RelativeTransform = elementRelative;
						info.Kind = "Brush";
						info.IsReadOnly = true;
						info.EditorKind = "Brush";
						info.Brush = described;
						info.Value = BrushDisplayText(described);
						return info;
					}
					// A {StaticResource key}/{DynamicResource key} on any other property gets the resource picker.
					if (property.ReturnType != null && !typeof(Brush).IsAssignableFrom(property.ReturnType)
						&& reference.Component is StaticResourceExtension or DynamicResourceExtension
						&& reference.Properties["ResourceKey"].ValueOnInstance is string referencedKey)
					{
						info.Kind = "Reference";
						info.IsReadOnly = true;
						info.EditorKind = "ResourceReference";
						info.ResourceReferenceKind = reference.Component is StaticResourceExtension ? "Static" : "Dynamic";
						info.Value = "{" + info.ResourceReferenceKind + "Resource " + referencedKey + "}";
						return info;
					}
					if (reference.Component is Transform && property.ReturnType != null && typeof(Transform).IsAssignableFrom(property.ReturnType)
						&& DescribeTransform(reference) is { } transformOps)
					{
						info.Kind = "Transform";
						info.IsReadOnly = true;
						info.EditorKind = "Transform";
						info.Transform = transformOps;
						info.Value = TransformDisplayText(transformOps);
						return info;
					}
					info.Kind = "Reference";
					info.IsReadOnly = true;
					info.Value = reference.ToString() ?? "";
					return info;
				}
				// An unset (identity) RenderTransform/LayoutTransform opens the transform editor, which can create one.
				if (!property.IsSet && property.ReturnType == typeof(Transform))
				{
					info.Kind = "Transform";
					info.IsReadOnly = true;
					info.EditorKind = "Transform";
					info.Transform = new List<DesignerTransformOp>();
					info.Value = TransformDisplayText(info.Transform);
					return info;
				}
				var value = property.ValueOnInstance;
				if (value == null)
				{
					info.IsNull = true;
					info.Kind = "Null";
					if (property.ReturnType != null && typeof(Brush).IsAssignableFrom(property.ReturnType) && !property.IsSet)
						info.EditorKind = "Brush";
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
				// A nested DesignItem is a source/model reference (the normal shape for a Binding,
				// object-valued Content, Transform, etc.), not an opaque conversion failure.  It is
				// intentionally read-only until the protocol grows a structured reference editor:
				// serializing ToString() back into XAML would corrupt the document.  Publishing the
				// distinct kind lets the shell explain that limitation instead of claiming the value
				// is unsupported by the runtime.
				if (value is DesignItem)
				{
					info.Kind = "Reference";
					info.IsReadOnly = true;
					info.Value = value.ToString() ?? "";
					return info;
				}
				var converter = property.ReturnType != null ? TypeDescriptor.GetConverter(property.ReturnType) : null;
				if (converter != null && converter.CanConvertTo(typeof(string)) && converter.CanConvertFrom(typeof(string)))
				{
					info.Value = converter.ConvertToInvariantString(value) ?? "";
					info.IsEnum = property.ReturnType!.IsEnum;
					info.Kind = PropertyKind(property.ReturnType, info.IsEnum);
					// The wire value for an enum is intentionally still its invariant XAML text;
					// AllowedValues supplies the presentation contract that lets a remote property
					// grid render a constrained drop-down without loading the child's CLR type.
					if (info.IsEnum)
						info.AllowedValues.AddRange(Enum.GetNames(property.ReturnType));
					// An unset or attribute-text brush also opens the brush editor, which can turn
					// it into a gradient.
					if (info.Kind == "Brush" && value is Brush textBrush && (!property.IsSet || textBrush is SolidColorBrush)
						&& DescribeBrush(textBrush) is { } described)
					{
						info.EditorKind = "Brush";
						info.Brush = described;
					}
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

		/// <summary>The brush as the brush editor's lossless shape, or null when it carries anything
		/// the editor would drop: a transform, a non-default color interpolation, or a frozen/bound
		/// value the instance cannot describe.</summary>
		static DesignerBrushInfo? DescribeBrush(Brush brush, bool ignoreTransforms = false)
		{
			// The caller that has the design item describes the transforms itself (TryDescribeBrushTransforms); one that only has
			// the live brush cannot tell a plain transform from a bound one, so it keeps a transformed brush a reference.
			if (!ignoreTransforms && (brush.Transform != null && !brush.Transform.Value.IsIdentity
				|| brush.RelativeTransform != null && !brush.RelativeTransform.Value.IsIdentity))
				return null;
			switch (brush)
			{
				case SolidColorBrush solid:
					return new DesignerBrushInfo { Kind = "Solid", Color = solid.Color.ToString(CultureInfo.InvariantCulture), Opacity = solid.Opacity };
				case GradientBrush gradient when gradient.ColorInterpolationMode == ColorInterpolationMode.SRgbLinearInterpolation:
					var info = new DesignerBrushInfo {
						Opacity = gradient.Opacity,
						MappingMode = gradient.MappingMode.ToString(),
						SpreadMethod = gradient.SpreadMethod.ToString(),
						Stops = gradient.GradientStops.Select(stop => new DesignerGradientStopInfo {
							Color = stop.Color.ToString(CultureInfo.InvariantCulture), Offset = stop.Offset
						}).ToList()
					};
					switch (gradient)
					{
						case LinearGradientBrush linear:
							info.Kind = "LinearGradient";
							(info.StartX, info.StartY, info.EndX, info.EndY) = (linear.StartPoint.X, linear.StartPoint.Y, linear.EndPoint.X, linear.EndPoint.Y);
							return info;
						case RadialGradientBrush radial:
							info.Kind = "RadialGradient";
							(info.CenterX, info.CenterY, info.OriginX, info.OriginY, info.RadiusX, info.RadiusY)
								= (radial.Center.X, radial.Center.Y, radial.GradientOrigin.X, radial.GradientOrigin.Y, radial.RadiusX, radial.RadiusY);
							return info;
					}
					return null;
				default:
					return null;
			}
		}

		/// <summary>Whether <paramref name="name"/> is a public static <c>SystemColors.*BrushKey</c>.</summary>
		static bool IsSystemBrushKey(string? name)
			=> !string.IsNullOrEmpty(name) && name.EndsWith("BrushKey", StringComparison.Ordinal)
				&& typeof(SystemColors).GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.PropertyType == typeof(ResourceKey);

		/// <summary>The SystemColors brush key an <c>x:Static</c> member names (<c>SystemColors.X</c>,
		/// optionally with an XML prefix), or null for anything else.</summary>
		static string? SystemBrushMember(string member)
		{
			var colon = member.IndexOf(':');
			var name = colon >= 0 ? member.Substring(colon + 1) : member;
			const string owner = "SystemColors.";
			if (!name.StartsWith(owner, StringComparison.Ordinal))
				return null;
			name = name.Substring(owner.Length);
			return IsSystemBrushKey(name) ? name : null;
		}

		/// <summary>An <see cref="ImageBrush"/> the editor can round-trip: its ImageSource is authored
		/// attribute text (not a binding or resource), and nothing but Stretch, Opacity and the tile
		/// settings is set besides; anything else stays a read-only reference.</summary>
		static DesignerBrushInfo? DescribeImageBrush(DesignItem brush)
		{
			if (brush.Properties["ImageSource"] is not { IsSet: true, TextValue: { Length: > 0 } source })
				return null;
			var info = DescribeTileBrush(brush, "Image");
			if (info != null)
				info.ImageSource = source;
			return info;
		}

		/// <summary>A <see cref="DrawingBrush"/> or <see cref="VisualBrush"/>: its content (a Drawing
		/// or Visual) is not edited here, only its Stretch, Opacity and tile settings, which
		/// <c>design/set-brush</c> changes in place without touching the content.</summary>
		static DesignerBrushInfo? DescribeContentTileBrush(DesignItem brush)
			=> brush.Component switch {
				DrawingBrush => DescribeTileBrush(brush, "Drawing"),
				VisualBrush => DescribeTileBrush(brush, "Visual"),
				_ => null
			};

		/// <summary>The Stretch/Opacity/tile settings of a TileBrush item as authored attribute text, or
		/// null when a transform, or any setting that is not attribute text, would be lost.</summary>
		static DesignerBrushInfo? DescribeTileBrush(DesignItem brush, string kind)
		{
			// DDP's property collection only enumerates properties already accessed, so name every
			// other TileBrush/Brush setting explicitly.
			if (!TryDescribeBrushTransforms(brush, out var transform, out var relativeTransform))
				return null;
			var stretch = brush.Properties["Stretch"];
			var opacity = brush.Properties["Opacity"];
			if (stretch.IsSet && stretch.TextValue == null || opacity.IsSet && opacity.TextValue == null)
				return null;
			// The tile settings are authored attribute text; a property element or markup extension
			// for one keeps the brush a read-only reference.
			string Tile(string name, out bool ok)
			{
				var property = brush.Properties[name];
				ok = !property.IsSet || property.TextValue != null;
				return property.IsSet ? property.TextValue ?? "" : "";
			}
			var tileMode = Tile("TileMode", out var okTileMode);
			var alignmentX = Tile("AlignmentX", out var okAlignmentX);
			var alignmentY = Tile("AlignmentY", out var okAlignmentY);
			var viewport = Tile("Viewport", out var okViewport);
			var viewportUnits = Tile("ViewportUnits", out var okViewportUnits);
			var viewbox = Tile("Viewbox", out var okViewbox);
			var viewboxUnits = Tile("ViewboxUnits", out var okViewboxUnits);
			if (!(okTileMode && okAlignmentX && okAlignmentY && okViewport && okViewportUnits && okViewbox && okViewboxUnits))
				return null;
			return new DesignerBrushInfo {
				Kind = kind,
				Transform = transform, RelativeTransform = relativeTransform,
				Stretch = stretch.IsSet ? stretch.TextValue! : "",
				TileMode = tileMode, AlignmentX = alignmentX, AlignmentY = alignmentY,
				Viewport = viewport, ViewportUnits = viewportUnits, Viewbox = viewbox, ViewboxUnits = viewboxUnits,
				Opacity = opacity.IsSet && double.TryParse(opacity.TextValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : 1
			};
		}

		/// <summary>Writes a tile brush item's Stretch, Opacity and tile settings in place: a default or
		/// blank value resets the property, anything else sets the authored text.</summary>
		static void ApplyTileSettings(DesignItem brush, DesignerBrushInfo info)
		{
			void Apply(string name, string text, string defaultText = "")
			{
				var property = brush.Properties[name];
				if (string.IsNullOrWhiteSpace(text) || text.Trim() == defaultText) { if (property.IsSet) property.Reset(); }
				else property.SetValue(text.Trim());
			}
			Apply("Stretch", info.Stretch, nameof(Stretch.Fill));
			if (info.Opacity != 1) brush.Properties["Opacity"].SetValue(info.Opacity);
			else if (brush.Properties["Opacity"].IsSet) brush.Properties["Opacity"].Reset();
			Apply("TileMode", info.TileMode, "None");
			Apply("AlignmentX", info.AlignmentX, "Center");
			Apply("AlignmentY", info.AlignmentY, "Center");
			Apply("Viewport", info.Viewport);
			Apply("ViewportUnits", info.ViewportUnits, "RelativeToBoundingBox");
			Apply("Viewbox", info.Viewbox);
			Apply("ViewboxUnits", info.ViewboxUnits, "RelativeToBoundingBox");
		}

		/// <summary>A SolidColorBrush element whose Color is an authored <c>{StaticResource key}</c> and
		/// which sets nothing else but Opacity (as attribute text).</summary>
		static DesignerBrushInfo? DescribeColorResourceBrush(DesignItem brush)
		{
			if (brush.Properties["Color"].Value is not { Component: StaticResourceExtension } reference
				|| reference.Properties["ResourceKey"].ValueOnInstance is not string key
				|| !TryDescribeBrushTransforms(brush, out var transform, out var relativeTransform))
				return null;
			var opacity = brush.Properties["Opacity"];
			if (opacity.IsSet && opacity.TextValue == null)
				return null;
			return new DesignerBrushInfo {
				Kind = "ColorResource",
				ResourceKey = key,
				Transform = transform, RelativeTransform = relativeTransform,
				Opacity = opacity.IsSet && double.TryParse(opacity.TextValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : 1
			};
		}

		/// <summary>The brush item's <c>Transform</c> and <c>RelativeTransform</c> as simple operations (empty when unset); false when either holds
		/// something the transform editor cannot round-trip (a Matrix, a bound or named transform), which keeps the brush a read-only reference.</summary>
		static bool TryDescribeBrushTransforms(DesignItem brush, out List<DesignerTransformOp> transform, out List<DesignerTransformOp> relativeTransform)
		{
			transform = new List<DesignerTransformOp>();
			relativeTransform = new List<DesignerTransformOp>();
			foreach (var (name, target) in new[] { ("Transform", transform), ("RelativeTransform", relativeTransform) })
			{
				var property = brush.Properties[name];
				if (!property.IsSet)
					continue;
				if (property.Value is not { } value || DescribeTransform(value) is not { } ops)
					return false;
				target.AddRange(ops);
			}
			return true;
		}

		/// <summary>Writes the brush's transforms onto an existing brush item: a list replaces the property (reset first), an empty one clears it.</summary>
		static void ApplyBrushTransforms(DesignItem owner, DesignItem brushItem, DesignerBrushInfo info)
		{
			foreach (var (name, ops) in new[] { ("Transform", info.Transform), ("RelativeTransform", info.RelativeTransform) })
			{
				var property = brushItem.Properties[name];
				if (property.IsSet)
					property.Reset();
				if (ops != null && ops.Count > 0)
					property.SetValue(CreateTransformItem(owner, ops));
			}
		}

		/// <summary>Why the brush's transforms are not writable, or null.</summary>
		static string? BrushTransformsError(DesignerBrushInfo brush)
		{
			var transform = brush.Transform ?? new List<DesignerTransformOp>();
			var relative = brush.RelativeTransform ?? new List<DesignerTransformOp>();
			if (TransformOpsError(transform) is { } error)
				return error;
			if (TransformOpsError(relative) is { } relativeError)
				return relativeError;
			if ((transform.Count > 0 || relative.Count > 0) && brush.Kind is "Null" or "Resource" or "System")
				return "A brush that is a reference or no brush has no transform of its own.";
			return null;
		}

		/// <summary>Why an image brush's tile settings are not writable, or null: every enum must be a
		/// defined member and a Viewport/Viewbox must parse as <c>x,y,width,height</c> with a
		/// non-negative size.</summary>
		static string? TileSettingsError(DesignerBrushInfo brush)
		{
			static bool Defined<T>(string text) where T : struct, Enum => string.IsNullOrEmpty(text) || Enum.TryParse<T>(text, out _) && Enum.IsDefined(typeof(T), text);
			if (!Defined<TileMode>(brush.TileMode)) return "TileMode must be None, Tile, FlipX, FlipY or FlipXY.";
			if (!Defined<AlignmentX>(brush.AlignmentX)) return "AlignmentX must be Left, Center or Right.";
			if (!Defined<AlignmentY>(brush.AlignmentY)) return "AlignmentY must be Top, Center or Bottom.";
			if (!Defined<BrushMappingMode>(brush.ViewportUnits) || !Defined<BrushMappingMode>(brush.ViewboxUnits))
				return "Viewport and Viewbox units must be Absolute or RelativeToBoundingBox.";
			foreach (var (name, text) in new[] { ("Viewport", brush.Viewport), ("Viewbox", brush.Viewbox) })
			{
				if (string.IsNullOrWhiteSpace(text)) continue;
				try
				{
					var rect = System.Windows.Rect.Parse(text);
					if (rect.IsEmpty || rect.Width < 0 || rect.Height < 0 || double.IsNaN(rect.X + rect.Y + rect.Width + rect.Height))
						return name + " needs a non-negative width and height.";
				}
				catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
				{
					return name + " must be x,y,width,height with a non-negative size.";
				}
			}
			return null;
		}

		/// <summary>Points a property at a resource in scope - <c>{StaticResource key}</c> or <c>{DynamicResource key}</c>, the key
		/// one of the property's <see cref="DesignerPropertyInfo.ResourceReferenceKeys"/> - or clears the reference (empty kind).
		/// Only a property that is unset or already holds such a reference is rewritten, so an inline value is never lost, and
		/// a Brush property (its own editor) is refused.</summary>
		[JsonRpcMethod("design/set-resource-reference")]
		public DesignerSessionState SetResourceReference(long baseVersion, string elementId, string propertyName, string kind, string key)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				DesignItemProperty? property;
				try { property = item.Properties[propertyName]; }
				catch (ArgumentException e) { return NotFound(state, e.Message); }
				if (property?.ReturnType == null || typeof(Brush).IsAssignableFrom(property.ReturnType))
					return NotFound(state, "Not a resource-reference property: " + propertyName);
				var isReference = property.Value is { } current
					&& current.Component is StaticResourceExtension or DynamicResourceExtension
					&& current.Properties["ResourceKey"].ValueOnInstance is string;
				if (property.IsSet && !isReference)
					return NotFound(state, propertyName + " holds a value that is not a resource reference.");
				if (kind is not ("" or "Static" or "Dynamic"))
					return NotFound(state, "Resource reference kind must be Static, Dynamic or empty.");
				var type = property.ReturnType;
				if (kind != "" && !ResourceKeys(item, value => value != null && type.IsInstanceOfType(value)).Contains(key ?? ""))
					return NotFound(state, "Not a resource of a matching type in scope: " + key);
				try
				{
					using var changeGroup = item.OpenGroup("Set " + propertyName);
					if (property.IsSet)
						property.Reset();
					if (kind != "")
					{
						property.SetValue(kind == "Static" ? new StaticResourceExtension() : (object)new DynamicResourceExtension());
						property.Value!.Properties["ResourceKey"].SetValue(key);
					}
					changeGroup.Commit();
				}
				catch (Exception e) when (e is ArgumentException or FormatException or NotSupportedException or InvalidOperationException)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		static readonly string[] TransformOpTypes = { "Rotate", "Scale", "Skew", "Translate" };

		/// <summary>The properties of each simple transform the editor writes, with their defaults.</summary>
		static (string Name, double Default)[] TransformFields(string type) => type switch {
			"Rotate" => new[] { ("Angle", 0d), ("CenterX", 0d), ("CenterY", 0d) },
			"Scale" => new[] { ("ScaleX", 1d), ("ScaleY", 1d), ("CenterX", 0d), ("CenterY", 0d) },
			"Skew" => new[] { ("AngleX", 0d), ("AngleY", 0d), ("CenterX", 0d), ("CenterY", 0d) },
			"Translate" => new[] { ("X", 0d), ("Y", 0d) },
			_ => Array.Empty<(string, double)>()
		};

		static double OpValue(DesignerTransformOp op, string name) => name switch {
			"Angle" => op.Angle, "AngleX" => op.AngleX, "AngleY" => op.AngleY, "ScaleX" => op.ScaleX, "ScaleY" => op.ScaleY,
			"X" => op.X, "Y" => op.Y, "CenterX" => op.CenterX, _ => op.CenterY
		};

		static void SetOpValue(DesignerTransformOp op, string name, double value)
		{
			switch (name)
			{
				case "Angle": op.Angle = value; break; case "AngleX": op.AngleX = value; break; case "AngleY": op.AngleY = value; break;
				case "ScaleX": op.ScaleX = value; break; case "ScaleY": op.ScaleY = value; break;
				case "X": op.X = value; break; case "Y": op.Y = value; break;
				case "CenterX": op.CenterX = value; break; default: op.CenterY = value; break;
			}
		}

		/// <summary>The transform as the editor's list of simple operations, or null when it holds anything the editor would
		/// lose: a transform it does not know (Matrix, nested groups), a value that is not plain attribute text (a
		/// binding), or a property it does not manage.</summary>
		static List<DesignerTransformOp>? DescribeTransform(DesignItem transform)
		{
			// The write builds fresh transform elements, so a named one (x:Name, referenced from code or bindings) would lose its name.
			if (!string.IsNullOrEmpty(transform.Name))
				return null;
			if (transform.Component is TransformGroup)
			{
				if (transform.Properties["Children"].CollectionElements is not { } children)
					return null;
				var ops = new List<DesignerTransformOp>();
				foreach (var child in children)
				{
					if (child.Component is TransformGroup || DescribeSimpleTransform(child) is not { } op)
						return null;
					ops.Add(op);
				}
				return ops;
			}
			return DescribeSimpleTransform(transform) is { } single ? new List<DesignerTransformOp> { single } : null;
		}

		static DesignerTransformOp? DescribeSimpleTransform(DesignItem item)
		{
			var type = item.Component switch {
				RotateTransform => "Rotate", ScaleTransform => "Scale", SkewTransform => "Skew", TranslateTransform => "Translate", _ => null
			};
			if (type == null || !string.IsNullOrEmpty(item.Name))
				return null;
			var op = new DesignerTransformOp { Type = type };
			foreach (var (name, _) in TransformFields(type))
			{
				var property = item.Properties[name];
				if (!property.IsSet)
					continue;
				// A non-finite value (NaN, or text that overflows to Infinity) could never be written back, so keep it a reference.
				if (property.TextValue == null || !double.TryParse(property.TextValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
					|| !double.IsFinite(number))
					return null;
				SetOpValue(op, name, number);
			}
			return op;
		}

		static string TransformDisplayText(IReadOnlyList<DesignerTransformOp> ops)
			=> ops.Count == 0 ? "(none)" : string.Join(", ", ops.Select(op => op.Type switch {
				"Rotate" => "Rotate(" + op.Angle.ToString(CultureInfo.InvariantCulture) + ")",
				"Scale" => "Scale(" + op.ScaleX.ToString(CultureInfo.InvariantCulture) + ", " + op.ScaleY.ToString(CultureInfo.InvariantCulture) + ")",
				"Skew" => "Skew(" + op.AngleX.ToString(CultureInfo.InvariantCulture) + ", " + op.AngleY.ToString(CultureInfo.InvariantCulture) + ")",
				_ => "Translate(" + op.X.ToString(CultureInfo.InvariantCulture) + ", " + op.Y.ToString(CultureInfo.InvariantCulture) + ")"
			}));

		/// <summary>Why the operations are not writable, or null: every type is Rotate/Scale/Skew/Translate and every number is finite.</summary>
		static string? TransformOpsError(IReadOnlyList<DesignerTransformOp>? ops)
		{
			if (ops == null)
				return "A transform edit needs its operation list.";
			foreach (var op in ops)
			{
				if (op == null || !TransformOpTypes.Contains(op.Type))
					return "A transform is Rotate, Scale, Skew or Translate.";
				foreach (var (name, _) in TransformFields(op.Type))
					if (!double.IsFinite(OpValue(op, name)))
						return name + " must be a finite number.";
			}
			return null;
		}

		/// <summary>Builds the design item for the operations: one is that transform, several a TransformGroup. Only values that differ
		/// from the default are set, so the written XAML stays minimal. Built through the design model (not a CLR object whose
		/// properties were assigned) so every value is serialized.</summary>
		static DesignItem CreateTransformItem(DesignItem owner, IReadOnlyList<DesignerTransformOp> ops)
		{
			DesignItem One(DesignerTransformOp op)
			{
				Transform transform = op.Type switch {
					"Rotate" => new RotateTransform(), "Scale" => new ScaleTransform(), "Skew" => new SkewTransform(), _ => new TranslateTransform()
				};
				var item = owner.Services.Component.RegisterComponentForDesigner(transform);
				foreach (var (name, defaultValue) in TransformFields(op.Type))
					if (OpValue(op, name) != defaultValue)
						item.Properties[name].SetValue(OpValue(op, name));
				return item;
			}
			if (ops.Count == 1)
				return One(ops[0]);
			var group = owner.Services.Component.RegisterComponentForDesigner(new TransformGroup());
			foreach (var op in ops)
				group.Properties["Children"].CollectionElements.Add(One(op));
			return group;
		}

		/// <summary>Replaces a Transform property (RenderTransform, LayoutTransform) with the given simple transforms as one change:
		/// none clears it, one is written as that transform element, several as a TransformGroup. Refuses a property that is
		/// not a Transform, or one that currently holds something the editor cannot describe (so nothing is lost).</summary>
		[JsonRpcMethod("design/set-transform")]
		public DesignerSessionState SetTransform(long baseVersion, string elementId, string propertyName, DesignerTransformOp[] ops)
			=> dispatcher.Dispatch(() => {
				if (RejectIfStale(baseVersion) is { } stale)
					return stale;
				var state = NewState(baseVersion);
				if (!pathToItem.TryGetValue(elementId, out var item))
					return NotFound(state, "Element not found: " + elementId);
				DesignItemProperty? property;
				try { property = item.Properties[propertyName]; }
				catch (ArgumentException e) { return NotFound(state, e.Message); }
				if (property?.ReturnType != typeof(Transform))
					return NotFound(state, "Not a Transform property: " + propertyName);
				if (property.IsSet && !(property.Value is { } current && DescribeTransform(current) != null))
					return NotFound(state, propertyName + " holds a value the transform editor cannot represent.");
				if (TransformOpsError(ops) is { } error)
					return NotFound(state, error);
				try
				{
					using var changeGroup = item.OpenGroup("Set " + propertyName);
					if (property.IsSet)
						property.Reset();
					if (ops.Length > 0)
						property.SetValue(CreateTransformItem(item, ops));
					changeGroup.Commit();
				}
				catch (Exception e) when (e is ArgumentException or FormatException or NotSupportedException or InvalidOperationException or OverflowException)
				{
					return NotFound(state, e.GetBaseException().Message);
				}
				RebuildTreeAndRender(state);
				state.Accepted = true;
				return state;
			});

		static string BrushDisplayText(DesignerBrushInfo brush) => brush.Kind switch {
			"ColorResource" => "Color {StaticResource " + brush.ResourceKey + "}",
			"Image" => "Image (" + brush.ImageSource + ")",
			"Drawing" => "Drawing brush",
			"Visual" => "Visual brush",
			"Solid" => brush.Color,
			"LinearGradient" => "Linear gradient (" + brush.Stops.Count + " stops)",
			_ => "Radial gradient (" + brush.Stops.Count + " stops)"
		};

		/// <summary>Builds the WPF brush a <see cref="DesignerBrushInfo"/> describes (null for "No
		/// brush"), setting only non-default values so the XAML stays minimal; throws for a malformed
		/// one (unknown kind/mode, bad color, an offset outside 0-1, a gradient without stops).</summary>
		static Brush? CreateBrush(DesignerBrushInfo info)
		{
			static System.Windows.Media.Color ParseColor(string text) => (System.Windows.Media.Color)ColorConverter.ConvertFromString(text);
			if (info.Kind == "Null")
				return null;
			if (double.IsNaN(info.Opacity) || info.Opacity < 0 || info.Opacity > 1)
				throw new ArgumentException("Opacity must be between 0 and 1.");
			Brush brush;
			if (info.Kind == "Solid")
				brush = new SolidColorBrush(ParseColor(info.Color));
			else
			{
				if (info.Kind is not ("LinearGradient" or "RadialGradient"))
					throw new ArgumentException("Unknown brush kind: " + info.Kind);
				if (info.Stops.Count == 0)
					throw new ArgumentException("A gradient needs at least one stop.");
				var mapping = Enum.Parse<BrushMappingMode>(info.MappingMode);
				var spread = Enum.Parse<GradientSpreadMethod>(info.SpreadMethod);
				var stops = new GradientStopCollection();
				foreach (var stop in info.Stops)
				{
					if (double.IsNaN(stop.Offset) || stop.Offset < 0 || stop.Offset > 1)
						throw new ArgumentException("A gradient stop offset must be between 0 and 1.");
					stops.Add(new GradientStop(ParseColor(stop.Color), stop.Offset));
				}
				GradientBrush gradient;
				if (info.Kind == "LinearGradient")
				{
					var linear = new LinearGradientBrush { GradientStops = stops };
					if (info.StartX != 0 || info.StartY != 0) linear.StartPoint = new Point(info.StartX, info.StartY);
					if (info.EndX != 1 || info.EndY != 1) linear.EndPoint = new Point(info.EndX, info.EndY);
					gradient = linear;
				}
				else
				{
					var radial = new RadialGradientBrush { GradientStops = stops };
					if (info.CenterX != 0.5 || info.CenterY != 0.5) radial.Center = new Point(info.CenterX, info.CenterY);
					if (info.OriginX != 0.5 || info.OriginY != 0.5) radial.GradientOrigin = new Point(info.OriginX, info.OriginY);
					if (info.RadiusX != 0.5) radial.RadiusX = info.RadiusX;
					if (info.RadiusY != 0.5) radial.RadiusY = info.RadiusY;
					gradient = radial;
				}
				if (mapping != BrushMappingMode.RelativeToBoundingBox) gradient.MappingMode = mapping;
				if (spread != GradientSpreadMethod.Pad) gradient.SpreadMethod = spread;
				brush = gradient;
			}
			if (info.Opacity != 1)
				brush.Opacity = info.Opacity;
			return brush;
		}

		static bool IsNumericType(Type type) =>
			type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
			type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
			type == typeof(float) || type == typeof(double) || type == typeof(decimal);

		/// <summary>Maps only values whose TypeConverter already gives us a lossless text round-trip
		/// to the common Properties-pad vocabulary. Nested design items deliberately remain
		/// <c>Unsupported</c>; presenting their <see cref="object.ToString"/> output as editable
		/// text would be a lossy write path.</summary>
		static string PropertyKind(Type type, bool isEnum) =>
			type == typeof(bool) ? "Boolean"
			: isEnum ? "Enum"
			: IsNumericType(type) ? "Number"
			: type == typeof(System.Windows.Media.Color) ? "Color"
			: typeof(Brush).IsAssignableFrom(type) ? "Brush"
			: type == typeof(Uri) ? "Uri"
			: type == typeof(Point) ? "Point"
			: type == typeof(Size) ? "Size"
			: type == typeof(Rect) ? "Rect"
			: type == typeof(Thickness) ? "Thickness"
			: "String";

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
			brushKeysByScope.Clear();
			colorKeysByScope.Clear();
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
				// The root carries the document's d:DesignWidth as an EXPLICIT Width (PageClone/
				// WindowClone copies it - see OverrideRootSizeForExpansion). Measuring with only
				// lastWidth available then clamps DesiredSize.Width down to lastWidth, because WPF
				// clamps DesiredSize to availableSize - and the Arrange below uses that clamped
				// width. The root's ActualWidth still reports the explicit design width, so the
				// bitmap comes out at the full size while the content was arranged narrower, and
				// everything laid out beyond the arrange slot is silently never painted.
				// WPFGallery's MainWindow is the reproducer: d:DesignWidth="1000" against a default
				// lastWidth of 800 arranged the window at 800, putting its caption buttons (x>=843)
				// outside the arranged area - they rendered as nothing at all.
				// This is NOT the harmful unconstrained-width probe the comment above warns about:
				// the width comes from the document's own design hint, not from letting content
				// measure itself against infinity.
				var effectiveWidth = double.IsNaN(root.Width) || root.Width <= 0
					? lastWidth
					: Math.Max(lastWidth, root.Width);
				root.Measure(new Size(effectiveWidth, double.PositiveInfinity));
				var natural = root.DesiredSize;
				var effectiveHeight = double.IsInfinity(natural.Height) || double.IsNaN(natural.Height)
					? lastHeight : Math.Max(lastHeight, natural.Height);
				root.Measure(new Size(effectiveWidth, effectiveHeight));
				var desired = root.DesiredSize;
				root.Arrange(new Rect(0, 0,
					desired.Width > 0 ? desired.Width : effectiveWidth,
					desired.Height > 0 ? desired.Height : effectiveHeight));
				root.UpdateLayout();
			}
			state.Tree = BuildNode(current.RootItem, current.RootItem, "", DesignerLayoutMode.Unknown);
			state.TrayComponents = BuildTrayComponents(state.Tree);
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

		static List<DesignerTrayItem> BuildTrayComponents(DesignerElementNode root)
		{
			var result = new List<DesignerTrayItem>();
			void Visit(DesignerElementNode node)
			{
				if (node.IsTrayComponent)
					result.Add(new DesignerTrayItem { Id = node.Id, Name = node.Name ?? "", Type = node.Type });
				foreach (var child in node.Children)
					Visit(child);
			}
			Visit(root);
			return result;
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

		DesignerElementNode BuildNode(DesignItem item, DesignItem root, string path, string layoutMode)
		{
			pathToItem[path] = item;
			var properties = BuildProperties(item, out var bindings);
			var node = new DesignerElementNode {
				Id = path,
				Name = string.IsNullOrEmpty(item.Name) ? null : item.Name,
				Type = item.ComponentType?.Name ?? "",
				Path = path,
				LayoutMode = layoutMode,
				HasContentCollection = item.ContentProperty?.IsCollection == true,
				ZIndex = path.Length == 0 ? null : item.View is UIElement visual ? Panel.GetZIndex(visual) : null,
				BaselineOffset = item.View is TextBlock text ? text.BaselineOffset : null,
				GridTracks = item.Component is Grid grid ? BuildGridTracks(grid) : null,
				LayoutInsets = BuildLayoutInsets(item),
				Bindings = bindings,
				Events = BuildEvents(item),
				Properties = properties
			};
			// The in-process Outline's eye and lock toggles, for every visual element but the root.
			if (path.Length > 0 && item.View is UIElement)
			{
				node.IsDesignTimeHidden = item.Properties.GetAttachedProperty(ICSharpCode.WpfDesign.XamlDom.DesignTimeProperties.IsHiddenProperty).ValueOnInstance is true;
				node.IsDesignTimeLocked = item.Properties.GetAttachedProperty(ICSharpCode.WpfDesign.XamlDom.DesignTimeProperties.IsLockedProperty).ValueOnInstance is true;
			}
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
						node.Children.Add(BuildNode(child, root, childPath,
							DesignerLayoutMode.InferFromContainerType(node.Type)));
						index++;
					}
				}
				else if (contentProperty.Value != null)
				{
					var childPath = path.Length == 0 ? "0" : path + ",0";
				node.Children.Add(BuildNode(contentProperty.Value, root, childPath,
					DesignerLayoutMode.InferFromContainerType(node.Type)));
				}
			}
			AppendAttachedContextMenu(item, root, path, node);
			AppendHeaderContent(item, root, path, node);
			return node;
		}

		/// <summary>Publishes only edge measurements whose container semantics WPF can state
		/// unambiguously.  Margin is useful as an editable design fact for Grid children; Canvas
		/// position is measured from the actual arranged rectangle so Right/Bottom authored forms
		/// remain intelligible without inventing a Left/Top source value.</summary>
		static DesignerLayoutInsets? BuildLayoutInsets(DesignItem item)
		{
			if (item.View is not FrameworkElement element || item.Parent?.Component is not FrameworkElement parent)
				return null;
			// The displayed Canvas edges are visual coordinates. A non-identity transform makes
			// TranslatePoint include that visual offset/rotation while Canvas.Left/Right still
			// describe pre-transform layout coordinates, so no source edit can faithfully map a
			// dragged displayed edge back to the attached property. Match the legacy handle rule:
			// suppress this affordance rather than performing a surprising jump.
			if (element.LayoutTransform.Value != Matrix.Identity || element.RenderTransform.Value != Matrix.Identity)
				return null;
			if (parent is Grid)
			{
				var margin = element.Margin;
				return new DesignerLayoutInsets {
					Kind = "Margin", Left = margin.Left, Top = margin.Top,
					Right = margin.Right, Bottom = margin.Bottom
				};
			}
			if (parent is Canvas canvas)
			{
				var point = element.TranslatePoint(new Point(0, 0), canvas);
				return new DesignerLayoutInsets {
					Kind = "CanvasPosition", Left = point.X, Top = point.Y,
					// Do not clamp overflow. A negative visual distance is a real, editable
					// measurement and is needed for a drag to round-trip an out-of-bounds child.
					Right = canvas.ActualWidth - point.X - element.ActualWidth,
					Bottom = canvas.ActualHeight - point.Y - element.ActualHeight
				};
			}
			return null;
		}

		static DesignerGridGuides BuildGridTracks(Grid grid)
			=> new DesignerGridGuides {
				Accepted = true,
				RowTracks = grid.RowDefinitions
					.Select(row => new DesignerGridTrackInfo { Offset = row.Offset, Size = row.ActualHeight, Unit = row.Height.GridUnitType.ToString(), Value = row.Height.Value })
					.ToList(),
				ColumnTracks = grid.ColumnDefinitions
					.Select(column => new DesignerGridTrackInfo { Offset = column.Offset, Size = column.ActualWidth, Unit = column.Width.GridUnitType.ToString(), Value = column.Width.Value })
					.ToList()
			};

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
			var contextMenuNode = BuildNode(contextMenu, root, contextMenuPath, DesignerLayoutMode.Unknown);
			// ContextMenu is detached from its owner's visual tree. It is therefore exposed through
			// the common component tray while retaining its existing tree/menu-editor representation.
			contextMenuNode.IsTrayComponent = true;
			node.Children.Add(contextMenuNode);
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
				LayoutMode = DesignerLayoutMode.Unknown,
			};
			headerGroupNode.Children.Add(BuildNode(header, root, headerPath,
				DesignerLayoutMode.Content));
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
			{
				if (!fallbackAnnounced)
				{
					fallbackAnnounced = true;
					Console.Error.WriteLine(portableGpuRenderingEnabled
						? "WpfDesign.SurfaceHost: GPU rendering already failed this session; emitting bounded software fallback frames."
						: "WpfDesign.SurfaceHost: GPU rendering disabled by OPENDEVELOP_WPF_GPU_RENDER; emitting bounded software fallback frames.");
				}
				return FallbackFrame(tree, (int)width, (int)height, stopwatch);
			}
			byte[] rgbaPixels;
			try
			{
				// Written BEFORE the compositor calls. Composition runs synchronously on this
				// (dispatcher) thread and can block on an unvalidated headless adapter, so this
				// line is the diagnostic that survives a hang: if it is the LAST line in
				// OpenDevelop's Output pad, GPU composition is what stalled - restart with
				// OPENDEVELOP_WPF_GPU_RENDER=0 to use the bounded software fallback instead.
				Console.Error.WriteLine($"WpfDesign.SurfaceHost: GPU composition starting ({width}x{height}); if this is the last Output-pad line, composition is blocking.");
				renderTarget ??= GpuCompositionTarget.CreateHeadless();
				var texture = new GpuTexture(renderTarget.Context, width, height,
					TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc,
					"WpfDesign.SurfaceHost render target");
				renderTarget.ReplayVisualSubtree(element, width, height);
				renderTarget.Render(width, height, width, height, 1f, texture.ViewPtr);
				Console.Error.WriteLine("WpfDesign.SurfaceHost: GPU composition submitted; awaiting readback (bounded to 2s).");
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
				Console.Error.WriteLine($"WpfDesign.SurfaceHost: GPU frame ready {width}x{height} in {stopwatch.ElapsedMilliseconds} ms.");
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
