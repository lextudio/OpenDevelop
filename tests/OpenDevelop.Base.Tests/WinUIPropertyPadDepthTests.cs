using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Property-pad depth: an unset property shows the value the renderer reports (not a blank cell),
/// the renderer's catalog picks the editor (Brush, CornerRadius, FontFamily), and a child of a Grid
/// or Canvas is offered its parent's attached layout properties.
/// </summary>
public sealed class WinUIPropertyPadDepthTests
{
	const string GridSource =
		"<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
		"<Button x:Name=\"B\" /></Grid>";

	const string CanvasSource =
		"<Canvas xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
		"<Button x:Name=\"B\" /></Canvas>";

	static (WinUIXamlElementPropertyAdapter Adapter, List<(string Name, string Value)> Writes) Create(
		string source, Func<XElement, IReadOnlyList<DesignerPropertyInfo>> runtime = null)
	{
		var document = XDocument.Parse(source);
		var element = document.Descendants().First(e => (string)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "B");
		var writes = new List<(string, string)>();
		var adapter = new WinUIXamlElementPropertyAdapter(element, document.Root,
			(e, name, value) => writes.Add((name.LocalName, value)), runtimeProperties: runtime);
		return (adapter, writes);
	}

	static PropertyDescriptor Property(WinUIXamlElementPropertyAdapter adapter, string name)
		=> adapter.GetProperties().Cast<PropertyDescriptor>().FirstOrDefault(property => property.DisplayName == name);

	static IReadOnlyList<DesignerPropertyInfo> Catalog(params DesignerPropertyInfo[] properties) => properties;

	static DesignerPropertyInfo Info(string name, string typeName, string value, string kind = "String", string category = "Misc")
		=> new() { Name = name, TypeName = typeName, Value = value, Kind = kind, Category = category };

	[Xunit.UIFactAttribute]
	public void An_unset_property_shows_the_value_the_renderer_reports()
	{
		var (adapter, _) = Create(GridSource, _ => Catalog(Info("FontSize", "Double", "14", "Number", "Text")));

		var property = Property(adapter, "FontSize");

		Assert.NotNull(property);
		Assert.Equal("14", property.GetValue(null));
		Assert.Contains("current value from the renderer", property.Description);
	}

	[Xunit.UIFactAttribute]
	public void A_catalog_brush_property_gets_a_color_editor_and_the_renderers_value()
	{
		var (adapter, writes) = Create(GridSource, _ => Catalog(Info("Background", "Brush", "#FF4488FF", "Brush", "Appearance")));

		var property = Property(adapter, "Background");

		Assert.Equal(typeof(Color), property.PropertyType);
		Assert.Equal("ColorValueConverter", property.Converter.GetType().Name);
		var color = Assert.IsType<Color>(property.GetValue(null));
		Assert.Equal(Color.FromArgb(0xFF, 0x44, 0x88, 0xFF), color);

		property.SetValue(null, Colors.Red);
		Assert.Contains(writes, write => write.Name == "Background" && write.Value == "#FFFF0000");
	}

	[Xunit.UIFactAttribute]
	public void CornerRadius_uses_its_own_converter_and_round_trips()
	{
		var (adapter, writes) = Create(GridSource, _ => Catalog(Info("CornerRadius", "CornerRadius", "8,4,8,4", "Thickness")));

		var property = Property(adapter, "CornerRadius");

		Assert.Equal(typeof(CornerRadius), property.PropertyType);
		var radius = Assert.IsType<CornerRadius>(property.GetValue(null));
		Assert.Equal(new CornerRadius(8, 4, 8, 4), radius);
		Assert.Equal("8,4,8,4", property.Converter.ConvertToString(radius));
		Assert.Equal("2", property.Converter.ConvertToString(new CornerRadius(2)));

		property.SetValue(null, new CornerRadius(2));
		Assert.Contains(writes, write => write.Name == "CornerRadius" && write.Value == "2");
	}

	[Xunit.UIFactAttribute]
	public void FontFamily_offers_the_installed_families_without_excluding_any()
	{
		var (adapter, _) = Create(GridSource, _ => Catalog(Info("FontFamily", "FontFamily", "Segoe UI")));

		var converter = Property(adapter, "FontFamily").Converter;

		Assert.Equal("FontFamilyValueConverter", converter.GetType().Name);
		Assert.False(converter.GetStandardValuesExclusive(null));
		if (converter.GetStandardValuesSupported(null)) {
			var values = converter.GetStandardValues(null);
			Assert.NotNull(values);
			Assert.NotEmpty(values.Cast<object>());
		}
	}

	[Xunit.UIFactAttribute]
	public void A_Grid_child_is_offered_the_parents_attached_properties_and_writes_them()
	{
		var (adapter, writes) = Create(GridSource);

		foreach (var name in new[] { "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan" }) {
			var property = Property(adapter, name);
			Assert.NotNull(property);
			Assert.Equal("Layout", property.Category);
			Assert.Equal(name == "Grid.Row" || name == "Grid.Column" ? "0" : "1", property.GetValue(null));
		}

		Property(adapter, "Grid.Row").SetValue(null, "1");
		Assert.Contains(writes, write => write.Name == "Grid.Row" && write.Value == "1");
	}

	[Xunit.UIFactAttribute]
	public void A_Canvas_child_is_offered_its_offsets()
	{
		var (adapter, writes) = Create(CanvasSource);

		Assert.Equal("0", Property(adapter, "Canvas.Left").GetValue(null));
		Assert.Equal("0", Property(adapter, "Canvas.Top").GetValue(null));
		Assert.Null(Property(adapter, "Grid.Row"));

		Property(adapter, "Canvas.Left").SetValue(null, "24");
		Assert.Contains(writes, write => write.Name == "Canvas.Left" && write.Value == "24");
	}
}
