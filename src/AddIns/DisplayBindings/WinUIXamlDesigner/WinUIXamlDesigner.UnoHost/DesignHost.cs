using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// StreamJsonRpc target of the out-of-process design host. Every method marshals
	/// onto the headless Uno dispatcher thread before touching the UI tree.
	/// </summary>
	public class DesignHost
	{
		readonly Func<string?> getXamlAssemblyPath;

		public DesignHost(Func<string?> getXamlAssemblyPath)
		{
			this.getXamlAssemblyPath = getXamlAssemblyPath;
		}

		FrameworkElement? root;

		public DesignCapabilities GetCapabilities()
			=> HeadlessDispatcher.Dispatch(() => BuildCapabilities());

		public DesignSnapshot LoadDesign(string xaml, double width, double height, double dpi)
			=> HeadlessDispatcher.DispatchAsync(() => LoadDesignAsync(new LoadDesignRequest { Xaml = xaml, Width = width, Height = height, Dpi = dpi })).GetAwaiter().GetResult();

		public DesignSnapshot Layout(double width, double height, double dpi)
			=> HeadlessDispatcher.DispatchAsync(() => LayoutAsync(new LayoutRequest { Width = width, Height = height, Dpi = dpi })).GetAwaiter().GetResult();

		public AppResourcesResult LoadAppResources(string xaml)
			=> HeadlessDispatcher.Dispatch(() => LoadAppResourcesCore(xaml));

		public HitTestResult HitTest(double x, double y)
			=> HeadlessDispatcher.Dispatch(() => HitTestCore(new HitTestRequest { X = x, Y = y }));

		public void Shutdown()
		{
			HeadlessDispatcher.RequestExit();
		}

		DesignCapabilities BuildCapabilities()
		{
			var asm = typeof(FrameworkElement).Assembly;
			return new DesignCapabilities
			{
				Runtime = "Uno.Skia",
				Version = asm.GetName().Version?.ToString() ?? "",
				Toolbox = BuildToolboxCatalog()
			};
		}

		/// <summary>
		/// Toolbox catalog generated from the *loaded* runtime assemblies (the project's
		/// own Uno, once the child runs under the project's deps) plus a design-time
		/// allowlist - the toolbox always matches the project's actual control set.
		/// </summary>
		List<ToolboxItemInfo> BuildToolboxCatalog()
		{
			var catalog = new List<ToolboxItemInfo>();
			foreach (var t in typeof(FrameworkElement).Assembly.GetTypes())
			{
				if (catalog.Count >= 200)
				{
					break;
				}
				if (TryCreateToolboxItem(t) is { } item)
				{
					catalog.Add(item);
				}
			}
			return catalog;
		}

		static ToolboxItemInfo? TryCreateToolboxItem(Type type)
		{
			var ns = type.Namespace ?? "";
			if (ns != "Microsoft.UI.Xaml.Controls")
			{
				return null;
			}
			if (!type.IsPublic || type.IsAbstract || type.IsGenericTypeDefinition)
			{
				return null;
			}
			if (!typeof(Control).IsAssignableFrom(type) && !typeof(ContentControl).IsAssignableFrom(type))
			{
				return null;
			}
			if (type.GetConstructor(Type.EmptyTypes) is null)
			{
				return null;
			}
			var name = type.Name;
			return new ToolboxItemInfo
			{
				Name = name,
				DisplayName = name,
				Category = "Common",
				XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
				Template = BuildDefaultTemplate(name)
			};
		}

		/// <summary>Matches the default content the host's editor gives inserted controls.</summary>
		static string BuildDefaultTemplate(string name)
		{
			var ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
			switch (name)
			{
				case "TextBlock":
				case "TextBox":
					return $"<{name} xmlns=\"{ns}\" Text=\"{name}\"/>";
				case "Button":
				case "CheckBox":
				case "HyperlinkButton":
				case "RadioButton":
				case "ToggleSwitch":
					return $"<{name} xmlns=\"{ns}\" Content=\"{name}\"/>";
				default:
					return $"<{name} xmlns=\"{ns}\"/>";
			}
		}

		async Task<DesignSnapshot> LoadDesignAsync(LoadDesignRequest request)
		{
			var snapshot = new DesignSnapshot();
			try
			{
				root = (FrameworkElement)Microsoft.UI.Xaml.Markup.XamlReader.Load(request.Xaml);
				return await FinishLayoutAsync(request.Width, request.Height, request.Dpi, snapshot);
			}
			catch (Exception e)
			{
				root = null;
				snapshot.Diagnostics.Add(new DesignDiagnostic { Message = e.GetBaseException().Message });
				return snapshot;
			}
		}

		async Task<DesignSnapshot> LayoutAsync(LayoutRequest request)
		{
			var snapshot = new DesignSnapshot();
			if (root is null)
			{
				snapshot.Diagnostics.Add(new DesignDiagnostic { Message = "No design loaded." });
				return snapshot;
			}
			return await FinishLayoutAsync(request.Width, request.Height, request.Dpi, snapshot);
		}

		async Task<DesignSnapshot> FinishLayoutAsync(double width, double height, double dpi, DesignSnapshot snapshot)
		{
			try
			{
				var size = new Size(width, height);
				root!.Measure(size);
				root.Arrange(new Rect(0, 0, width, height));
				snapshot.Tree = BuildTree(root, root);
				snapshot.Render = await RenderAsync(dpi);
			}
			catch (Exception e)
			{
				snapshot.Diagnostics.Add(new DesignDiagnostic { Message = e.GetBaseException().Message });
			}
			return snapshot;
		}

		/// <summary>
		/// Replaces the app-level resource dictionaries with the project's App.xaml content
		/// (rebuilt inline by the parent) so StaticResource/ThemeResource resolve against the
		/// real app. The Fluent XamlControlsResources stays in place below it, so lookups hit
		/// the app's own entries first. A load failure is reported and the design continues
		/// without the app resources.
		/// </summary>
		AppResourcesResult LoadAppResourcesCore(string xaml)
		{
			try
			{
				var dictionary = (ResourceDictionary)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
				Application.Current.Resources.MergedDictionaries.Insert(0, dictionary);
				return new AppResourcesResult { Success = true };
			}
			catch (Exception e)
			{
				return new AppResourcesResult { Success = false, Error = e.GetBaseException().Message };
			}
		}

		async Task<RenderResult> RenderAsync(double dpi)
		{
			var rtb = new RenderTargetBitmap();
			// The headless visual tree has no display, so its system scale is 1.0 and an
			// unscaled render would be soft on a Retina display. Uno's RenderTargetBitmap
			// rasterizes at renderSize * GetEffectiveRasterizationScale(), and that scale
			// honors RootScale._testOverrideScale - set it reflectively (same pattern as
			// HeadlessDispatcher) to get a crisp, native-resolution bitmap. The override
			// only feeds GetEffectiveRasterizationScale; it does not trigger ApplyScale,
			// so the root visual's own scale stays untouched (no double scaling).
			if (dpi > 0 && Math.Abs(dpi - 1.0) > 0.001 && TrySetRasterizationScale(root, dpi))
			{
				await rtb.RenderAsync(root);
			}
			else
			{
				var scaled = new Size(root!.RenderSize.Width * dpi, root.RenderSize.Height * dpi);
				await rtb.RenderAsync(root, (int)scaled.Width, (int)scaled.Height);
			}
			var pixels = await rtb.GetPixelsAsync();
			var buffer = new byte[pixels.Length];
			DataReader.FromBuffer(pixels).ReadBytes(buffer);
			return new RenderResult
			{
				Width = rtb.PixelWidth,
				Height = rtb.PixelHeight,
				Dpi = Math.Max(1.0, dpi),
				Data = Convert.ToBase64String(buffer)
			};
		}

		static bool TrySetRasterizationScale(FrameworkElement element, double dpi)
		{
			try
			{
				var xamlRoot = element.XamlRoot;
				if (xamlRoot is null)
				{
					return false;
				}
				var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
				var visualTree = xamlRoot.GetType().GetProperty("VisualTree", flags)?.GetValue(xamlRoot);
				var rootScale = visualTree?.GetType().GetProperty("RootScale", flags)?.GetValue(visualTree);
				var field = rootScale?.GetType().GetField("_testOverrideScale", flags);
				if (field is null)
				{
					return false;
				}
				field.SetValue(rootScale, (float)dpi);
				return true;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Walks the live visual tree and reports every element with its bounds in root
		/// coordinates. Template parts are included; the parent maps a pick back to the
		/// nearest named ancestor, matching the document namescope rule.
		/// </summary>
		static ElementNode BuildTree(DependencyObject node, UIElement root)
		{
			var nodeInfo = new ElementNode();
			if (node is FrameworkElement fe)
			{
				nodeInfo.Name = string.IsNullOrEmpty(fe.Name) ? null : fe.Name;
				nodeInfo.Type = fe.GetType().Name;
				var bounds = GetBoundsInRoot(fe, root);
				nodeInfo.X = bounds.X;
				nodeInfo.Y = bounds.Y;
				nodeInfo.Width = bounds.Width;
				nodeInfo.Height = bounds.Height;
			}
			else if (node is UIElement ue)
			{
				nodeInfo.Type = ue.GetType().Name;
			}

			var count = VisualTreeHelper.GetChildrenCount(node);
			for (var i = 0; i < count; i++)
			{
				var child = VisualTreeHelper.GetChild(node, i);
				if (child is UIElement)
				{
					nodeInfo.Children.Add(BuildTree(child, root));
				}
			}
			return nodeInfo;
		}

		/// <summary>
		/// An element's bounds in root coordinates, accumulated from each ancestor's layout
		/// slot plus that element's own margin (WPF arranges an element at
		/// slot + margin, so a margin'd child sits offset inside its slot). TransformToVisual
		/// cannot be used in this headless host: elements are not in a live tree, so Uno's
		/// GetTransform short-circuits to the identity matrix and every element reports its
		/// local slot position, disagreeing with the rendered pixels.
		/// </summary>
		static Rect GetBoundsInRoot(FrameworkElement element, UIElement root)
		{
			var x = 0.0;
			var y = 0.0;
			FrameworkElement current = element;
			while (current is not null && !ReferenceEquals(current, root))
			{
				var slot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(current);
				x += slot.X + current.Margin.Left;
				y += slot.Y + current.Margin.Top;
				current = VisualTreeHelper.GetParent(current) as FrameworkElement;
			}
			return new Rect(x, y, element.ActualWidth, element.ActualHeight);
		}

		HitTestResult HitTestCore(HitTestRequest request)
		{
			var result = new HitTestResult();
			if (root is null)
			{
				return result;
			}
			// Uno's FindElementsInHostCoordinates skips the whole subtree of any Panel without a
			// Background (IsViewHitImpl), which in a headless design would swallow every control -
			// so walk the tree ourselves and test point-in-bounds, exactly like BuildTree measures
			// bounds, and report hits innermost-first.
			var point = new Point(request.X, request.Y);
			var hits = new List<UIElement>();
			CollectHits(root, root, point, hits);
			foreach (var hit in hits)
			{
				var name = ResolveName(hit);
				if (name != null && !result.Chain.Contains(name))
				{
					result.Chain.Add(name);
				}
			}
			return result;
		}

		static void CollectHits(UIElement element, UIElement root, Point point, List<UIElement> hits)
		{
			var count = VisualTreeHelper.GetChildrenCount(element);
			for (var i = 0; i < count; i++)
			{
				if (VisualTreeHelper.GetChild(element, i) is UIElement child)
				{
					CollectHits(child, root, point, hits);
				}
			}
			if (element is FrameworkElement fe)
			{
				if (GetBoundsInRoot(fe, root).Contains(point))
				{
					hits.Add(element);
				}
			}
		}

		static string? ResolveName(DependencyObject element)
		{
			var current = element;
			while (current is not null)
			{
				if (current is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name))
				{
					return fe.Name;
				}
				current = VisualTreeHelper.GetParent(current);
			}
			return null;
		}
	}
}
