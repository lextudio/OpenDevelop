namespace LeXtudio.MewUI.Xaml;

/// <summary>
/// The verified Aprillz.MewUI 0.12 control surface, mirrored as the MXAML property registry.
/// Values here decide how the C# generator emits each attribute (see MxamlPropertyKind) and
/// whether an element accepts children. Properties not listed for a known type round-trip but
/// generate a comment instead of code (compile-safe); unknown TYPES are retained with a
/// Warning diagnostic.
/// </summary>
public static class MewUIControlCatalog
{
	static readonly Dictionary<string, Dictionary<string, MxamlPropertyKind>> propertiesByType = new(StringComparer.Ordinal)
	{
		["Window"] = new(StringComparer.Ordinal)
		{
			["Title"] = MxamlPropertyKind.String,
			["Width"] = MxamlPropertyKind.Double,
			["Height"] = MxamlPropertyKind.Double,
			["MinWidth"] = MxamlPropertyKind.Double,
			["MinHeight"] = MxamlPropertyKind.Double,
			["MaxWidth"] = MxamlPropertyKind.Double,
			["MaxHeight"] = MxamlPropertyKind.Double,
			["Opacity"] = MxamlPropertyKind.Double,
		},
		["StackPanel"] = new(StringComparer.Ordinal)
		{
			["Orientation"] = MxamlPropertyKind.Enum,
			["Spacing"] = MxamlPropertyKind.Double,
		},
		["DockPanel"] = new(StringComparer.Ordinal)
		{
			["LastChildFill"] = MxamlPropertyKind.Boolean,
			["Spacing"] = MxamlPropertyKind.Double,
		},
		["Label"] = new(StringComparer.Ordinal) { ["Text"] = MxamlPropertyKind.String },
		["Button"] = new(StringComparer.Ordinal) { ["Content"] = MxamlPropertyKind.String },
		["TextBox"] = new(StringComparer.Ordinal) { ["Text"] = MxamlPropertyKind.String },
		["CheckBox"] = new(StringComparer.Ordinal)
		{
			["Text"] = MxamlPropertyKind.String,
			["IsChecked"] = MxamlPropertyKind.Boolean,
		},
		["RadioButton"] = new(StringComparer.Ordinal)
		{
			["Text"] = MxamlPropertyKind.String,
			["IsChecked"] = MxamlPropertyKind.Boolean,
		},
		["ComboBox"] = new(StringComparer.Ordinal)
		{
			["SelectedIndex"] = MxamlPropertyKind.Int32,
			["Placeholder"] = MxamlPropertyKind.String,
		},
	};

	static readonly HashSet<string> containerTypes = new(StringComparer.Ordinal)
	{
		"Window", "StackPanel", "DockPanel", "WrapPanel", "Grid", "Canvas", "ScrollViewer",
		"Border", "GroupBox", "TabControl", "TabItem", "ContentControl", "Menu",
	};

	static readonly HashSet<string> types = new(StringComparer.Ordinal)
	{
		// containers (below) plus leaf controls
		"Button", "Label", "TextBox", "CheckBox", "RadioButton", "Slider", "ProgressBar",
		"Image", "ComboBox", "ListBox", "Separator",
	};
	static HashSet<string> allTypes => new(types.Concat(containerTypes), StringComparer.Ordinal);
	static readonly HashSet<string> events = new(StringComparer.OrdinalIgnoreCase)
	{
		"Click", "CheckedChanged", "SelectionChanged", "ItemActivated", "Loaded", "Closed",
		"Activated", "Deactivated",
	};

	public static IReadOnlyCollection<string> Types => allTypes;
	public static IReadOnlyCollection<string> Events => events;

	public static bool IsKnownType(string type) => allTypes.Contains(type);
	public static bool IsContainer(string type) => containerTypes.Contains(type);
	public static bool IsKnownEvent(string eventName) => events.Contains(eventName);

	/// <summary>Registry kind for a property on a type. Unlisted combinations return
	/// <see cref="MxamlPropertyKind.Unsupported"/>: the generator emits a comment instead of
	/// guessing a literal kind (the M-2 failure mode), keeping generated code compilable while
	/// surfacing an actionable diagnostic.</summary>
	public static MxamlPropertyKind KindOf(string type, string property)
		=> propertiesByType.TryGetValue(type, out var props) && props.TryGetValue(property, out var kind)
			? kind
			: MxamlPropertyKind.Unsupported;

	public static bool IsSupported(string type, string property)
		=> KindOf(type, property) != MxamlPropertyKind.Unsupported;
}
