using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
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
	sealed class WpfSurfaceHostService
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

		public WpfSurfaceHostService(string expectedToken, WpfHeadlessDispatcher dispatcher)
		{
			this.expectedToken = expectedToken;
			this.dispatcher = dispatcher;
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
				var loadSettings = string.IsNullOrEmpty(snapshot.ProjectAssemblyPath) && snapshot.ReferencedAssemblyPaths.Count == 0
					? new XamlLoadSettings()
					: new XamlLoadSettings { TypeFinder = new SurfaceTypeFinder(snapshot.ProjectAssemblyPath, snapshot.ReferencedAssemblyPaths) };
				var appResources = ParseAppResources(snapshot, loadSettings);
				current = new XamlDesignContext(xmlReader, loadSettings);
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

		/// <summary>Confirmed by direct run (see wpf-designer.md's Phase 0 progress notes):
		/// VisualTreeHelper.HitTest never descends past the root visual under headless LibreWPF
		/// on macOS, even though children are real, arranged, correctly-bounded WPF objects
		/// (Grid.Children/ActualWidth/ActualHeight all correct). Every hit callback reports only
		/// the root - the same platform gap category as Render (see below), most likely because
		/// per-visual hit-test geometry also depends on the native compositor channel this
		/// headless host never establishes. Not fixed this round; see the technote for the
		/// deferred follow-up options.</summary>
		[JsonRpcMethod("design/hit-test")]
		public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, double x, double y)
			=> dispatcher.Dispatch(() => {
				var result = new DesignerHitTestResult();
				if (current?.RootItem?.View is not UIElement root)
					return result;
				DesignItem? hitItem = null;
				VisualTreeHelper.HitTest(root, null, hitResult => {
					if (hitResult.VisualHit is DependencyObject hit)
					{
						var component = current.Services.Component;
						var walked = hit;
						while (walked != null && hitItem == null)
						{
							if (walked is UIElement || walked is System.Windows.Media.Visual)
							{
								var item = component.GetDesignItem(walked);
								if (item != null)
								{
									hitItem = item;
									break;
								}
							}
							walked = VisualTreeHelper.GetParent(walked);
						}
					}
					return hitItem != null ? HitTestResultBehavior.Stop : HitTestResultBehavior.Continue;
				}, new PointHitTestParameters(new Point(x, y)));
				if (hitItem != null)
				{
					var path = pathToItem.FirstOrDefault(entry => entry.Value == hitItem).Key;
					result.PickPath = path ?? "";
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
				try
				{
					var created = CreateComponentTool.CreateItem(current, type);
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
					item.Properties["Width"].SetValue(width);
					item.Properties["Height"].SetValue(height);
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

		[JsonRpcMethod("ping")]
		public void Ping() { }

		[JsonRpcMethod("shutdown")]
		public void Shutdown() => shutdown.Set();

		public void WaitForShutdown() => shutdown.Wait();

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
				root.Measure(new Size(lastWidth, lastHeight));
				root.Arrange(new Rect(0, 0, lastWidth, lastHeight));
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
				Path = path
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
