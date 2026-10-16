using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>A Properties-pad descriptor for a host-published <c>ImageListImages</c> editor row.</summary>
public interface IImageListProperty
{
	IReadOnlyList<DesignerImageListEntry> Images { get; }
	/// <summary>The list's ImageSize as <c>width,height</c>; the images are scaled to it.</summary>
	string ImageSize { get; }
	string ColorDepth { get; }
}

/// <summary>The Properties pad's selected object, which commits an edited image list.</summary>
public interface IImageListEditorHost
{
	void SetImageList(string propertyName, IReadOnlyList<DesignerImageListEntry> images, string imageSize, string colorDepth);
}

/// <summary>
/// The state behind the ImageList editor: the list's images in order, each with its key and PNG pixels. Images can be added,
/// removed, reordered and renamed; keys stay unique (an empty key is allowed and means "unnamed"). The editor never decodes
/// pixels itself - the host scales every image to the list's size when it builds the stream - so entries carry PNG bytes only.
/// </summary>
public sealed class ImageListEditModel
{
	public sealed class Entry
	{
		public string Key { get; set; } = "";
		public byte[] Png { get; set; } = Array.Empty<byte>();
	}

	readonly List<Entry> entries;
	readonly string initial;

	public ImageListEditModel(IEnumerable<DesignerImageListEntry>? images, string imageSize = "", string colorDepth = "Depth32Bit")
	{
		entries = (images ?? Array.Empty<DesignerImageListEntry>()).Select(image => new Entry { Key = image.Key ?? "", Png = image.Png ?? Array.Empty<byte>() }).ToList();
		var parts = (imageSize ?? "").Split(',');
		Width = parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ? width : 16;
		Height = parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height) ? height : 16;
		ColorDepth = string.IsNullOrEmpty(colorDepth) ? "Depth32Bit" : colorDepth;
		initial = Fingerprint();
	}

	public IReadOnlyList<Entry> Entries => entries;
	public int Width { get; private set; }
	public int Height { get; private set; }
	public string ColorDepth { get; private set; }
	public bool IsChanged => Fingerprint() != initial;

	/// <summary>Adds an image at the end, making <paramref name="key"/> unique (blank stays unnamed). Returns its index, or -1 for no pixels.</summary>
	public int Add(string key, byte[] png)
	{
		if (png == null || png.Length == 0)
			return -1;
		entries.Add(new Entry { Key = UniqueKey(key, -1), Png = png });
		return entries.Count - 1;
	}

	public bool Remove(int index)
	{
		if (index < 0 || index >= entries.Count)
			return false;
		entries.RemoveAt(index);
		return true;
	}

	public bool MoveUp(int index) => Move(index, -1);
	public bool MoveDown(int index) => Move(index, 1);

	bool Move(int index, int delta)
	{
		var target = index + delta;
		if (index < 0 || index >= entries.Count || target < 0 || target >= entries.Count)
			return false;
		(entries[index], entries[target]) = (entries[target], entries[index]);
		return true;
	}

	/// <summary>Renames an image; false (nothing changed) when another image already has that non-empty key.</summary>
	public bool Rename(int index, string key)
	{
		key ??= "";
		if (index < 0 || index >= entries.Count)
			return false;
		if (key.Length > 0 && entries.Where((other, i) => i != index).Any(other => other.Key == key))
			return false;
		entries[index].Key = key;
		return true;
	}

	/// <summary>Changes the serialized ImageSize. ImageList accepts sizes from one through 256 pixels.</summary>
	public bool SetImageSize(int width, int height)
	{
		if (width is < 1 or > 256 || height is < 1 or > 256)
			return false;
		Width = width;
		Height = height;
		return true;
	}

	/// <summary>Changes the serialized ColorDepth to one of the WinForms enum names.</summary>
	public bool SetColorDepth(string colorDepth)
	{
		if (colorDepth is not ("Depth4Bit" or "Depth8Bit" or "Depth16Bit" or "Depth24Bit" or "Depth32Bit"))
			return false;
		ColorDepth = colorDepth;
		return true;
	}

	/// <summary>The key for a new image: <paramref name="wanted"/> itself, else with " (2)", " (3)"... appended; blank stays blank.</summary>
	public string UniqueKey(string wanted, int ignoreIndex)
	{
		wanted ??= "";
		if (wanted.Length == 0)
			return "";
		var taken = entries.Where((_, i) => i != ignoreIndex).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
		if (!taken.Contains(wanted))
			return wanted;
		var dot = wanted.LastIndexOf('.');
		var stem = dot > 0 ? wanted[..dot] : wanted;
		var extension = dot > 0 ? wanted[dot..] : "";
		for (var number = 2;; number++) {
			var candidate = stem + " (" + number.ToString(CultureInfo.InvariantCulture) + ")" + extension;
			if (!taken.Contains(candidate))
				return candidate;
		}
	}

	public IReadOnlyList<DesignerImageListEntry> ToEntries()
		=> entries.Select(entry => new DesignerImageListEntry { Key = entry.Key, Png = entry.Png }).ToList();

	string Fingerprint()
		=> Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture) + ":" + ColorDepth + "|"
			+ string.Join("|", entries.Select(entry => entry.Key + ":" + Convert.ToHexString(SHA256.HashData(entry.Png))));
}

/// <summary>
/// The shared flow behind an <c>ImageListImages</c> Properties-pad row: open <see cref="ImageListEditorDialog"/> (or a test edit) over
/// the row's images and commit only an accepted, changed list to the row's host.
/// </summary>
public static class ImageListEditing
{
	/// <summary>DevFlow supplies an edit script instead of the modal dialog; normal use never sets it.</summary>
	public static Func<ImageListEditModel, bool>? TestEdit { get; set; }
	public static Exception? TestException { get; set; }

	/// <summary>Reads an image file as PNG bytes through WPF's decoders (so png, jpg, bmp, gif and ico files are all accepted), or null.</summary>
	public static byte[]? ReadAsPng(string path)
	{
		try {
			// A PNG is used as is: lossless, and the only path that works where WPF has no native encoder
			// (LibreWPF on macOS throws from PngBitmapEncoder).
			var bytes = File.ReadAllBytes(Path.GetFullPath(path));
			if (IsPng(bytes))
				return bytes;
			var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
			var encoder = new PngBitmapEncoder();
			encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
			using var stream = new MemoryStream();
			encoder.Save(stream);
			return stream.ToArray();
		} catch (Exception) {
			return null;
		}
	}

	static bool IsPng(byte[] bytes)
		=> bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
			&& bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

	/// <summary>
	/// Applies an edit script to a model, as a user would in the dialog: ';'-separated <c>add:&lt;path&gt;</c> (an image file; its file
	/// name becomes the key), <c>remove:&lt;i&gt;</c>, <c>up:&lt;i&gt;</c>, <c>down:&lt;i&gt;</c>, <c>rename:&lt;i&gt;:&lt;key&gt;</c>,
	/// <c>size:&lt;width&gt;x&lt;height&gt;</c>, <c>depth:&lt;ColorDepth name&gt;</c>, or
	/// <c>cancel</c>. Returns false for cancel; throws for an unknown or rejected step.
	/// </summary>
	public static bool ApplyScript(ImageListEditModel model, string script)
	{
		foreach (var step in (script ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = step.Split(':', 3);
			if (parts[0] == "cancel")
				return false;
			var index = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : -1;
			var ok = parts[0] switch {
				"add" => parts.Length > 1 && ReadAsPng(step.Substring("add:".Length)) is { } png && model.Add(Path.GetFileName(step.Substring("add:".Length)), png) >= 0,
				"remove" => model.Remove(index),
				"up" => model.MoveUp(index),
				"down" => model.MoveDown(index),
				"rename" => model.Rename(index, parts.Length > 2 ? parts[2] : ""),
				"size" => TryParseSize(parts.Length > 1 ? parts[1] : "", out var width, out var height) && model.SetImageSize(width, height),
				"depth" => model.SetColorDepth(parts.Length > 1 ? parts[1] : ""),
				_ => throw new ArgumentException("Unknown image list edit step: " + step)
			};
			if (!ok)
				throw new ArgumentException("Rejected image list edit step: " + step);
		}
		return true;
	}

	static bool TryParseSize(string text, out int width, out int height)
	{
		width = height = 0;
		var parts = text.Split('x', 'X');
		return parts.Length == 2 && Int32.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
			&& Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height);
	}

	public sealed record ScriptedEditResult(bool Success, string? Error, string[]? Before, string[]? After, bool Committed);

	/// <summary>Clicks a rendered image-list "…" button with <paramref name="script"/> applied in place of the modal dialog (DevFlow seam).</summary>
	public static ScriptedEditResult RunScriptedEdit(System.Windows.Controls.Primitives.ButtonBase button, string script)
	{
		string[]? before = null, after = null;
		var committed = false;
		TestException = null;
		TestEdit = model => {
			before = model.Entries.Select(entry => entry.Key).ToArray();
			if (!ApplyScript(model, script))
				return false;
			after = model.Entries.Select(entry => entry.Key).ToArray();
			committed = model.IsChanged;
			return true;
		};
		try {
			button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
		} finally {
			TestEdit = null;
		}
		return TestException != null
			? new ScriptedEditResult(false, TestException.Message, before, after, false)
			: new ScriptedEditResult(true, null, before, after, committed);
	}

	/// <summary>Runs the editor for <paramref name="descriptor"/> on <paramref name="instance"/> (the row's own selected object). Failures go to
	/// <paramref name="reportError"/>, or to <see cref="TestException"/> while a test edit is active.</summary>
	public static void Edit(object? instance, PropertyDescriptor? descriptor, Window? owner, Action<Exception> reportError)
	{
		try {
			if (instance is not IImageListEditorHost host || descriptor is not IImageListProperty property)
				throw new InvalidOperationException("The image list editor is not hosted by a designer Properties pad.");
			var model = new ImageListEditModel(property.Images, property.ImageSize, property.ColorDepth);
			var accepted = TestEdit != null
				? TestEdit(model)
				: new ImageListEditorDialog(model, descriptor.DisplayName) { Owner = owner }.ShowDialog() == true;
			if (accepted && model.IsChanged)
				host.SetImageList(descriptor.Name, model.ToEntries(), model.Width + "," + model.Height, model.ColorDepth);
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
/// The ImageList editor: the images as thumbnails with their keys on the left, Add (image files)/Remove/reorder commands on the right,
/// and the selected image's key below. The list's image size is shown; images are scaled to it by the host.
/// </summary>
public sealed class ImageListEditorDialog : Window
{
	readonly ImageListEditModel model;
	readonly ListBox list = new() { MinHeight = 200 };
	readonly TextBox key = new() { Margin = new Thickness(0, 6, 0, 0) };
	bool syncing;

	public ImageListEditorDialog(ImageListEditModel model, string propertyName)
	{
		this.model = model;
		Title = "Edit " + propertyName;
		Width = 420;
		Height = 420;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		ResizeMode = ResizeMode.CanResizeWithGrip;
		list.ItemTemplate = CreateItemTemplate();

		Button Command(string label, Action action)
		{
			var button = new Button { Content = label, Margin = new Thickness(0, 0, 0, 4), MinWidth = 84 };
			button.Click += (_, _) => action();
			return button;
		}
		var commands = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
		commands.Children.Add(Command("Add...", AddFiles));
		commands.Children.Add(Command("Remove", () => { var index = list.SelectedIndex; if (model.Remove(index)) Refresh(Math.Min(index, model.Entries.Count - 1)); }));
		commands.Children.Add(Command("Move Up", () => { var index = list.SelectedIndex; if (model.MoveUp(index)) Refresh(index - 1); }));
		commands.Children.Add(Command("Move Down", () => { var index = list.SelectedIndex; if (model.MoveDown(index)) Refresh(index + 1); }));
		commands.Children.Add(new TextBlock { Text = "Image size", Margin = new Thickness(0, 12, 0, 2), Opacity = 0.7 });
		var width = new TextBox { Text = model.Width.ToString(CultureInfo.InvariantCulture), Width = 84, ToolTip = "Width (1–256 pixels)" };
		var height = new TextBox { Text = model.Height.ToString(CultureInfo.InvariantCulture), Width = 84, Margin = new Thickness(0, 2, 0, 0), ToolTip = "Height (1–256 pixels)" };
		commands.Children.Add(width);
		commands.Children.Add(height);
		commands.Children.Add(new TextBlock { Text = "Color depth", Margin = new Thickness(0, 8, 0, 2), Opacity = 0.7 });
		var depth = new ComboBox { Width = 112, ItemsSource = new[] { "Depth4Bit", "Depth8Bit", "Depth16Bit", "Depth24Bit", "Depth32Bit" }, SelectedItem = model.ColorDepth };
		commands.Children.Add(depth);

		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 6, 0) };
		ok.Click += (_, _) => {
			if (!Int32.TryParse(width.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedWidth)
				|| !Int32.TryParse(height.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedHeight)
				|| !model.SetImageSize(parsedWidth, parsedHeight)
				|| !model.SetColorDepth(depth.SelectedItem as string ?? "")) {
				MessageBox.Show(this, "Image size must be between 1 and 256 pixels, and color depth must be supported.", Title,
					MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			DialogResult = true;
		};
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
		var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
		footer.Children.Add(ok);
		footer.Children.Add(cancel);

		var body = new DockPanel();
		DockPanel.SetDock(commands, Dock.Right);
		body.Children.Add(commands);
		var left = new DockPanel();
		DockPanel.SetDock(key, Dock.Bottom);
		left.Children.Add(key);
		left.Children.Add(list);
		body.Children.Add(left);
		var root = new DockPanel { Margin = new Thickness(10) };
		DockPanel.SetDock(footer, Dock.Bottom);
		root.Children.Add(footer);
		root.Children.Add(body);
		Content = root;
		SetResourceReference(BackgroundProperty, "ToolWindowBackground");

		list.SelectionChanged += (_, _) => {
			syncing = true;
			key.Text = list.SelectedIndex >= 0 ? model.Entries[list.SelectedIndex].Key : "";
			key.IsEnabled = list.SelectedIndex >= 0;
			syncing = false;
		};
		key.LostKeyboardFocus += (_, _) => {
			if (syncing || list.SelectedIndex < 0)
				return;
			var index = list.SelectedIndex;
			// A duplicate key is refused: the box goes back to the image's current key.
			if (model.Rename(index, key.Text))
				Refresh(index);
			else
				key.Text = model.Entries[index].Key;
		};
		Refresh(model.Entries.Count > 0 ? 0 : -1);
	}

	void AddFiles()
	{
		var dialog = new Microsoft.Win32.OpenFileDialog {
			Title = "Add images",
			Multiselect = true,
			Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tif;*.tiff|All files|*.*"
		};
		if (dialog.ShowDialog(this) != true)
			return;
		var last = -1;
		foreach (var file in dialog.FileNames)
			if (ImageListEditing.ReadAsPng(file) is { } png)
				last = model.Add(Path.GetFileName(file), png);
		Refresh(last >= 0 ? last : list.SelectedIndex);
	}

	void Refresh(int select)
	{
		syncing = true;
		try {
			list.ItemsSource = null;
			list.ItemsSource = model.Entries;
			list.SelectedIndex = select;
		} finally {
			syncing = false;
		}
		key.Text = list.SelectedIndex >= 0 ? model.Entries[list.SelectedIndex].Key : "";
		key.IsEnabled = list.SelectedIndex >= 0;
		if (list.SelectedItem != null)
			list.ScrollIntoView(list.SelectedItem);
	}

	static DataTemplate CreateItemTemplate()
	{
		var factory = new FrameworkElementFactory(typeof(StackPanel));
		factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
		var image = new FrameworkElementFactory(typeof(Image));
		image.SetValue(FrameworkElement.WidthProperty, 32.0);
		image.SetValue(FrameworkElement.HeightProperty, 32.0);
		image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(ImageListEditModel.Entry.Png)) { Converter = PngConverter.Instance });
		factory.AppendChild(image);
		var text = new FrameworkElementFactory(typeof(TextBlock));
		text.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
		text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ImageListEditModel.Entry.Key)));
		factory.AppendChild(text);
		return new DataTemplate { VisualTree = factory };
	}

	sealed class PngConverter : System.Windows.Data.IValueConverter
	{
		public static readonly PngConverter Instance = new();

		public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
		{
			if (value is not byte[] { Length: > 0 } bytes)
				return null;
			try {
				var bitmap = new BitmapImage();
				bitmap.BeginInit();
				bitmap.CacheOption = BitmapCacheOption.OnLoad;
				bitmap.StreamSource = new MemoryStream(bytes);
				bitmap.EndInit();
				bitmap.Freeze();
				return bitmap;
			} catch (Exception) {
				return null;
			}
		}

		public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
	}
}
