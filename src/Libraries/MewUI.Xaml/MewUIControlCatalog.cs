namespace LeXtudio.MewUI.Xaml;

/// <summary>
/// The verified Aprillz.MewUI 0.22.0 control surface, mirrored as the MXAML property registry.
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
		["Button"] = new(StringComparer.Ordinal) { ["Content"] = MxamlPropertyKind.ContentText },
		["TextBox"] = new(StringComparer.Ordinal) { ["Text"] = MxamlPropertyKind.String },
		// 0.22: CheckBox/RadioButton are content controls; "Text" (the attribute existing documents
		// use) and "Content" both mean the caption.
		["CheckBox"] = new(StringComparer.Ordinal)
		{
			["Text"] = MxamlPropertyKind.ContentText,
			["Content"] = MxamlPropertyKind.ContentText,
			["IsChecked"] = MxamlPropertyKind.Boolean,
		},
		["RadioButton"] = new(StringComparer.Ordinal)
		{
			["Text"] = MxamlPropertyKind.ContentText,
			["Content"] = MxamlPropertyKind.ContentText,
			["IsChecked"] = MxamlPropertyKind.Boolean,
		},
		["ComboBox"] = new(StringComparer.Ordinal)
		{
			["SelectedIndex"] = MxamlPropertyKind.Int32,
			["Placeholder"] = MxamlPropertyKind.String,
			["ZebraStriping"] = MxamlPropertyKind.Boolean,
			["ChangeOnWheel"] = MxamlPropertyKind.Boolean,
			["ItemHeight"] = MxamlPropertyKind.Double,
		},
		// Writable public properties of Aprillz.MewUI 0.22.0 (the version the designer host and the
		// fixture use), checked by reflection.
		["ListBox"] = new(StringComparer.Ordinal)
		{
			["SelectedIndex"] = MxamlPropertyKind.Int32,
			["ZebraStriping"] = MxamlPropertyKind.Boolean,
			["ItemHeight"] = MxamlPropertyKind.Double,
		},
		// Not a FrameworkElement (a plain MewObject): its own properties only.
		["TabItem"] = new(StringComparer.Ordinal) { ["IsEnabled"] = MxamlPropertyKind.Boolean, ["HeaderText"] = MxamlPropertyKind.String },
		["Slider"] = new(StringComparer.Ordinal)
		{
			["Minimum"] = MxamlPropertyKind.Double,
			["Maximum"] = MxamlPropertyKind.Double,
			["Value"] = MxamlPropertyKind.Double,
			["SmallChange"] = MxamlPropertyKind.Double,
			["LargeChange"] = MxamlPropertyKind.Double,
			["ChangeOnWheel"] = MxamlPropertyKind.Boolean,
		},
		["ProgressBar"] = new(StringComparer.Ordinal)
		{
			["Minimum"] = MxamlPropertyKind.Double,
			["Maximum"] = MxamlPropertyKind.Double,
			["Value"] = MxamlPropertyKind.Double,
			["IsIndeterminate"] = MxamlPropertyKind.Boolean,
		},
	};

	/// <summary>Catalogued types that take none of <see cref="commonProperties"/>: Window keeps its own
	/// list (it IS a FrameworkElement, but sizes through WindowSize); TabItem is a plain MewObject.</summary>
	static readonly HashSet<string> excludedFromCommon = new(StringComparer.Ordinal) { "Window", "TabItem" };

	/// <summary>FrameworkElement/UIElement properties every catalogued control inherits in 0.22.0.
	/// Margin/Padding (Thickness) and colours are not scalar kinds yet.</summary>
	static readonly Dictionary<string, MxamlPropertyKind> commonProperties = new(StringComparer.Ordinal)
	{
		["MinWidth"] = MxamlPropertyKind.Double,
		["MinHeight"] = MxamlPropertyKind.Double,
		["MaxWidth"] = MxamlPropertyKind.Double,
		["MaxHeight"] = MxamlPropertyKind.Double,
		["Opacity"] = MxamlPropertyKind.Double,
		["IsEnabled"] = MxamlPropertyKind.Boolean,
		["IsVisible"] = MxamlPropertyKind.Boolean,
		["IsHitTestVisible"] = MxamlPropertyKind.Boolean,
		["HorizontalAlignment"] = MxamlPropertyKind.Enum,
		["VerticalAlignment"] = MxamlPropertyKind.Enum,
	};

	static readonly HashSet<string> panelTypes = new(StringComparer.Ordinal)
	{
		"StackPanel", "DockPanel", "WrapPanel", "Grid", "Canvas",
	};
	static readonly HashSet<string> contentTypes = new(StringComparer.Ordinal)
	{
		"Window", "GroupBox", "TabControl", "TabItem", "ContentControl", "ScrollViewer",
	};
	static readonly HashSet<string> borderTypes = new(StringComparer.Ordinal) { "Border" };
	static readonly HashSet<string> containerTypes =
		panelTypes.Concat(contentTypes).Concat(borderTypes).ToHashSet(StringComparer.Ordinal);

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
	/// <summary>"Children()" extension target (real Panel subclass).</summary>
	public static bool IsPanel(string type) => panelTypes.Contains(type);
	/// <summary>Single-child Content assignment (ContentControl family).</summary>
	public static bool IsContentHost(string type) => contentTypes.Contains(type) || type == "Border";
	/// <summary>How a parent accepts children: Children / Content / Child.</summary>
	public static string ContainmentMode(string parentType)
	{
		if (panelTypes.Contains(parentType)) return "Children";
		if (contentTypes.Contains(parentType)) return "Content";
		if (borderTypes.Contains(parentType)) return "Child";
		return "";
	}
	public static bool IsKnownEvent(string eventName) => events.Contains(eventName);
	static readonly HashSet<string> stringItemTypes = new(StringComparer.Ordinal) { "ComboBox", "ListBox" };
	/// <summary>Types that take a plain string list as <c>&lt;Item&gt;</c> children, generated as the
	/// runtime's <c>.Items(params string[])</c> extension.</summary>
	public static bool HasStringItems(string type) => stringItemTypes.Contains(type);

	/// <summary>Registry kind for a property on a type. Unlisted combinations return
	/// <see cref="MxamlPropertyKind.Unsupported"/>: the generator emits a comment instead of
	/// guessing a literal kind (the M-2 failure mode), keeping generated code compilable while
	/// surfacing an actionable diagnostic.</summary>
	public static MxamlPropertyKind KindOf(string type, string property)
		=> property is "Canvas.Left" or "Canvas.Top"
			|| property is "Width" or "Height" && (type == "Window" || IsKnownType(type) && !excludedFromCommon.Contains(type))
			? MxamlPropertyKind.Double
			: propertiesByType.TryGetValue(type, out var props) && props.TryGetValue(property, out var kind)
			? kind
			: VisualKind(type, property) is { } visual
			? visual
			: !excludedFromCommon.Contains(type) && IsKnownType(type) && commonProperties.TryGetValue(property, out var common)
			? common
			: MxamlPropertyKind.Unsupported;

	/// <summary>Margin on every FrameworkElement; Padding on everything but Image and Separator; the
	/// Control-level visuals on every control except the panels, Image and Separator (0.22.0).</summary>
	static MxamlPropertyKind? VisualKind(string type, string property)
	{
		var isControl = type == "Window" || IsKnownType(type) && !excludedFromCommon.Contains(type);
		if (!isControl) return null;
		var bare = type is "Image" or "Separator";
		return property switch {
			"Margin" when type != "Window" => MxamlPropertyKind.Thickness,
			"Padding" when !bare => MxamlPropertyKind.Thickness,
			"Background" or "Foreground" or "BorderBrush" when !bare && !panelTypes.Contains(type) => MxamlPropertyKind.Color,
			"CornerRadius" or "BorderThickness" when !bare && !panelTypes.Contains(type) => MxamlPropertyKind.Double,
			_ => null
		};
	}

	/// <summary>The named colours of Aprillz.MewUI 0.22.0's <c>Color</c> (static properties, by
	/// reflection); any other name has no source form.</summary>
	public static readonly IReadOnlySet<string> ColorNames = new HashSet<string>(StringComparer.Ordinal) {
		"Transparent", "Black", "Blue", "Cyan", "DarkGray", "Fuchsia", "Gray", "Green", "LightGray", "Magenta", "Maroon", "Orange", "Purple", "Red", "White", "Yellow", "AliceBlue", "AntiqueWhite", "Aqua", "Aquamarine", "Azure", "Beige", "Bisque", "BlanchedAlmond", "BlueViolet", "Brown", "BurlyWood", "CadetBlue", "Chartreuse", "Chocolate", "Coral", "CornflowerBlue", "Cornsilk", "Crimson", "DarkBlue", "DarkCyan", "DarkGoldenrod", "DarkGreen", "DarkKhaki", "DarkMagenta", "DarkOliveGreen", "DarkOrange", "DarkOrchid", "DarkRed", "DarkSalmon", "DarkSeaGreen", "DarkSlateBlue", "DarkSlateGray", "DarkTurquoise", "DarkViolet", "DeepPink", "DeepSkyBlue", "DimGray", "DodgerBlue", "Firebrick", "FloralWhite", "ForestGreen", "Gainsboro", "GhostWhite", "Gold", "Goldenrod", "GreenYellow", "Honeydew", "HotPink", "IndianRed", "Indigo", "Ivory", "Khaki", "Lavender", "LavenderBlush", "LawnGreen", "LemonChiffon", "LightBlue", "LightCoral", "LightCyan", "LightGoldenrodYellow", "LightGreen", "LightPink", "LightSalmon", "LightSeaGreen", "LightSkyBlue", "LightSlateGray", "LightSteelBlue", "LightYellow", "Lime", "LimeGreen", "Linen", "MediumAquamarine", "MediumBlue", "MediumOrchid", "MediumPurple", "MediumSeaGreen", "MediumSlateBlue", "MediumSpringGreen", "MediumTurquoise", "MediumVioletRed", "MidnightBlue", "MintCream", "MistyRose", "Moccasin", "NavajoWhite", "Navy", "OldLace", "Olive", "OliveDrab", "OrangeRed", "Orchid", "PaleGoldenrod", "PaleGreen", "PaleTurquoise", "PaleVioletRed", "PapayaWhip", "PeachPuff", "Peru", "Pink", "Plum", "PowderBlue", "RebeccaPurple", "RosyBrown", "RoyalBlue", "SaddleBrown", "Salmon", "SandyBrown", "SeaGreen", "SeaShell", "Sienna", "Silver", "SkyBlue", "SlateBlue", "SlateGray", "Snow", "SpringGreen", "SteelBlue", "Tan", "Teal", "Thistle", "Tomato", "Turquoise", "Violet", "Wheat", "WhiteSmoke", "YellowGreen", "Grey", "DarkGrey", "LightGrey" };

	/// <summary>The four Thickness components for "4", "4,8" (horizontal, vertical) or "1,2,3,4", or null.</summary>
	public static double[]? ParseThickness(string value)
	{
		var parts = (value ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length is not (1 or 2 or 4)) return null;
		var numbers = new double[parts.Length];
		for (var i = 0; i < parts.Length; i++)
			if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
				return null;
		return numbers;
	}

	/// <summary>A colour value's ARGB for "#RRGGBB"/"#AARRGGBB", or null (a name is checked separately).</summary>
	public static uint? ParseHexColor(string value)
	{
		if (value is not { Length: 7 or 9 } || value[0] != '#'
			|| !uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var argb))
			return null;
		return value.Length == 7 ? 0xFF000000u | argb : argb;
	}

	public static bool IsValidColor(string value) => ParseHexColor(value) != null || ColorNames.Contains(value ?? "");

	public static bool IsSupported(string type, string property)
		=> KindOf(type, property) != MxamlPropertyKind.Unsupported;

	/// <summary>Finite source tokens for a catalogued enum. This is deliberately source-facing:
	/// MXAML emits <c>Property.Value</c>, so the designer must not expose CLR display names or
	/// fabricate a value it cannot round-trip.</summary>
	public static IReadOnlyList<string> EnumValuesOf(string type, string property)
		=> KindOf(type, property) != MxamlPropertyKind.Enum ? Array.Empty<string>()
			: property switch {
				"Orientation" => new[] { "Horizontal", "Vertical" },
				"HorizontalAlignment" => new[] { "Left", "Center", "Right", "Stretch" },
				"VerticalAlignment" => new[] { "Top", "Center", "Bottom", "Stretch" },
				_ => Array.Empty<string>()
			};
}
