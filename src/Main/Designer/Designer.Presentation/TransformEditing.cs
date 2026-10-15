using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>A Properties-pad descriptor for a host-published <c>Transform</c> editor row.</summary>
public interface ITransformProperty
{
	/// <summary>The transform as an ordered list of simple operations (empty when unset).</summary>
	IReadOnlyList<DesignerTransformOp> Transform { get; }
}

/// <summary>The Properties pad's selected object, which commits an edited transform.</summary>
public interface ITransformEditorHost
{
	void SetTransform(string propertyName, IReadOnlyList<DesignerTransformOp> ops);
}

/// <summary>
/// The state behind the transform editor dialog: an ordered list of simple transforms (Rotate, Scale,
/// Skew, Translate), which the host writes as one transform or a TransformGroup. Starts from the
/// host's <see cref="DesignerTransformOp"/> list and produces one for <c>design/set-transform</c>.
/// </summary>
public sealed class TransformEditModel
{
	public static IReadOnlyList<string> Types { get; } = new[] { "Rotate", "Scale", "Skew", "Translate" };

	/// <summary>The numeric fields an operation of <paramref name="type"/> has, in display order.</summary>
	public static IReadOnlyList<string> Fields(string type) => type switch {
		"Rotate" => new[] { "Angle", "CenterX", "CenterY" },
		"Scale" => new[] { "ScaleX", "ScaleY", "CenterX", "CenterY" },
		"Skew" => new[] { "AngleX", "AngleY", "CenterX", "CenterY" },
		"Translate" => new[] { "X", "Y" },
		_ => Array.Empty<string>()
	};

	readonly List<DesignerTransformOp> ops;
	readonly string initial;

	public TransformEditModel(IEnumerable<DesignerTransformOp>? transform)
	{
		ops = (transform ?? Array.Empty<DesignerTransformOp>()).Select(Copy).ToList();
		SelectedIndex = ops.Count > 0 ? 0 : -1;
		initial = Serialize(ops);
	}

	public IReadOnlyList<DesignerTransformOp> Operations => ops;
	public int SelectedIndex { get; private set; }
	public bool IsChanged => Serialize(ops) != initial;

	public bool Select(int index)
	{
		if (index < -1 || index >= ops.Count)
			return false;
		SelectedIndex = index;
		return true;
	}

	/// <summary>Appends a default operation of <paramref name="type"/> and selects it.</summary>
	public bool Add(string type)
	{
		if (!Types.Contains(type))
			return false;
		ops.Add(new DesignerTransformOp { Type = type });
		SelectedIndex = ops.Count - 1;
		return true;
	}

	public bool Remove(int index)
	{
		if (index < 0 || index >= ops.Count)
			return false;
		ops.RemoveAt(index);
		SelectedIndex = Math.Min(SelectedIndex, ops.Count - 1);
		return true;
	}

	/// <summary>Moves an operation one place up (<paramref name="delta"/> -1) or down (+1); the order is the order they apply in.</summary>
	public bool Move(int index, int delta)
	{
		var target = index + delta;
		if (index < 0 || index >= ops.Count || target < 0 || target >= ops.Count || Math.Abs(delta) != 1)
			return false;
		(ops[index], ops[target]) = (ops[target], ops[index]);
		SelectedIndex = target;
		return true;
	}

	/// <summary>Sets one numeric field of an operation; false (nothing changed) for an unknown field of that type or a non-finite value.</summary>
	public bool SetField(int index, string field, double value)
	{
		if (index < 0 || index >= ops.Count || !Fields(ops[index].Type).Contains(field) || !double.IsFinite(value))
			return false;
		typeof(DesignerTransformOp).GetProperty(field)!.SetValue(ops[index], value);
		return true;
	}

	public double GetField(int index, string field) => (double)typeof(DesignerTransformOp).GetProperty(field)!.GetValue(ops[index])!;

	public IReadOnlyList<DesignerTransformOp> ToOps() => ops.Select(Copy).ToList();

	static DesignerTransformOp Copy(DesignerTransformOp op) => new() {
		Type = op.Type, Angle = op.Angle, AngleX = op.AngleX, AngleY = op.AngleY, ScaleX = op.ScaleX, ScaleY = op.ScaleY,
		X = op.X, Y = op.Y, CenterX = op.CenterX, CenterY = op.CenterY
	};

	static string Serialize(IEnumerable<DesignerTransformOp> list) => JsonSerializer.Serialize(list);

	/// <summary>A one-line description, e.g. "Rotate(45), Scale(2, 2)", for the list and the row.</summary>
	public static string Describe(DesignerTransformOp op)
	{
		string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
		return op.Type switch {
			"Rotate" => $"Rotate({N(op.Angle)})",
			"Scale" => $"Scale({N(op.ScaleX)}, {N(op.ScaleY)})",
			"Skew" => $"Skew({N(op.AngleX)}, {N(op.AngleY)})",
			_ => $"Translate({N(op.X)}, {N(op.Y)})"
		};
	}
}

/// <summary>The shared flow behind a <c>Transform</c> Properties-pad row: open <see cref="TransformEditorDialog"/> (or a test edit) over
/// the row's transform and commit only an accepted, changed list.</summary>
public static class TransformEditing
{
	/// <summary>DevFlow supplies an edit script instead of the modal dialog, as for the brush and items editors.</summary>
	public static Func<TransformEditModel, bool>? TestEdit { get; set; }
	public static Exception? TestException { get; set; }

	public sealed record ScriptedEditResult(bool Success, string? Error, IReadOnlyList<DesignerTransformOp>? Before, IReadOnlyList<DesignerTransformOp>? After, bool Committed);

	/// <summary>
	/// Applies an edit script to a model: ';'-separated <c>add:&lt;Rotate|Scale|Skew|Translate&gt;</c>, <c>remove:&lt;i&gt;</c>,
	/// <c>up:&lt;i&gt;</c>, <c>down:&lt;i&gt;</c>, <c>select:&lt;i&gt;</c>, <c>set:&lt;i&gt;:&lt;Field&gt;:&lt;value&gt;</c>, or <c>cancel</c>.
	/// Returns false for cancel; throws for an unknown or rejected step.
	/// </summary>
	public static bool ApplyScript(TransformEditModel model, string script)
	{
		foreach (var step in (script ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = step.Split(':', 4);
			var ok = true;
			switch (parts[0]) {
				case "cancel": return false;
				case "add": ok = parts.Length > 1 && model.Add(parts[1]); break;
				case "remove": ok = parts.Length > 1 && model.Remove(int.Parse(parts[1], CultureInfo.InvariantCulture)); break;
				case "up": ok = parts.Length > 1 && model.Move(int.Parse(parts[1], CultureInfo.InvariantCulture), -1); break;
				case "down": ok = parts.Length > 1 && model.Move(int.Parse(parts[1], CultureInfo.InvariantCulture), 1); break;
				case "select": ok = parts.Length > 1 && model.Select(int.Parse(parts[1], CultureInfo.InvariantCulture)); break;
				case "set":
					ok = parts.Length > 3 && model.SetField(int.Parse(parts[1], CultureInfo.InvariantCulture), parts[2],
						double.Parse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture));
					break;
				default: throw new ArgumentException("Unknown transform edit step: " + step);
			}
			if (!ok)
				throw new ArgumentException("Rejected transform edit step: " + step);
		}
		return true;
	}

	/// <summary>Clicks a rendered transform "…" button with <paramref name="script"/> applied in place of the modal dialog, exercising
	/// the real button handler and commit path (DevFlow seam).</summary>
	public static ScriptedEditResult RunScriptedEdit(ButtonBase button, string script)
	{
		IReadOnlyList<DesignerTransformOp>? before = null;
		IReadOnlyList<DesignerTransformOp>? after = null;
		var committed = false;
		TestException = null;
		TestEdit = model => {
			before = model.ToOps();
			if (!ApplyScript(model, script))
				return false;
			after = model.ToOps();
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

	/// <summary>Runs the editor for <paramref name="descriptor"/> on <paramref name="instance"/> (the row's own selected object). Failures
	/// go to <paramref name="reportError"/>, or to <see cref="TestException"/> while a test edit is active.</summary>
	public static void Edit(object? instance, PropertyDescriptor? descriptor, Window? owner, Action<Exception> reportError)
	{
		try {
			if (instance is not ITransformEditorHost host || descriptor is not ITransformProperty property)
				throw new InvalidOperationException("The transform editor is not hosted by a designer Properties pad.");
			var model = new TransformEditModel(property.Transform);
			var accepted = TestEdit != null
				? TestEdit(model)
				: new TransformEditorDialog(model, descriptor.DisplayName) { Owner = owner }.ShowDialog() == true;
			if (accepted && model.IsChanged)
				host.SetTransform(descriptor.Name, model.ToOps());
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
/// The transform editor: the ordered list of operations on the left (add one of Rotate, Scale, Skew, Translate; remove; reorder -
/// the order is the order they apply in), and the selected operation's numeric fields on the right.
/// </summary>
public sealed class TransformEditorDialog : Window
{
	readonly TransformEditModel model;
	readonly ListBox list = new() { MinHeight = 140, MinWidth = 170 };
	readonly ComboBox addType = new() { ItemsSource = TransformEditModel.Types, SelectedIndex = 0, Margin = new Thickness(0, 0, 4, 0) };
	readonly StackPanel fields = new() { Margin = new Thickness(12, 0, 0, 0), MinWidth = 160 };
	bool syncing;

	public TransformEditorDialog(TransformEditModel model, string propertyName)
	{
		this.model = model;
		Title = "Edit " + propertyName;
		SizeToContent = SizeToContent.WidthAndHeight;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		var root = new StackPanel { Margin = new Thickness(12) };
		var body = new DockPanel { LastChildFill = true };
		var left = new StackPanel();
		left.Children.Add(list);
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
		var add = new Button { Content = "Add", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
		var remove = new Button { Content = "Remove", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
		var up = new Button { Content = "▲", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Apply earlier" };
		var down = new Button { Content = "▼", Padding = new Thickness(6, 2, 6, 2), ToolTip = "Apply later" };
		foreach (UIElement element in new UIElement[] { addType, add, remove, up, down })
			buttons.Children.Add(element);
		left.Children.Add(buttons);
		DockPanel.SetDock(left, Dock.Left);
		body.Children.Add(left);
		body.Children.Add(fields);
		root.Children.Add(body);
		var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 70, Margin = new Thickness(0, 0, 6, 0) };
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 70 };
		footer.Children.Add(ok);
		footer.Children.Add(cancel);
		root.Children.Add(footer);
		Content = root;

		ok.Click += (_, _) => DialogResult = true;
		add.Click += (_, _) => { if (addType.SelectedItem is string type) model.Add(type); Refresh(); };
		remove.Click += (_, _) => { model.Remove(model.SelectedIndex); Refresh(); };
		up.Click += (_, _) => { model.Move(model.SelectedIndex, -1); Refresh(); };
		down.Click += (_, _) => { model.Move(model.SelectedIndex, 1); Refresh(); };
		list.SelectionChanged += (_, _) => { if (!syncing) { model.Select(list.SelectedIndex); RefreshFields(); } };
		Refresh();
	}

	void Refresh()
	{
		RefreshList();
		RefreshFields();
	}

	void RefreshList()
	{
		syncing = true;
		try {
			list.ItemsSource = model.Operations.Select(TransformEditModel.Describe).ToList();
			list.SelectedIndex = model.SelectedIndex;
		} finally {
			syncing = false;
		}
	}

	void RefreshFields()
	{
		fields.Children.Clear();
		if (model.SelectedIndex < 0)
			return;
		var index = model.SelectedIndex;
		foreach (var field in TransformEditModel.Fields(model.Operations[index].Type)) {
			fields.Children.Add(new TextBlock { Text = field, Margin = new Thickness(0, 4, 0, 2) });
			var box = new TextBox { Text = model.GetField(index, field).ToString("0.###", CultureInfo.InvariantCulture) };
			box.LostKeyboardFocus += (_, _) => {
				// Only the list text is refreshed: rebuilding these boxes during a focus change would destroy the one being tabbed into.
				if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && model.SetField(index, field, value))
					RefreshList();
				else
					box.Text = model.GetField(index, field).ToString("0.###", CultureInfo.InvariantCulture);
			};
			fields.Children.Add(box);
		}
	}
}
