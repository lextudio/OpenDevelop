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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;

using ICSharpCode.WpfDesign;
using ICSharpCode.WpfDesign.Designer.Services;
using ICSharpCode.WpfDesign.Designer.Xaml;
using ICSharpCode.SharpDevelop.Designer.Remote;

using StreamJsonRpc;

using ProGPU.Backend;
using Silk.NET.WebGPU;
using GpuCompositionTarget = System.Windows.Media.ProGPU.ProGpuWpfCompositionTarget;

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
		double lastWidth = 800;
		double lastHeight = 600;
		/// <summary>Created once, lazily, on first successful render and reused for the process's
		/// life - matches every LibreWPF ProGPU test/harness, which all construct one
		/// CreateHeadless() target and reuse it across frames rather than recreating the GPU
		/// context per render.</summary>
		GpuCompositionTarget? renderTarget;
		bool renderUnavailable;

		// Design-time theme resolution - resolved once per session/open from the project
		// assembly, see ResolveThemes. Theme name (as shown in the designer's combo) to
		// theme source; any number of themes, with no light/dark semantics attached to the
		// names (the theme IS whatever the dictionary paints).
		Assembly? projectAssembly;
		Dictionary<string, string>? themeSources;
		ResourceDictionary? appliedThemeDictionary;

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
			if (!CryptographicOperations.FixedTimeEquals(
				Convert.FromHexString(expectedToken), Convert.FromHexString(token)))
				throw new UnauthorizedAccessException("Invalid designer-host token.");
			if (protocolVersion != DesignerProtocol.Version)
				throw new NotSupportedException($"Protocol {protocolVersion} is not supported.");
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
				using var stringReader = new StringReader(xaml);
				using var xmlReader = XmlReader.Create(stringReader);
				// Phase 1 slice (see wpf-designer.md's Phase 1 progress notes): any target
				// assembly - the project's own output OR a resolved reference (a referenced
				// control library / NuGet package) - means type resolution must happen here in
				// the child, never in OpenDevelop. Checking ReferencedAssemblyPaths too is
				// load-bearing: a document using only referenced-library controls has no project
				// assembly at all, and testing ProjectAssemblyPath alone silently ignored its
				// references. Stock-only documents keep the Phase 0 default untouched.
				var typeFinder = string.IsNullOrEmpty(snapshot.ProjectAssemblyPath) && snapshot.ReferencedAssemblyPaths.Count == 0
					? null
					: new SurfaceTypeFinder(snapshot.ProjectAssemblyPath, snapshot.ReferencedAssemblyPaths);
				var loadSettings = typeFinder == null ? new XamlLoadSettings() : new XamlLoadSettings { TypeFinder = typeFinder };
				ResolveThemes(typeFinder?.ProjectAssembly);
				state.SupportsThemeSwitch = themeSources != null;
				state.DesignThemes = themeSources?.Keys.ToArray() ?? Array.Empty<string>();
				var appResources = ParseAppResources(snapshot, loadSettings);
				current = new XamlDesignContext(xmlReader, loadSettings);
				// A fresh document parse means a fresh root FrameworkElement - the previous root's
				// MergedDictionaries (and whatever theme dictionary this field used to point at)
				// no longer exist, so re-applying design/theme (if the IDE asks again) must start
				// from a clean slate rather than trying to remove a dictionary from the new root
				// that was never actually added to it.
				appliedThemeDictionary = null;
				// Merged after parse but before RebuildTreeAndRender runs layout, which is when
				// implicit styles get applied - the headless stand-in for the live designer's
				// DesignPanel.Resources (see ParseAppResources' remarks).
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

		/// <summary>Parses an app-level resource dictionary out of the snapshot's "AppXaml" file and
		/// merges it into <see cref="Application.Current"/>'s resources, so the document's
		/// StaticResource lookups can fall through to app-level resources during parse.
		///
		/// Mirrors the live in-process designer's proven approach (WpfViewContent.LoadInternal's
		/// EnableAppXamlParsing block): pull out the &lt;Application.Resources&gt; node, copy the
		/// root element's xmlns declarations onto each of its children (the inner XML is reparsed
		/// standalone and would otherwise lose them), and parse it through a
		/// <see cref="XamlDesignContext"/> rather than a runtime XamlReader, taking
		/// RootItem.Component as the dictionary. A bare &lt;ResourceDictionary&gt; document is
		/// accepted too. Still deliberately narrow: no StartupUri, no code-behind, no theme or
		/// merged-dictionary URI resolution.
		///
		/// The live designer merges into DesignPanel.Resources - the design surface's visual
		/// ancestor - so resource lookup from the document walks up into it. This headless child
		/// has no DesignPanel, and the document's own root element is the top of the tree, so the
		/// dictionary is merged into that root's Resources instead (by <see cref="OpenCore"/>,
		/// after parse but before layout, which is when implicit styles are applied). Merging into
		/// Application.Current.Resources was tried first and did not work - confirmed by a real
		/// run, see wpf-designer.md's Phase 1 progress notes.</summary>
		ResourceDictionary? ParseAppResources(DesignerDocumentSnapshot snapshot, XamlLoadSettings loadSettings)
		{
			var appFile = snapshot.Files.FirstOrDefault(item => item.Kind == "AppXaml");
			if (appFile == null || string.IsNullOrEmpty(appFile.Text))
				return null;

			var document = new XmlDocument();
			document.LoadXml(appFile.Text);
			var root = document.DocumentElement;
			if (root == null)
				return null;

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
					return null;
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
				return null;

			using var stringReader = new StringReader(dictionaryXml);
			using var xmlReader = XmlReader.Create(stringReader);
			var appContext = new XamlDesignContext(xmlReader, loadSettings);
			return appContext.RootItem?.Component as ResourceDictionary;
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

				if (renderTarget != null &&
					renderTarget.TryHitTestOwner(new System.Numerics.Vector2((float)x, (float)y), out var owner, out _) &&
					owner is DependencyObject ownerVisual)
				{
					hitItem = ResolveOwner(ownerVisual);
				}
				else
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

		internal void Close()
		{
			dispatcher.Dispatch(() => {
				current = null;
				pathToItem.Clear();
				appliedThemeDictionary = null;
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
				root.Measure(new Size(lastWidth, lastHeight));
				var desired = root.DesiredSize;
				root.Arrange(new Rect(0, 0,
					desired.Width > 0 ? desired.Width : lastWidth,
					desired.Height > 0 ? desired.Height : lastHeight));
				root.UpdateLayout();
			}
			state.Tree = BuildNode(current.RootItem, current.RootItem, "");
			state.Render = Render(current.RootItem.View as FrameworkElement);
			state.ComponentCount = pathToItem.Count;
		}

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
				try
				{
					var offset = element.TransformToAncestor(rootVisual).Transform(new Point(0, 0));
					node.X = offset.X;
					node.Y = offset.Y;
				}
				catch (InvalidOperationException)
				{
					// Not connected to the root's visual tree (shouldn't happen post-layout, but
					// don't let a geometry edge case fail the whole tree build).
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
			return node;
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
		unsafe DesignerRenderFrame? Render(FrameworkElement? element)
		{
			if (element == null || element.ActualWidth <= 0 || element.ActualHeight <= 0 || renderUnavailable)
				return null;
			var stopwatch = Stopwatch.StartNew();
			var width = (uint)Math.Ceiling(element.ActualWidth);
			var height = (uint)Math.Ceiling(element.ActualHeight);
			byte[] rgbaPixels;
			try
			{
				renderTarget ??= GpuCompositionTarget.CreateHeadless();
				using var texture = new GpuTexture(renderTarget.Context, width, height,
					TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc,
					"WpfDesign.SurfaceHost render target");
				renderTarget.ReplayVisualSubtree(element, width, height);
				renderTarget.Render(width, height, width, height, 1f, texture.ViewPtr);
				rgbaPixels = texture.ReadPixels();
			}
			catch (Exception e)
			{
				// Not narrowed to DllNotFoundException like the old RenderTargetBitmap catch:
				// this is a different native backend (WebGPU/GPU driver) with its own unknown
				// failure shapes on a machine without one - fail soft the same way regardless.
				renderUnavailable = true;
				Console.Error.WriteLine("WpfDesign.SurfaceHost: ProGPU render unavailable, disabling rendering for this process: " + e);
				return null;
			}
			// ReadPixels returns RGBA byte order (Rgba8Unorm); DesignerRenderFrame.Data's
			// established wire shape (matching WinUI/Uno) is BGRA - swap R/B in place rather than
			// widen the protocol, since this is purely an encoding detail of this one backend.
			for (var i = 0; i + 2 < rgbaPixels.Length; i += 4)
				(rgbaPixels[i], rgbaPixels[i + 2]) = (rgbaPixels[i + 2], rgbaPixels[i]);
			using var stream = new MemoryStream();
			using (var deflate = new DeflateStream(stream, CompressionLevel.Fastest, leaveOpen: true))
				deflate.Write(rgbaPixels, 0, rgbaPixels.Length);
			stream.Flush();
			stopwatch.Stop();
			return new DesignerRenderFrame {
				Sequence = ++frameSequence,
				Width = (int)width,
				Height = (int)height,
				Dpi = 1,
				Data = Convert.ToBase64String(stream.ToArray()),
				RenderMs = stopwatch.Elapsed.TotalMilliseconds
			};
		}

		long frameSequence;
	}
}
