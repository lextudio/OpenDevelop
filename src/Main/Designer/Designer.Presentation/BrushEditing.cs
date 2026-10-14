using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>A Properties-pad descriptor for a host-published <c>Brush</c> editor row.</summary>
public interface IBrushProperty
{
	DesignerBrushInfo? Brush { get; }
	IReadOnlyList<string> BrushResourceKeys { get; }
}

/// <summary>The Properties pad's selected object, which commits an edited brush.</summary>
public interface IBrushEditorHost
{
	void SetBrush(string propertyName, DesignerBrushInfo brush);
}

/// <summary>Optionally implemented by a brush editor host: the designer's last rendered frame,
/// which the brush editor's eyedropper samples (the design, not the screen, so it needs no OS
/// screen-capture permission and picks exactly what the user is designing).</summary>
public interface IDesignFrameSource
{
	BitmapSource? DesignFrame { get; }
}

/// <summary>Optionally implemented by a brush editor host: the project images, relative to the
/// designed document, that the brush editor's image tab offers.</summary>
public interface IImageSourceCandidates
{
	IReadOnlyList<string> ImageSourceCandidates { get; }
}

/// <summary>Reads one pixel of a design frame as an unpremultiplied color.</summary>
public static class BrushEyedropper
{
	/// <summary>The color at fractional position (<paramref name="u"/>, <paramref name="v"/>) in
	/// 0-1 of <paramref name="frame"/>, or null for an unsupported pixel format or a point outside.</summary>
	public static Color? Sample(BitmapSource frame, double u, double v)
	{
		if (frame == null || !(u >= 0 && u <= 1 && v >= 0 && v <= 1)
			|| frame.Format != PixelFormats.Pbgra32 && frame.Format != PixelFormats.Bgra32)
			return null;
		var x = Math.Min(frame.PixelWidth - 1, (int)(u * frame.PixelWidth));
		var y = Math.Min(frame.PixelHeight - 1, (int)(v * frame.PixelHeight));
		var pixel = new byte[4];
		frame.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
		byte b = pixel[0], g = pixel[1], r = pixel[2], a = pixel[3];
		if (frame.Format == PixelFormats.Pbgra32 && a is > 0 and < 255) {
			byte Unpremultiply(byte c) => (byte)Math.Min(255, (int)Math.Round(c * 255.0 / a));
			(r, g, b) = (Unpremultiply(r), Unpremultiply(g), Unpremultiply(b));
		}
		return Color.FromArgb(a, r, g, b);
	}
}

/// <summary>
/// The shared flow behind a <c>Brush</c> Properties-pad row: open <see cref="BrushEditorDialog"/>
/// (or a test edit) over the row's brush and commit only an accepted, changed brush.
/// </summary>
public static class BrushEditing
{
	// DevFlow supplies an edit script instead of the modal dialog, as for the items editor.
	public static Func<BrushEditModel, bool>? TestEdit { get; set; }
	public static Exception? TestException { get; set; }

	public sealed record ScriptedEditResult(bool Success, string? Error, DesignerBrushInfo? Before, DesignerBrushInfo? After, bool Committed);

	/// <summary>
	/// Applies an edit script to a model, as a user would in the dialog: ';'-separated
	/// <c>kind:&lt;Null|Solid|LinearGradient|RadialGradient&gt;</c>, <c>color:&lt;color&gt;</c> (the
	/// solid color or selected stop), <c>add:&lt;offset&gt;</c>, <c>select:&lt;i&gt;</c>,
	/// <c>offset:&lt;i&gt;:&lt;offset&gt;</c>, <c>remove:&lt;i&gt;</c>, <c>reverse</c>,
	/// <c>opacity:&lt;0-1&gt;</c>, <c>start:x,y</c>, <c>end:x,y</c>, <c>center:x,y</c>,
	/// <c>origin:x,y</c>, <c>radius:x,y</c>, <c>mapping:&lt;mode&gt;</c>, <c>spread:&lt;method&gt;</c>,
	/// <c>resource:&lt;key&gt;</c>,
	/// or <c>cancel</c>. Returns false for cancel; throws for an unknown or rejected step.
	/// </summary>
	public static bool ApplyScript(BrushEditModel model, string script)
	{
		static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
		static (double, double) Pair(string text)
		{
			var parts = text.Split(',');
			return parts.Length == 2 ? (Number(parts[0]), Number(parts[1])) : throw new ArgumentException("Expected x,y: " + text);
		}
		foreach (var step in (script ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = step.Split(':', 3);
			var argument = parts.Length > 1 ? parts[1] : "";
			var ok = true;
			switch (parts[0]) {
				case "cancel":
					return false;
				case "kind": model.SetKind(argument); break;
				case "resource": ok = model.SelectResource(argument); break;
				case "system": ok = model.SelectSystemResource(argument); break;
				case "image": ok = model.SetImage(step.Substring("image:".Length)); break;
				case "stretch": ok = model.SetStretch(argument); break;
				case "color":
					ok = BrushEditModel.TryParseColor(step.Substring("color:".Length), out var color);
					if (ok) model.SelectedColor = color;
					break;
				case "add": model.AddStop(Number(argument)); break;
				case "select": ok = model.SelectStop(int.Parse(argument, CultureInfo.InvariantCulture)); break;
				case "offset": ok = parts.Length > 2 && model.SetStopOffset(int.Parse(argument, CultureInfo.InvariantCulture), Number(parts[2])); break;
				case "remove": ok = model.RemoveStop(int.Parse(argument, CultureInfo.InvariantCulture)); break;
				case "reverse": model.Reverse(); break;
				case "opacity": model.Opacity = Number(argument); break;
				case "start": (model.StartX, model.StartY) = Pair(argument); break;
				case "end": (model.EndX, model.EndY) = Pair(argument); break;
				case "center": (model.CenterX, model.CenterY) = Pair(argument); break;
				case "origin": (model.OriginX, model.OriginY) = Pair(argument); break;
				case "radius": (model.RadiusX, model.RadiusY) = Pair(argument); break;
				case "mapping": model.MappingMode = argument; break;
				case "spread": model.SpreadMethod = argument; break;
				default: throw new ArgumentException("Unknown brush edit step: " + step);
			}
			if (!ok)
				throw new ArgumentException("Brush edit step was rejected: " + step);
		}
		return true;
	}

	/// <summary>Clicks a rendered brush "…" button with <paramref name="script"/> applied in place
	/// of the modal dialog, exercising the real button handler and commit path (DevFlow seam).</summary>
	public static ScriptedEditResult RunScriptedEdit(ButtonBase button, string script)
	{
		DesignerBrushInfo? before = null;
		DesignerBrushInfo? after = null;
		var committed = false;
		TestException = null;
		TestEdit = model => {
			before = model.ToInfo();
			if (!ApplyScript(model, script))
				return false;
			after = model.ToInfo();
			committed = model.IsChanged;
			return true;
		};
		try {
			button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
		} finally {
			TestEdit = null;
		}
		return TestException != null
			? new ScriptedEditResult(false, TestException.Message, before, after, false)
			: new ScriptedEditResult(true, null, before, after, committed);
	}

	/// <summary>Runs the editor for <paramref name="descriptor"/> on <paramref name="instance"/>
	/// (the row's own selected object). Failures go to <paramref name="reportError"/>, or to
	/// <see cref="TestException"/> while a test edit is active.</summary>
	public static void Edit(object? instance, PropertyDescriptor? descriptor, Window? owner, Action<Exception> reportError)
	{
		try {
			if (instance is not IBrushEditorHost host || descriptor is not IBrushProperty property)
				throw new InvalidOperationException("The brush editor is not hosted by a designer Properties pad.");
			var model = new BrushEditModel(property.Brush, property.BrushResourceKeys, (instance as IImageSourceCandidates)?.ImageSourceCandidates);
			var accepted = TestEdit != null
				? TestEdit(model)
				: new BrushEditorDialog(model, descriptor.DisplayName) { Owner = owner, DesignFrame = (instance as IDesignFrameSource)?.DesignFrame }.ShowDialog() == true;
			if (accepted && model.IsChanged)
				host.SetBrush(descriptor.Name, model.ToInfo());
		} catch (Exception exception) {
			if (TestEdit != null) {
				TestException = exception;
				return;
			}
			reportError(exception);
		}
	}
}

/// <summary>
/// The brush editor, laid out like Visual Studio's: brush-kind tabs (No brush, Solid, Gradient),
/// a gradient slider with stops (click to add, drag to move, drag off to remove) plus
/// linear/radial, reverse and selected-offset controls, one color editor (saturation/value field,
/// hue slider, R/G/B/A, hex, initial/current chips) for the solid color or the selected stop, and
/// an advanced section (opacity, geometry, mapping, spread).
/// </summary>
public sealed class BrushEditorDialog : Window
{
	const double SvSize = 180;
	const double SliderHeight = 22;
	const double StopWidth = 10;

	readonly BrushEditModel model;
	readonly Color initialColor;
	readonly ToggleButton noBrushTab = new() { Content = "No brush", Padding = new Thickness(8, 2, 8, 2) };
	readonly ToggleButton solidTab = new() { Content = "Solid", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
	readonly ToggleButton gradientTab = new() { Content = "Gradient", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
	readonly ToggleButton resourcesTab = new() { Content = "Resources", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
	readonly ListBox resources = new() { Height = 160 };
	readonly ToggleButton imageTab = new() { Content = "Image", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
	readonly ComboBox imageSource = new() { IsEditable = true, Margin = new Thickness(0, 0, 0, 4) };
	readonly ComboBox imageStretch = new();
	readonly StackPanel imagePanel = new();
	bool showImage;
	bool showResources;
	readonly RadioButton linear = new() { Content = "Linear", GroupName = "gradientKind", Margin = new Thickness(0, 0, 8, 0) };
	readonly RadioButton radial = new() { Content = "Radial", GroupName = "gradientKind", Margin = new Thickness(0, 0, 8, 0) };
	readonly Border gradientBar = new() { Height = SliderHeight, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
	readonly Canvas stopLayer = new() { Height = 14, Background = Brushes.Transparent };
	readonly TextBox offsetBox = new() { Width = 50 };
	readonly Rectangle svField = new() { Width = SvSize, Height = SvSize };
	readonly Ellipse svMarker = new() { Width = 10, Height = 10, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
	readonly Slider hue = new() { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 360, Height = SvSize, IsDirectionReversed = true, Margin = new Thickness(6, 0, 0, 0) };
	readonly TextBox red = new() { Width = 44 }, green = new() { Width = 44 }, blue = new() { Width = 44 }, alpha = new() { Width = 44 };
	readonly TextBox hex = new() { Width = 90 };
	readonly Border initialChip = new() { Width = 30, Height = 20, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
	readonly Border currentChip = new() { Width = 30, Height = 20, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
	readonly TextBox opacity = new() { Width = 60 };
	readonly TextBox start = new() { Width = 80 }, end = new() { Width = 80 }, center = new() { Width = 80 }, origin = new() { Width = 80 }, radius = new() { Width = 80 };
	readonly ComboBox mapping = new() { Width = 160, ItemsSource = new[] { "RelativeToBoundingBox", "Absolute" } };
	readonly ComboBox spread = new() { Width = 160, ItemsSource = new[] { "Pad", "Reflect", "Repeat" } };
	readonly Border preview = new() { Height = 36, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0) };
	readonly FrameworkElement gradientPanel, colorPanel, advancedPanel;
	readonly Grid linearRows, radialRows;
	double currentHue;
	bool syncing;
	int draggedStop = -1;

	readonly ToggleButton eyedropperToggle = new() { Content = "Pick from design", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 2, 8, 2) };
	readonly Image eyedropperFrame = new() { Stretch = Stretch.Fill, Cursor = Cursors.Cross, Margin = new Thickness(0, 4, 0, 0) };
	BitmapSource? designFrame;

	/// <summary>The design frame the eyedropper samples; without one there is no eyedropper.</summary>
	public BitmapSource? DesignFrame
	{
		get => designFrame;
		set {
			designFrame = value;
			eyedropperFrame.Source = value;
			// Keep the frame's aspect ratio at the dialog's width, so a click maps linearly to a pixel.
			eyedropperFrame.Width = 300;
			eyedropperFrame.Height = value == null || value.PixelWidth == 0 ? 0 : 300.0 * value.PixelHeight / value.PixelWidth;
			Refresh();
		}
	}

	public BrushEditorDialog(BrushEditModel model, string propertyName)
	{
		this.model = model;
		initialColor = model.SelectedColor;
		currentHue = ToHsv(initialColor).Hue;
		Title = "Edit " + propertyName;
		SizeToContent = SizeToContent.WidthAndHeight;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		ResizeMode = ResizeMode.NoResize;

		var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
		tabs.Children.Add(noBrushTab);
		tabs.Children.Add(solidTab);
		tabs.Children.Add(gradientTab);
		tabs.Children.Add(resourcesTab);
		tabs.Children.Add(imageTab);
		imagePanel.Children.Add(new TextBlock { Text = "Image source", Margin = new Thickness(0, 0, 0, 2) });
		imagePanel.Children.Add(imageSource);
		imagePanel.Children.Add(new TextBlock { Text = "Stretch", Margin = new Thickness(0, 0, 0, 2) });
		imagePanel.Children.Add(imageStretch);
		imageSource.ItemsSource = model.ImageSources;
		imageStretch.ItemsSource = BrushEditModel.StretchValues;
		imageTab.Click += (_, _) => { showImage = true; showResources = false; Refresh(); };
		imageSource.SelectionChanged += (_, _) => { if (!syncing && imageSource.SelectedItem is string picked) Change(() => model.SetImage(picked)); };
		imageSource.LostKeyboardFocus += (_, _) => { if (!syncing && !string.IsNullOrWhiteSpace(imageSource.Text) && imageSource.Text != model.ImageSource) Change(() => model.SetImage(imageSource.Text)); };
		imageStretch.SelectionChanged += (_, _) => { if (!syncing && imageStretch.SelectedItem is string stretch) Change(() => model.SetStretch(stretch)); };
		showImage = model.Kind == BrushEditModel.ImageKind;
		resourcesTab.Click += (_, _) => { showResources = true; showImage = false; Refresh(); };
		resources.ItemsSource = model.ResourceEntries;
		resources.SelectionChanged += (_, _) => { if (!syncing && resources.SelectedItem is string entry) Change(() => model.SelectResourceEntry(entry)); };
		noBrushTab.Click += (_, _) => { showResources = false; showImage = false; Change(() => model.SetKind(BrushEditModel.NullKind)); };
		solidTab.Click += (_, _) => { showResources = false; showImage = false; Change(() => model.SetKind(BrushEditModel.SolidKind)); };
		gradientTab.Click += (_, _) => { showResources = false; showImage = false; Change(() => model.SetKind(model.Kind == BrushEditModel.RadialKind ? BrushEditModel.RadialKind : BrushEditModel.LinearKind)); };
		showResources = model.Kind is BrushEditModel.ResourceKind or BrushEditModel.SystemKind;

		gradientPanel = BuildGradientPanel();
		colorPanel = BuildColorPanel();
		(advancedPanel, linearRows, radialRows) = BuildAdvancedPanel();

		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 6, 0) };
		ok.Click += (_, _) => DialogResult = true;
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
		var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
		footer.Children.Add(ok);
		footer.Children.Add(cancel);

		var root = new StackPanel { Margin = new Thickness(10), Width = 300 };
		root.Children.Add(tabs);
		root.Children.Add(resources);
		root.Children.Add(imagePanel);
		root.Children.Add(gradientPanel);
		root.Children.Add(colorPanel);
		root.Children.Add(eyedropperToggle);
		root.Children.Add(eyedropperFrame);
		eyedropperToggle.Click += (_, _) => Refresh();
		eyedropperFrame.MouseLeftButtonDown += (_, e) => {
			if (DesignFrame == null || eyedropperFrame.ActualWidth <= 0 || eyedropperFrame.ActualHeight <= 0)
				return;
			var point = e.GetPosition(eyedropperFrame);
			if (BrushEyedropper.Sample(DesignFrame, point.X / eyedropperFrame.ActualWidth, point.Y / eyedropperFrame.ActualHeight) is { } picked)
				Change(() => model.SelectedColor = picked);
			e.Handled = true;
		};
		root.Children.Add(advancedPanel);
		root.Children.Add(preview);
		root.Children.Add(footer);
		Content = root;
		SetResourceReference(BackgroundProperty, "ToolWindowBackground");
		Refresh();
	}

	FrameworkElement BuildGradientPanel()
	{
		linear.Checked += (_, _) => { if (!syncing) Change(() => model.SetKind(BrushEditModel.LinearKind)); };
		radial.Checked += (_, _) => { if (!syncing) Change(() => model.SetKind(BrushEditModel.RadialKind)); };
		var reverse = new Button { Content = "Reverse", Padding = new Thickness(6, 0, 6, 0), ToolTip = "Reverse gradient stops" };
		reverse.Click += (_, _) => Change(model.Reverse);
		var previous = new Button { Content = "◀", Width = 22, ToolTip = "Select previous gradient stop", Margin = new Thickness(8, 0, 0, 0) };
		previous.Click += (_, _) => Change(() => model.SelectPreviousStop());
		var next = new Button { Content = "▶", Width = 22, ToolTip = "Select next gradient stop" };
		next.Click += (_, _) => Change(() => model.SelectNextStop());
		var kinds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
		kinds.Children.Add(linear);
		kinds.Children.Add(radial);
		kinds.Children.Add(reverse);
		kinds.Children.Add(previous);
		kinds.Children.Add(next);

		// Clicking the bar adds a stop there; the thumbs below it move or, dragged down off the
		// slider, remove a stop.
		gradientBar.MouseLeftButtonDown += (_, e) => Change(() => model.AddStop(e.GetPosition(gradientBar).X / Math.Max(1, gradientBar.ActualWidth)));
		gradientBar.SizeChanged += (_, _) => LayoutStops();
		stopLayer.MouseMove += (_, e) => {
			if (draggedStop < 0 || e.LeftButton != MouseButtonState.Pressed)
				return;
			var position = e.GetPosition(gradientBar);
			Change(() => model.SetStopOffset(draggedStop, position.X / Math.Max(1, gradientBar.ActualWidth)));
			draggedStop = model.SelectedStop;
		};
		stopLayer.MouseLeftButtonUp += (_, e) => {
			if (draggedStop >= 0 && e.GetPosition(stopLayer).Y > stopLayer.ActualHeight + 20)
				Change(() => model.RemoveStop(draggedStop));
			draggedStop = -1;
			stopLayer.ReleaseMouseCapture();
		};

		offsetBox.LostFocus += (_, _) => CommitOffset();
		offsetBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitOffset(); };
		var offsetRow = Row("Selected stop offset", offsetBox);

		var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
		panel.Children.Add(kinds);
		panel.Children.Add(gradientBar);
		panel.Children.Add(stopLayer);
		panel.Children.Add(offsetRow);
		return panel;
	}

	FrameworkElement BuildColorPanel()
	{
		// Saturation runs left to right over the pure hue, value top to bottom.
		var white = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0);
		var black = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90);
		var field = new Grid { Width = SvSize, Height = SvSize, ClipToBounds = true, Cursor = Cursors.Cross };
		field.Children.Add(svField);
		field.Children.Add(new Rectangle { Fill = white, IsHitTestVisible = false });
		field.Children.Add(new Rectangle { Fill = black, IsHitTestVisible = false });
		var markerCanvas = new Canvas { IsHitTestVisible = false };
		markerCanvas.Children.Add(svMarker);
		field.Children.Add(markerCanvas);
		void PickSv(Point point) => Change(() => {
			var a = model.SelectedColor.A;
			var saturation = Math.Max(0, Math.Min(1, point.X / SvSize));
			var value = Math.Max(0, Math.Min(1, 1 - point.Y / SvSize));
			model.SelectedColor = FromHsv(currentHue, saturation, value, a);
		});
		field.MouseLeftButtonDown += (_, e) => { field.CaptureMouse(); PickSv(e.GetPosition(field)); };
		field.MouseMove += (_, e) => { if (field.IsMouseCaptured) PickSv(e.GetPosition(field)); };
		field.MouseLeftButtonUp += (_, _) => field.ReleaseMouseCapture();

		var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0) };
		for (var i = 0; i <= 6; i++)
			rainbow.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1, 255), i / 6.0));
		hue.Background = rainbow;
		hue.ValueChanged += (_, _) => {
			if (syncing)
				return;
			currentHue = hue.Value;
			var (_, saturation, value) = ToHsv(model.SelectedColor);
			Change(() => model.SelectedColor = FromHsv(currentHue, saturation, value, model.SelectedColor.A));
		};

		var top = new StackPanel { Orientation = Orientation.Horizontal };
		top.Children.Add(field);
		top.Children.Add(hue);

		foreach (var box in new[] { red, green, blue, alpha, hex }) {
			box.LostFocus += (_, _) => CommitColorText(box);
			box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitColorText(box); };
		}
		var channels = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
		foreach (var (label, box) in new[] { ("R", red), ("G", green), ("B", blue), ("A", alpha) }) {
			channels.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
			channels.Children.Add(box);
			box.Margin = new Thickness(0, 0, 8, 0);
		}
		var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
		chips.Children.Add(hex);
		chips.Children.Add(new TextBlock { Text = "Initial", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) });
		chips.Children.Add(initialChip);
		chips.Children.Add(new TextBlock { Text = "Current", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
		chips.Children.Add(currentChip);
		initialChip.Background = new SolidColorBrush(initialColor);
		initialChip.ToolTip = "Restore the initial color";
		initialChip.MouseLeftButtonDown += (_, _) => Change(() => model.SelectedColor = initialColor);

		var panel = new StackPanel();
		panel.Children.Add(top);
		panel.Children.Add(channels);
		panel.Children.Add(chips);
		return panel;
	}

	(FrameworkElement, Grid, Grid) BuildAdvancedPanel()
	{
		void Bind(TextBox box, Action<string> apply)
		{
			void Commit()
			{
				if (syncing)
					return;
				try {
					Change(() => apply(box.Text));
				} catch (FormatException) {
					Refresh();
				}
			}
			box.LostFocus += (_, _) => Commit();
			box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
		}
		static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
		static (double, double) Pair(string text)
		{
			var parts = text.Split(',');
			return parts.Length == 2 ? (Number(parts[0]), Number(parts[1])) : throw new FormatException();
		}
		Bind(opacity, text => model.Opacity = Math.Max(0, Math.Min(1, Number(text))));
		Bind(start, text => (model.StartX, model.StartY) = Pair(text));
		Bind(end, text => (model.EndX, model.EndY) = Pair(text));
		Bind(center, text => (model.CenterX, model.CenterY) = Pair(text));
		Bind(origin, text => (model.OriginX, model.OriginY) = Pair(text));
		Bind(radius, text => (model.RadiusX, model.RadiusY) = Pair(text));
		mapping.SelectionChanged += (_, _) => { if (!syncing && mapping.SelectedItem is string value) Change(() => model.MappingMode = value); };
		spread.SelectionChanged += (_, _) => { if (!syncing && spread.SelectedItem is string value) Change(() => model.SpreadMethod = value); };

		var linearGrid = Rows(("StartPoint", start), ("EndPoint", end), ("MappingMode", mapping), ("SpreadMethod", spread));
		var radialGrid = Rows(("Center", center), ("GradientOrigin", origin), ("RadiusX, RadiusY", radius), ("MappingMode", mapping), ("SpreadMethod", spread));
		// One mapping/spread pair is shared, so it lives in whichever grid is shown.
		var panel = new StackPanel();
		panel.Children.Add(Row("Opacity", opacity));
		panel.Children.Add(linearGrid);
		panel.Children.Add(radialGrid);
		var expander = new Expander { Header = "Advanced", Content = panel, Margin = new Thickness(0, 8, 0, 0) };
		return (expander, linearGrid, radialGrid);
	}

	static Grid Rows(params (string Label, FrameworkElement Editor)[] rows)
	{
		var grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		for (var i = 0; i < rows.Length; i++) {
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			var label = new TextBlock { Text = rows[i].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
			Grid.SetRow(label, i);
			grid.Children.Add(label);
		}
		grid.Tag = rows;
		return grid;
	}

	static FrameworkElement Row(string label, FrameworkElement editor)
	{
		var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
		panel.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
		panel.Children.Add(editor);
		return panel;
	}

	/// <summary>Moves the shared editors into the grid being shown (a WPF element has one parent).</summary>
	static void Populate(Grid grid)
	{
		var rows = ((string Label, FrameworkElement Editor)[])grid.Tag;
		for (var i = 0; i < rows.Length; i++) {
			var editor = rows[i].Editor;
			if (editor.Parent == grid)
				continue;
			(editor.Parent as Panel)?.Children.Remove(editor);
			editor.Margin = new Thickness(0, 2, 0, 2);
			editor.HorizontalAlignment = HorizontalAlignment.Left;
			Grid.SetRow(editor, i);
			Grid.SetColumn(editor, 1);
			grid.Children.Add(editor);
		}
	}

	void CommitOffset()
	{
		if (syncing || model.SelectedStop < 0)
			return;
		if (double.TryParse(offsetBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var offset))
			Change(() => model.SetStopOffset(model.SelectedStop, offset));
		else
			Refresh();
	}

	void CommitColorText(TextBox box)
	{
		if (syncing)
			return;
		var color = model.SelectedColor;
		if (box == hex) {
			if (BrushEditModel.TryParseColor(hex.Text, out var parsed))
				color = parsed;
		} else if (byte.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)) {
			color = box == red ? Color.FromArgb(color.A, channel, color.G, color.B)
				: box == green ? Color.FromArgb(color.A, color.R, channel, color.B)
				: box == blue ? Color.FromArgb(color.A, color.R, color.G, channel)
				: Color.FromArgb(channel, color.R, color.G, color.B);
		}
		var hsv = ToHsv(color);
		if (hsv.Saturation > 0)
			currentHue = hsv.Hue;
		Change(() => model.SelectedColor = color);
	}

	void Change(Action action)
	{
		action();
		Refresh();
	}

	void Refresh()
	{
		syncing = true;
		try {
			noBrushTab.IsChecked = !showResources && !showImage && model.Kind == BrushEditModel.NullKind;
			solidTab.IsChecked = !showResources && !showImage && model.Kind == BrushEditModel.SolidKind;
			gradientTab.IsChecked = !showResources && !showImage && model.IsGradient;
			resourcesTab.IsChecked = showResources;
			imageTab.IsChecked = showImage;
			imagePanel.Visibility = showImage ? Visibility.Visible : Visibility.Collapsed;
			imageSource.Text = model.Kind == BrushEditModel.ImageKind ? model.ImageSource : "";
			imageStretch.SelectedItem = model.Kind == BrushEditModel.ImageKind ? (model.Stretch.Length == 0 ? "Fill" : model.Stretch) : null;
			resources.Visibility = showResources ? Visibility.Visible : Visibility.Collapsed;
			resources.SelectedItem = model.SelectedResourceEntry;
			gradientPanel.Visibility = !showResources && !showImage && model.IsGradient ? Visibility.Visible : Visibility.Collapsed;
			colorPanel.Visibility = showResources || showImage || model.Kind is BrushEditModel.NullKind or BrushEditModel.ResourceKind or BrushEditModel.SystemKind or BrushEditModel.ImageKind ? Visibility.Collapsed : Visibility.Visible;
			// The eyedropper sets the color the color editor shows, so it lives and hides with it.
			eyedropperToggle.Visibility = designFrame != null && colorPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
			eyedropperFrame.Visibility = eyedropperToggle.Visibility == Visibility.Visible && eyedropperToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
			advancedPanel.Visibility = colorPanel.Visibility;
			linear.IsChecked = model.Kind == BrushEditModel.LinearKind;
			radial.IsChecked = model.Kind == BrushEditModel.RadialKind;
			linearRows.Visibility = model.Kind == BrushEditModel.LinearKind ? Visibility.Visible : Visibility.Collapsed;
			radialRows.Visibility = model.Kind == BrushEditModel.RadialKind ? Visibility.Visible : Visibility.Collapsed;
			Populate(model.Kind == BrushEditModel.RadialKind ? radialRows : linearRows);

			var color = model.SelectedColor;
			var (h, s, v) = ToHsv(color);
			if (s > 0 && v > 0)
				currentHue = h;
			hue.Value = currentHue;
			svField.Fill = new SolidColorBrush(FromHsv(currentHue, 1, 1, 255));
			Canvas.SetLeft(svMarker, s * SvSize - svMarker.Width / 2);
			Canvas.SetTop(svMarker, (1 - v) * SvSize - svMarker.Height / 2);
			red.Text = color.R.ToString(CultureInfo.InvariantCulture);
			green.Text = color.G.ToString(CultureInfo.InvariantCulture);
			blue.Text = color.B.ToString(CultureInfo.InvariantCulture);
			alpha.Text = color.A.ToString(CultureInfo.InvariantCulture);
			hex.Text = BrushEditModel.FormatColor(color);
			currentChip.Background = new SolidColorBrush(color);

			gradientBar.Background = new LinearGradientBrush(new GradientStopCollection(
				model.Stops.Select(stop => new GradientStop(stop.Color, stop.Offset))), 0);
			offsetBox.Text = model.SelectedStop >= 0 && model.IsGradient
				? model.Stops[model.SelectedStop].Offset.ToString("0.###", CultureInfo.InvariantCulture) : "";
			LayoutStops();

			opacity.Text = model.Opacity.ToString("0.###", CultureInfo.InvariantCulture);
			string P(double x, double y) => x.ToString("0.###", CultureInfo.InvariantCulture) + "," + y.ToString("0.###", CultureInfo.InvariantCulture);
			start.Text = P(model.StartX, model.StartY);
			end.Text = P(model.EndX, model.EndY);
			center.Text = P(model.CenterX, model.CenterY);
			origin.Text = P(model.OriginX, model.OriginY);
			radius.Text = P(model.RadiusX, model.RadiusY);
			mapping.SelectedItem = model.MappingMode;
			spread.SelectedItem = model.SpreadMethod;
			preview.Background = model.ToPreviewBrush() ?? (Brush)Brushes.Transparent;
		} finally {
			syncing = false;
		}
	}

	void LayoutStops()
	{
		stopLayer.Children.Clear();
		if (!model.IsGradient)
			return;
		var width = Math.Max(1, gradientBar.ActualWidth);
		for (var i = 0; i < model.Stops.Count; i++) {
			var index = i;
			var thumb = new Border {
				Width = StopWidth, Height = 14,
				Background = new SolidColorBrush(model.Stops[i].Color),
				BorderBrush = i == model.SelectedStop ? Brushes.DodgerBlue : Brushes.Gray,
				BorderThickness = new Thickness(i == model.SelectedStop ? 2 : 1),
				Cursor = Cursors.SizeWE
			};
			Canvas.SetLeft(thumb, model.Stops[i].Offset * width - StopWidth / 2);
			thumb.MouseLeftButtonDown += (_, e) => {
				Change(() => model.SelectStop(index));
				draggedStop = index;
				stopLayer.CaptureMouse();
				e.Handled = true;
			};
			stopLayer.Children.Add(thumb);
		}
	}

	/// <summary>Hue in degrees, saturation and value in 0-1.</summary>
	public static (double Hue, double Saturation, double Value) ToHsv(Color color)
	{
		double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
		var max = Math.Max(r, Math.Max(g, b));
		var min = Math.Min(r, Math.Min(g, b));
		var delta = max - min;
		double hue = 0;
		if (delta > 0) {
			hue = max == r ? 60 * (((g - b) / delta) % 6)
				: max == g ? 60 * ((b - r) / delta + 2)
				: 60 * ((r - g) / delta + 4);
			if (hue < 0)
				hue += 360;
		}
		return (hue, max == 0 ? 0 : delta / max, max);
	}

	public static Color FromHsv(double hue, double saturation, double value, byte alpha)
	{
		var c = value * saturation;
		var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
		var m = value - c;
		var (r, g, b) = (hue % 360) switch {
			< 60 => (c, x, 0.0),
			< 120 => (x, c, 0.0),
			< 180 => (0.0, c, x),
			< 240 => (0.0, x, c),
			< 300 => (x, 0.0, c),
			_ => (c, 0.0, x)
		};
		byte Channel(double component) => (byte)Math.Round((component + m) * 255);
		return Color.FromArgb(alpha, Channel(r), Channel(g), Channel(b));
	}
}
