using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>WPF-side boundary for an isolated WinUI/Uno designer runtime.</summary>
public sealed class WinUIXamlHost : ContentControl, IDisposable
{
	IWinUIXamlRuntimeHost runtime;

	public WinUIXamlHost(XamlFrameworkContext framework, string documentFileName)
	{
		Framework = framework ?? throw new ArgumentNullException(nameof(framework));
		HorizontalContentAlignment = HorizontalAlignment.Stretch;
		VerticalContentAlignment = VerticalAlignment.Stretch;
		runtime = WinUIXamlRuntimeHostRegistry.Create(framework, documentFileName);
		if (runtime != null) {
			runtime.StateChanged += OnRuntimeStateChanged;
			runtime.ElementPicked += OnRuntimeElementPicked;
		}
		Content = runtime?.WpfSurface ?? new TextBlock {
			Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, Text = StatusText
		};
		AllowDrop = true;
		DragOver += OnDragOver;
		Drop += OnDrop;
	}

	/// <summary>Raised after an asynchronous render settles, so the view can refresh status.</summary>
	public event EventHandler StateChanged;

	void OnRuntimeStateChanged(object sender, EventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);

	/// <summary>Raised with the x:Name of the element the user clicked on the design surface.</summary>
	public event EventHandler<string> ElementPicked;

	void OnRuntimeElementPicked(object sender, string name) => ElementPicked?.Invoke(this, name);

	public void SetSelectableNames(IReadOnlyList<string> names) =>
		runtime?.SetSelectableNames(names);

	public (double X, double Y, double Width, double Height)? QueryElementBounds(string name) =>
		runtime?.QueryElementBounds(name);

	public string ResolveNameAt(System.Numerics.Vector2 point) => runtime?.ResolveNameAt(point);

	public int ResolvedNameCount => runtime?.ResolvedNameCount ?? 0;
	public string LastPickDiagnostic => runtime?.LastPickDiagnostic ?? "no runtime";

	/// <summary>Translates surface-local element bounds into screen coordinates for pointer input.</summary>
	public Rect? QueryElementScreenBounds(string name)
	{
		var bounds = QueryElementBounds(name);
		if (bounds == null || !IsVisible)
			return null;
		var origin = PointToScreen(new Point(bounds.Value.X, bounds.Value.Y));
		return new Rect(origin.X, origin.Y, bounds.Value.Width, bounds.Value.Height);
	}

	/// <summary>
	/// Raised when a Toolbox item is dropped on the design surface, with the control to create and
	/// the x:Name of the container it was dropped into (null for the root). Resolving the container
	/// here - from the real drop point - is what makes a drag land where the user aimed.
	/// </summary>
	public event EventHandler<(string ControlName, string ContainerName)> ControlDropped;

	void OnDragOver(object sender, DragEventArgs e)
	{
		e.Effects = e.Data.GetDataPresent(WinUIXamlToolbox.DragDataFormat)
			? DragDropEffects.Copy
			: DragDropEffects.None;
		e.Handled = true;
	}

	void OnDrop(object sender, DragEventArgs e)
	{
		if (!e.Data.GetDataPresent(WinUIXamlToolbox.DragDataFormat))
			return;
		var controlName = e.Data.GetData(WinUIXamlToolbox.DragDataFormat) as string;
		if (string.IsNullOrEmpty(controlName))
			return;
		var point = e.GetPosition(this);
		var container = ResolveNameAt(new System.Numerics.Vector2((float)point.X, (float)point.Y));
		ControlDropped?.Invoke(this, (controlName, container));
		e.Handled = true;
	}

	public XamlFrameworkContext Framework { get; }
	public bool HasRenderedPreview => runtime?.HasRenderedPreview == true;
	public string StatusText => runtime?.StatusText ??
		"WinUI/Uno runtime host is not installed. The WPF XamlReader compatibility renderer is disabled.";
	public void LoadXaml(string text) => runtime?.LoadXaml(text ?? string.Empty);

	public void Dispose()
	{
		DragOver -= OnDragOver;
		Drop -= OnDrop;
		if (runtime != null) {
			runtime.StateChanged -= OnRuntimeStateChanged;
			runtime.ElementPicked -= OnRuntimeElementPicked;
		}
		runtime?.Dispose();
		runtime = null;
		Content = null;
	}
}

/// <summary>
/// Implemented by the independent XAML Studio/Uno runtime assembly. Microsoft.UI.Xaml objects
/// never cross this interface; only the WPF hosting surface does.
/// </summary>
public interface IWinUIXamlRuntimeHost : IDisposable
{
	UIElement WpfSurface { get; }
	bool HasRenderedPreview { get; }
	string StatusText { get; }
	/// <summary>Raised once an asynchronous <see cref="LoadXaml"/> has settled.</summary>
	event EventHandler StateChanged;
	void LoadXaml(string text);

	/// <summary>
	/// The x:Names the document defines. The runtime resolves them against the rendered tree's
	/// namescope so a click can be reported back as a name.
	/// </summary>
	void SetSelectableNames(IReadOnlyList<string> names);

	/// <summary>Surface-local bounds of a rendered element, as plain numbers.</summary>
	(double X, double Y, double Width, double Height)? QueryElementBounds(string name);

	/// <summary>Diagnostics for why a design-surface click did or did not resolve to an element.</summary>
	int ResolvedNameCount { get; }
	string LastPickDiagnostic { get; }

	/// <summary>x:Name of the nearest source-backed element at a surface-local point, or null.</summary>
	string ResolveNameAt(System.Numerics.Vector2 point);

	/// <summary>
	/// Raised when the user clicks an element on the design surface, carrying that element's
	/// x:Name. Only the name crosses this boundary - never a <c>Microsoft.UI.Xaml</c> object -
	/// so the shell side keeps resolving selection against the XAML source document.
	/// </summary>
	event EventHandler<string> ElementPicked;
}

public static class WinUIXamlRuntimeHostRegistry
{
	static Func<XamlFrameworkContext, string, IWinUIXamlRuntimeHost> factory;

	public static void Register(Func<XamlFrameworkContext, string, IWinUIXamlRuntimeHost> runtimeFactory) =>
		factory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));

	internal static IWinUIXamlRuntimeHost Create(XamlFrameworkContext framework, string documentFileName) =>
		factory?.Invoke(framework, documentFileName);
}
