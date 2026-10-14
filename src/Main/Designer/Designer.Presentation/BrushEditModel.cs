using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// The state behind the brush editor dialog, modelled on Visual Studio's brush editor: a brush
/// kind (no brush, solid, linear or radial gradient), one color editor that edits the solid color
/// or the selected gradient stop, the gradient stops themselves, and the advanced settings. It
/// starts from the host's <see cref="DesignerBrushInfo"/> and produces one for
/// <c>design/set-brush</c>.
/// </summary>
public sealed class BrushEditModel
{
	public const string NullKind = "Null";
	public const string SolidKind = "Solid";
	public const string LinearKind = "LinearGradient";
	public const string RadialKind = "RadialGradient";
	public const string ResourceKind = "Resource";
	/// <summary>A <c>{DynamicResource {x:Static SystemColors.XBrushKey}}</c> reference; its
	/// <see cref="ResourceKey"/> is the SystemColors member name.</summary>
	public const string SystemKind = "System";
	/// <summary>How the resources tab lists a system brush, beside the document's own keys.</summary>
	public const string SystemEntryPrefix = "SystemColors.";

	/// <summary>The SystemColors brush keys (<c>ControlBrushKey</c>, ...), in name order.</summary>
	public static IReadOnlyList<string> SystemResourceKeys { get; } = typeof(System.Windows.SystemColors)
		.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
		.Where(property => property.PropertyType == typeof(System.Windows.ResourceKey) && property.Name.EndsWith("BrushKey", StringComparison.Ordinal))
		.Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

	public sealed class Stop
	{
		public Color Color { get; set; }
		public double Offset { get; set; }
	}

	readonly string initial;
	readonly List<Stop> stops = new();
	Color solidColor;

	public BrushEditModel(DesignerBrushInfo? brush, IReadOnlyList<string>? resourceKeys = null)
	{
		brush ??= new DesignerBrushInfo { Kind = NullKind };
		Kind = brush.Kind;
		ResourceKey = brush.ResourceKey;
		ResourceKeys = resourceKeys ?? Array.Empty<string>();
		solidColor = ParseColorOrDefault(brush.Color, Colors.Black);
		Opacity = brush.Opacity;
		(StartX, StartY, EndX, EndY) = (brush.StartX, brush.StartY, brush.EndX, brush.EndY);
		(CenterX, CenterY, OriginX, OriginY, RadiusX, RadiusY) = (brush.CenterX, brush.CenterY, brush.OriginX, brush.OriginY, brush.RadiusX, brush.RadiusY);
		MappingMode = brush.MappingMode;
		SpreadMethod = brush.SpreadMethod;
		stops.AddRange(brush.Stops.Select(stop => new Stop { Color = ParseColorOrDefault(stop.Color, Colors.Black), Offset = stop.Offset }));
		SelectedStop = stops.Count > 0 ? 0 : -1;
		initial = Serialize(ToInfo());
	}

	public string Kind { get; private set; }
	/// <summary>The in-scope Brush resources the resources tab offers.</summary>
	public IReadOnlyList<string> ResourceKeys { get; }
	/// <summary>For a resource brush, the referenced key.</summary>
	public string ResourceKey { get; private set; }
	/// <summary>The resources tab's entries: the document's Brush resources, then the system brushes.</summary>
	public IReadOnlyList<string> ResourceEntries => ResourceKeys.Concat(SystemResourceKeys.Select(key => SystemEntryPrefix + key)).ToArray();
	/// <summary>The resources tab entry for the current brush, or null when it is not a resource.</summary>
	public string? SelectedResourceEntry => Kind == ResourceKind ? ResourceKey : Kind == SystemKind ? SystemEntryPrefix + ResourceKey : null;
	public bool IsGradient => Kind is LinearKind or RadialKind;
	public IReadOnlyList<Stop> Stops => stops;
	public int SelectedStop { get; private set; }
	public double Opacity { get; set; }
	public double StartX { get; set; }
	public double StartY { get; set; }
	public double EndX { get; set; }
	public double EndY { get; set; }
	public double CenterX { get; set; }
	public double CenterY { get; set; }
	public double OriginX { get; set; }
	public double OriginY { get; set; }
	public double RadiusX { get; set; }
	public double RadiusY { get; set; }
	public string MappingMode { get; set; }
	public string SpreadMethod { get; set; }

	/// <summary>Whether the edited brush differs from the one the editor opened with.</summary>
	public bool IsChanged => Serialize(ToInfo()) != initial;

	/// <summary>The color the color editor shows and edits: the solid color, or the selected
	/// gradient stop's color. Setting it on "no brush" turns the brush solid, as picking a color does
	/// in Visual Studio.</summary>
	public Color SelectedColor {
		get => IsGradient && SelectedStop >= 0 ? stops[SelectedStop].Color : solidColor;
		set {
			if (IsGradient && SelectedStop >= 0) {
				stops[SelectedStop].Color = value;
				return;
			}
			solidColor = value;
			if (Kind is NullKind or ResourceKind or SystemKind) {
				Kind = SolidKind;
				ResourceKey = "";
			}
		}
	}

	/// <summary>Switches the brush kind. Entering a gradient from a solid (or no) brush starts a
	/// two-stop gradient from the current color to white (black when the color is white); leaving a
	/// gradient for a solid brush keeps the selected stop's color.</summary>
	public void SetKind(string kind)
	{
		if (kind is not (NullKind or SolidKind or LinearKind or RadialKind))
			throw new ArgumentException("Unknown brush kind: " + kind, nameof(kind));
		if (Kind is ResourceKind or SystemKind)
			ResourceKey = "";
		if (kind == Kind)
			return;
		var color = SelectedColor;
		if (kind is LinearKind or RadialKind && stops.Count == 0) {
			stops.Add(new Stop { Color = color, Offset = 0 });
			stops.Add(new Stop { Color = color == Colors.White ? Colors.Black : Colors.White, Offset = 1 });
			SelectedStop = 0;
		}
		if (kind == SolidKind && IsGradient)
			solidColor = color;
		Kind = kind;
	}

	/// <summary>Makes the brush a <c>{StaticResource key}</c> reference to one of
	/// <see cref="ResourceKeys"/> (Visual Studio's brush resources tab).</summary>
	public bool SelectResource(string key)
	{
		if (!ResourceKeys.Contains(key))
			return false;
		Kind = ResourceKind;
		ResourceKey = key;
		return true;
	}

	/// <summary>Makes the brush a system brush (<c>{DynamicResource {x:Static SystemColors.key}}</c>).</summary>
	public bool SelectSystemResource(string key)
	{
		if (!SystemResourceKeys.Contains(key))
			return false;
		Kind = SystemKind;
		ResourceKey = key;
		return true;
	}

	/// <summary>Selects one of <see cref="ResourceEntries"/>: a document key wins over the system
	/// form, so a resource literally keyed "SystemColors.X" still selects that resource.</summary>
	public bool SelectResourceEntry(string entry)
		=> SelectResource(entry)
			|| entry.StartsWith(SystemEntryPrefix, StringComparison.Ordinal) && SelectSystemResource(entry.Substring(SystemEntryPrefix.Length));

	/// <summary>Adds a stop at <paramref name="offset"/> with the color the gradient has there, as
	/// clicking the gradient slider does, and selects it. Returns its index.</summary>
	public int AddStop(double offset)
	{
		if (!IsGradient)
			throw new InvalidOperationException("Only a gradient has stops.");
		offset = Clamp(offset);
		var stop = new Stop { Color = ColorAt(offset), Offset = offset };
		var index = stops.FindIndex(existing => existing.Offset > offset);
		if (index < 0)
			index = stops.Count;
		stops.Insert(index, stop);
		SelectedStop = index;
		return index;
	}

	/// <summary>Removes a stop (dragging it off the slider); a gradient keeps at least two.</summary>
	public bool RemoveStop(int index)
	{
		if (!IsGradient || index < 0 || index >= stops.Count || stops.Count <= 2)
			return false;
		stops.RemoveAt(index);
		SelectedStop = Math.Min(index, stops.Count - 1);
		return true;
	}

	public bool SelectStop(int index)
	{
		if (index < 0 || index >= stops.Count)
			return false;
		SelectedStop = index;
		return true;
	}

	public bool SelectNextStop() => SelectStop(SelectedStop + 1);
	public bool SelectPreviousStop() => SelectStop(SelectedStop - 1);

	/// <summary>Moves a stop (dragging it along the slider or typing its offset); the stop list
	/// stays in offset order and the moved stop stays selected.</summary>
	public bool SetStopOffset(int index, double offset)
	{
		if (index < 0 || index >= stops.Count || double.IsNaN(offset))
			return false;
		var stop = stops[index];
		stop.Offset = Clamp(offset);
		stops.RemoveAt(index);
		var target = stops.FindIndex(existing => existing.Offset > stop.Offset);
		if (target < 0)
			target = stops.Count;
		stops.Insert(target, stop);
		SelectedStop = target;
		return true;
	}

	/// <summary>Visual Studio's "Reverse gradient stops".</summary>
	public void Reverse()
	{
		foreach (var stop in stops)
			stop.Offset = 1 - stop.Offset;
		stops.Reverse();
		if (SelectedStop >= 0)
			SelectedStop = stops.Count - 1 - SelectedStop;
	}

	/// <summary>The gradient's color at an offset, interpolated between the surrounding stops.</summary>
	public Color ColorAt(double offset)
	{
		if (stops.Count == 0)
			return solidColor;
		var ordered = stops.OrderBy(stop => stop.Offset).ToList();
		if (offset <= ordered[0].Offset)
			return ordered[0].Color;
		for (var i = 1; i < ordered.Count; i++) {
			if (offset > ordered[i].Offset)
				continue;
			var (a, b) = (ordered[i - 1], ordered[i]);
			var t = b.Offset == a.Offset ? 0 : (offset - a.Offset) / (b.Offset - a.Offset);
			byte Lerp(byte from, byte to) => (byte)Math.Round(from + (to - from) * t);
			return Color.FromArgb(Lerp(a.Color.A, b.Color.A), Lerp(a.Color.R, b.Color.R), Lerp(a.Color.G, b.Color.G), Lerp(a.Color.B, b.Color.B));
		}
		return ordered[^1].Color;
	}

	public DesignerBrushInfo ToInfo()
	{
		var info = new DesignerBrushInfo { Kind = Kind };
		if (Kind == NullKind)
			return info;
		if (Kind is ResourceKind or SystemKind) {
			info.ResourceKey = ResourceKey;
			return info;
		}
		info.Opacity = Opacity;
		if (Kind == SolidKind) {
			info.Color = FormatColor(solidColor);
			return info;
		}
		(info.StartX, info.StartY, info.EndX, info.EndY) = (StartX, StartY, EndX, EndY);
		(info.CenterX, info.CenterY, info.OriginX, info.OriginY, info.RadiusX, info.RadiusY) = (CenterX, CenterY, OriginX, OriginY, RadiusX, RadiusY);
		info.MappingMode = MappingMode;
		info.SpreadMethod = SpreadMethod;
		info.Stops = stops.Select(stop => new DesignerGradientStopInfo { Color = FormatColor(stop.Color), Offset = stop.Offset }).ToList();
		return info;
	}

	/// <summary>The WPF brush the model describes, for the dialog's previews; null for no brush.</summary>
	public Brush? ToPreviewBrush()
	{
		Brush? brush = Kind switch {
			SolidKind => new SolidColorBrush(solidColor),
			LinearKind => new LinearGradientBrush(PreviewStops(), new System.Windows.Point(StartX, StartY), new System.Windows.Point(EndX, EndY)),
			RadialKind => new RadialGradientBrush(PreviewStops()) {
				Center = new System.Windows.Point(CenterX, CenterY), GradientOrigin = new System.Windows.Point(OriginX, OriginY),
				RadiusX = RadiusX, RadiusY = RadiusY
			},
			// A swatch only: the IDE's own system colors, standing in for the designed app's.
			SystemKind when ResourceKey.EndsWith("Key", StringComparison.Ordinal) => typeof(System.Windows.SystemColors)
				.GetProperty(ResourceKey.Substring(0, ResourceKey.Length - "Key".Length), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) is Brush system ? system.Clone() : null,
			_ => null
		};
		if (brush is GradientBrush gradient) {
			gradient.MappingMode = Enum.TryParse<BrushMappingMode>(MappingMode, out var mapping) ? mapping : BrushMappingMode.RelativeToBoundingBox;
			gradient.SpreadMethod = Enum.TryParse<GradientSpreadMethod>(SpreadMethod, out var spread) ? spread : GradientSpreadMethod.Pad;
		}
		if (brush != null)
			brush.Opacity = Opacity;
		return brush;
	}

	GradientStopCollection PreviewStops() => new(stops.Select(stop => new GradientStop(stop.Color, stop.Offset)));

	public static string FormatColor(Color color) => color.ToString(CultureInfo.InvariantCulture);

	/// <summary>Parses <c>#AARRGGBB</c>, <c>#RRGGBB</c>, <c>#ARGB</c>, <c>#RGB</c> or a color name.</summary>
	public static bool TryParseColor(string? text, out Color color)
	{
		color = default;
		if (string.IsNullOrWhiteSpace(text))
			return false;
		try {
			color = (Color)ColorConverter.ConvertFromString(text.Trim());
			return true;
		} catch (FormatException) {
			return false;
		}
	}

	static Color ParseColorOrDefault(string? text, Color fallback) => TryParseColor(text, out var color) ? color : fallback;

	static double Clamp(double offset) => Math.Max(0, Math.Min(1, offset));

	static string Serialize(DesignerBrushInfo info) => JsonSerializer.Serialize(info);
}
