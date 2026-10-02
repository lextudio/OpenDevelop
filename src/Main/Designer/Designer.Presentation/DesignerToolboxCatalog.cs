// The framework-neutral half of the Toolbox classification: general control concepts and the glyph
// each one is drawn with. Three layers, each with a different job and a different owner.
//
//   Category       "Inputs"          groups the rows. Carries no icon of its own.
//   GeneralControl "Button" + glyph  the portable concept. THIS is the only layer that owns an icon.
//   Mapping        "GtkEntry" ->     framework type name -> concept. Owned by the framework's addin.
//
// The icon belongs to the middle layer because a category is not a thing you can draw, while a
// Button looks the same in every framework. Splitting the mapping out is what lets WPF's Button and
// GTK's GtkEntry both land on the same glyph without Core knowing that GTK spells it "GtkEntry" -
// a spelling that changes with the framework, and therefore belongs beside the framework.
//
// A framework with real icons of its own - WinForms reads them out of its own assembly - shows those
// instead of the concept's. See DesignerTypeIcons, whose per-framework resolver is consulted before
// this table.

using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>A portable control concept: the layer that owns an icon.</summary>
	public sealed record DesignerGeneralControl(string Name, string IconName);

	/// <summary>A group of general controls. Carries no icon.</summary>
	public sealed record DesignerControlCategory(string Name, params DesignerGeneralControl[] Controls);

	/// <summary>A framework type name and the concept it is. Supplied by the framework's addin.</summary>
	public sealed record DesignerTypeMapping(string TypeName, string ControlName);

	/// <summary>Maps a framework's control types onto the shared concepts, so that a framework
	/// supplying neither icons nor categories of its own still gets readable rows.</summary>
	public interface IDesignerControlMapper
	{
		/// <summary>This mapper's framework, for diagnostics.</summary>
		string FrameworkName { get; }

		/// <summary>The concept a framework type is, or null when this framework does not claim it.</summary>
		string? GetConcept(string typeName);

		/// <summary>The framework type's own category, or null to use the shared one.</summary>
		string? GetCategory(string typeName);
	}

	/// <summary>The shared control concepts and their glyphs. Contains no framework type names: those
	/// live in each framework's addin, which is the only place that can keep them current.</summary>
	public static class DesignerToolboxCatalog
	{
		/// <summary>The category for a type no concept claims.</summary>
		public const string OtherCategory = "Other";

		/// <summary>The concept for a type no mapper claims.</summary>
		public const string OtherControl = "Other";

		// Icon names, verified present in the VS Image Library, so a rename there is a one-line fix.
		const string ContainerIcon = "Container";
		const string LayoutPanelIcon = "LayoutPanel";
		const string PanelIcon = "Panel";
		const string GridIcon = "Grid";
		const string TableIcon = "Table";
		const string StackPanelIcon = "StackPanel";
		const string ButtonIcon = "Button";
		const string ToggleButtonIcon = "ToggleButton";
		const string CheckBoxIcon = "CheckBoxChecked";
		const string RadioButtonIcon = "RadioButton";
		const string SplitButtonIcon = "SplitButton";
		const string LabelIcon = "Label";
		const string TextBoxIcon = "TextBox";
		const string TextAreaIcon = "TextArea";
		const string PasswordBoxIcon = "PasswordBox";
		const string ListBoxIcon = "ListBox";
		const string ListViewIcon = "ListView";
		const string TreeViewIcon = "TreeView";
		const string DataGridIcon = "DataGrid";
		const string TabIcon = "Tab";
		const string SliderIcon = "Slider";
		const string ProgressBarIcon = "ProgressBar";
		const string CalendarIcon = "Calendar";
		const string NumericIcon = "Numeric";
		const string ComboBoxIcon = "ComboBox";
		const string AutoCompleteIcon = "AutoComplete";
		const string ImageIcon = "Image";
		const string MediaIcon = "Media";
		const string MenuBarIcon = "MenuBar";
		const string MenuItemIcon = "MenuItem";
		const string ToolstripIcon = "ToolstripContainer";
		const string StatusStripIcon = "StatusStrip";
		const string DialogIcon = "Dialog";
		const string WindowIcon = "WindowScreenshot";
		const string WarningIcon = "StatusWarning";
		const string BrushIcon = "Brush";
		const string TransformIcon = "Transform";
		const string EffectIcon = "Effect";
		const string TriggerIcon = "Trigger";
		const string AnimationIcon = "Animation";
		const string BehaviorIcon = "Behavior";
		const string DataSourceIcon = "DataSource";
		const string LinkIcon = "HyperLink";
		const string RunIcon = "Run";
		const string ExpanderIcon = "Expander";
		const string ScrollViewerIcon = "ScrollViewer";

		/// <summary>The categories, in the order a Toolbox should show them: containers and layout
		/// first, because that is the order a layout is built in, then text, then input, then the
		/// rest. Names are generic so no framework's vocabulary leaks in.</summary>
		public static IReadOnlyList<DesignerControlCategory> Categories { get; } = new[] {
			new DesignerControlCategory("Containers",
				new DesignerGeneralControl("Container", ContainerIcon),
				new DesignerGeneralControl("Grid", GridIcon),
				new DesignerGeneralControl("Stack", StackPanelIcon),
				new DesignerGeneralControl("Dock", LayoutPanelIcon),
				new DesignerGeneralControl("Canvas", ContainerIcon),
				new DesignerGeneralControl("Border", GridIcon),
				new DesignerGeneralControl("Scroll", ScrollViewerIcon),
				new DesignerGeneralControl("Expander", ExpanderIcon),
				new DesignerGeneralControl("Popup", ContainerIcon)),

			new DesignerControlCategory("Layout",
				new DesignerGeneralControl("Grid Splitter", LayoutPanelIcon),
				new DesignerGeneralControl("Row Definition", TableIcon),
				new DesignerGeneralControl("Column Definition", TableIcon)),

			new DesignerControlCategory("Text",
				new DesignerGeneralControl("Label", LabelIcon),
				new DesignerGeneralControl("Text Input", TextBoxIcon),
				new DesignerGeneralControl("Multiline Text", TextAreaIcon),
				new DesignerGeneralControl("Password", PasswordBoxIcon),
				new DesignerGeneralControl("Link", LinkIcon),
				new DesignerGeneralControl("Read-only Text", LabelIcon)),

			new DesignerControlCategory("Inputs",
				new DesignerGeneralControl("Button", ButtonIcon),
				new DesignerGeneralControl("Repeat Button", ButtonIcon),
				new DesignerGeneralControl("Toggle Button", ToggleButtonIcon),
				new DesignerGeneralControl("Check Box", CheckBoxIcon),
				new DesignerGeneralControl("Radio Button", RadioButtonIcon),
				new DesignerGeneralControl("Split Button", SplitButtonIcon),
				new DesignerGeneralControl("Drop-down Button", ComboBoxIcon),
				new DesignerGeneralControl("Combo Box", ComboBoxIcon),
				new DesignerGeneralControl("Auto Complete", AutoCompleteIcon),
				new DesignerGeneralControl("List Box", ListBoxIcon),
				new DesignerGeneralControl("List View", ListViewIcon),
				new DesignerGeneralControl("Tree View", TreeViewIcon),
				new DesignerGeneralControl("Data Grid", DataGridIcon),
				new DesignerGeneralControl("Tab", TabIcon),
				new DesignerGeneralControl("Slider", SliderIcon),
				new DesignerGeneralControl("Progress", ProgressBarIcon),
				new DesignerGeneralControl("Calendar", CalendarIcon),
				new DesignerGeneralControl("Numeric", NumericIcon),
				new DesignerGeneralControl("Expander Input", ExpanderIcon)),

			new DesignerControlCategory("Media",
				new DesignerGeneralControl("Image", ImageIcon),
				new DesignerGeneralControl("Media", MediaIcon)),

			new DesignerControlCategory("Menus",
				new DesignerGeneralControl("Menu Bar", MenuBarIcon),
				new DesignerGeneralControl("Menu Item", MenuItemIcon),
				new DesignerGeneralControl("Status Bar", StatusStripIcon),
				new DesignerGeneralControl("Command Bar", ToolstripIcon)),

			new DesignerControlCategory("Windows",
				new DesignerGeneralControl("Window", WindowIcon),
				new DesignerGeneralControl("Dialog", DialogIcon),
				new DesignerGeneralControl("Notification", WarningIcon)),

			new DesignerControlCategory("Data",
				new DesignerGeneralControl("Data Source", DataSourceIcon),
				new DesignerGeneralControl("Collection", DataGridIcon)),

			new DesignerControlCategory("Drawing",
				new DesignerGeneralControl("Brush", BrushIcon),
				new DesignerGeneralControl("Transform", TransformIcon),
				new DesignerGeneralControl("Effect", EffectIcon),
				new DesignerGeneralControl("Geometry", TableIcon)),

			new DesignerControlCategory("Automation",
				new DesignerGeneralControl("Behavior", BehaviorIcon),
				new DesignerGeneralControl("Animation", AnimationIcon),
				new DesignerGeneralControl("Timer", RunIcon),
				new DesignerGeneralControl("Template", LayoutPanelIcon)),
		};

		static readonly Dictionary<string, DesignerGeneralControl> controlByName = BuildControlIndex();
		static readonly Dictionary<string, DesignerControlCategory> categoryByControl = BuildCategoryIndex();

		static Dictionary<string, DesignerGeneralControl> BuildControlIndex()
		{
			var index = new Dictionary<string, DesignerGeneralControl>(StringComparer.Ordinal);
			foreach (var category in Categories)
			foreach (var control in category.Controls)
				if (!index.ContainsKey(control.Name))
					index[control.Name] = control;
			return index;
		}

		static Dictionary<string, DesignerControlCategory> BuildCategoryIndex()
		{
			var index = new Dictionary<string, DesignerControlCategory>(StringComparer.Ordinal);
			foreach (var category in Categories)
			foreach (var control in category.Controls)
				if (!index.ContainsKey(control.Name))
					index[control.Name] = category;
			return index;
		}

		/// <summary>The concept with this name, or null.</summary>
		public static DesignerGeneralControl? GetControl(string controlName)
		{
			return controlName != null && controlByName.TryGetValue(controlName, out var control) ? control : null;
		}

		/// <summary>The shared category a concept belongs to, or <see cref="OtherCategory"/>.</summary>
		public static string GetCategory(string controlName)
		{
			return controlName != null && categoryByControl.TryGetValue(controlName, out var category)
				? category.Name : OtherCategory;
		}

		/// <summary>The glyph for a concept name, or null when there is no such concept.</summary>
		public static string? GetIconName(string controlName)
		{
			return GetControl(controlName)?.IconName;
		}

		/// <summary>Resolves a framework type to a concept through <paramref name="mapper"/>, or to
		/// nothing when the framework does not claim it. Callers fall back to the type's own name and
		/// the generic glyph - an unmapped framework is a gap in that addin's mapper, not a failure.</summary>
		public static string? ResolveControl(IDesignerControlMapper? mapper, string type)
		{
			return mapper?.GetConcept(DesignerTypeIcons.GetElementTypeName(type));
		}

		/// <summary>The category for a framework type: the framework's own when it has one, otherwise
		/// the shared category of the concept it maps to.</summary>
		public static string ResolveCategory(IDesignerControlMapper? mapper, string type)
		{
			var normalized = DesignerTypeIcons.GetElementTypeName(type);
			return mapper?.GetCategory(normalized)
				?? GetCategory(ResolveControl(mapper, type) ?? OtherControl);
		}

		/// <summary>The glyph for a framework type: the framework's real icon if it has one, otherwise
		/// the shared concept's, otherwise <paramref name="fallbackIconName"/>.</summary>
		public static string? ResolveIconName(IDesignerControlMapper? mapper, string type,
			System.Windows.Media.ImageSource? frameworkIcon, string? fallbackIconName = null)
		{
			if (frameworkIcon != null)
				return null; // the caller draws its own image; no glyph name needed
			return GetIconName(ResolveControl(mapper, type) ?? OtherControl) ?? fallbackIconName;
		}
	}
}
