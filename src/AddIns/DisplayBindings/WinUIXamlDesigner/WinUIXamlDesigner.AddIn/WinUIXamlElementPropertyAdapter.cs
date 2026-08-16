using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using System.Windows.Controls;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.WinUIXamlDesigner
{
	/// <summary>
	/// Adapts a XAML element to the shared Properties pad (Xceed PropertyGrid) with typed
	/// editing: known enum attributes render as dropdowns, brush/color attributes get a
	/// color editor, and brush attributes holding a resource reference get a dropdown of
	/// the document's resource keys. Everything still lands as a source edit.
	/// The design-time events are exposed through <see cref="ICustomTypeDescriptor.GetEvents"/>
	/// plus <see cref="IPropertyGridEventSource"/> (handler names live in the element's XAML
	/// event attributes), which the grid's VS-style Events view consumes.
	/// </summary>
	sealed class WinUIXamlElementPropertyAdapter : ICustomTypeDescriptor, IPropertyGridEventSource
	{
		readonly XElement element;
		readonly XElement documentRoot;
		readonly Action<XElement, XName, string> setAttribute;

		public WinUIXamlElementPropertyAdapter(XElement element, XElement documentRoot, Action<XElement, XName, string> setAttribute)
		{
			this.element = element ?? throw new ArgumentNullException(nameof(element));
			this.documentRoot = documentRoot;
			this.setAttribute = setAttribute ?? throw new ArgumentNullException(nameof(setAttribute));
		}

		public override string ToString() => element.Name.LocalName;

		string IPropertyGridEventSource.GetEventHandler(string eventName)
			=> element.Attribute(XName.Get(eventName))?.Value ?? "";

		void IPropertyGridEventSource.SetEventHandler(string eventName, string handlerName)
		{
			var attribute = XName.Get(eventName);
			var current = element.Attribute(attribute)?.Value;
			if (string.IsNullOrEmpty(handlerName) ? current == null : current == handlerName)
				return;
			setAttribute(element, attribute, string.IsNullOrEmpty(handlerName) ? null : handlerName);
		}

		public PropertyDescriptorCollection GetProperties() => GetProperties(null);

		public PropertyDescriptorCollection GetProperties(Attribute[] attributes)
		{
			var descriptors = new List<PropertyDescriptor>();
			var resourceKeys = CollectResourceKeys();
			var present = new HashSet<string>(StringComparer.Ordinal);
			var eventNames = EventsFor(element.Name.LocalName);
			foreach (var attribute in element.Attributes()) {
				if (attribute.IsNamespaceDeclaration) continue;
				present.Add(attribute.Name.LocalName);
				// Event attributes (Click="...") live in the Events view, not the property list.
				if (eventNames.Contains(attribute.Name.LocalName))
					continue;
				descriptors.Add(new XamlAttributeDescriptor(element, attribute.Name, attribute.Value, setAttribute, resourceKeys));
			}
			// The pad lists the element's common properties even when they are not set yet,
			// so users can add them (VS-style) instead of only editing existing attributes.
			foreach (var name in CommonProperties(element.Name.LocalName))
			{
				if (present.Contains(name))
					continue;
				// Attribute names without a namespace serialize without a prefix, and the
				// XAML reader resolves them into the element's default namespace - the
				// idiomatic form, rather than a generated "p7:" prefix.
				descriptors.Add(new XamlAttributeDescriptor(element, XName.Get(name), null, setAttribute, resourceKeys, isNew: true));
			}
			return new PropertyDescriptorCollection(descriptors.ToArray(), readOnly: true);
		}

		public EventDescriptorCollection GetEvents() => GetEvents(null);

		public EventDescriptorCollection GetEvents(Attribute[] attributes)
		{
			var eventNames = EventsFor(element.Name.LocalName);
			var descriptors = new EventDescriptor[eventNames.Count];
			for (var i = 0; i < eventNames.Count; i++)
				descriptors[i] = new XamlEventDescriptor(element, XName.Get(eventNames[i]), setAttribute);
			return new EventDescriptorCollection(descriptors, readOnly: true);
		}

		static class XamlNamespaces
		{
			public static readonly XNamespace Default = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
		}

		/// <summary>Common editable properties per control type, for the add-property list.</summary>
		static IReadOnlyList<string> CommonProperties(string controlName)
		{
			if (ControlProperties.TryGetValue(controlName, out var specific))
				return specific;
			return FrameworkElementProperties;
		}

		static readonly IReadOnlyList<string> FrameworkElementProperties = new[] {
			"Margin", "Width", "Height", "HorizontalAlignment", "VerticalAlignment",
			"Visibility", "Opacity", "Background", "Foreground", "Padding"
		};

		static readonly Dictionary<string, IReadOnlyList<string>> ControlProperties = new(StringComparer.Ordinal) {
			["TextBlock"] = new[] {
				"Text", "TextWrapping", "TextAlignment", "Foreground", "FontSize", "FontFamily",
				"FontWeight", "FontStyle", "LineHeight", "Background", "Margin", "Padding",
				"HorizontalAlignment", "VerticalAlignment", "Visibility", "Opacity"
			},
			["Button"] = new[] {
				"Content", "Background", "Foreground", "BorderBrush", "BorderThickness",
				"FontSize", "FontWeight", "Padding", "Margin", "HorizontalAlignment",
				"VerticalAlignment", "Visibility", "Opacity", "IsEnabled"
			},
			["TextBox"] = new[] {
				"Text", "PlaceholderText", "FontSize", "Foreground", "Background",
				"MaxLength", "IsReadOnly", "Margin", "Padding", "HorizontalAlignment",
				"VerticalAlignment", "Visibility", "Width", "Height"
			},
			["Slider"] = new[] {
				"Minimum", "Maximum", "Value", "StepFrequency", "Orientation", "IsEnabled",
				"Margin", "Width", "HorizontalAlignment", "VerticalAlignment", "Visibility"
			},
			["Image"] = new[] {
				"Source", "Stretch", "Width", "Height", "Margin", "Opacity",
				"HorizontalAlignment", "VerticalAlignment", "Visibility"
			},
			["Grid"] = new[] {
				"Background", "Margin", "Width", "Height", "Padding", "RowSpacing",
				"ColumnSpacing", "HorizontalAlignment", "VerticalAlignment", "Visibility"
			},
			["StackPanel"] = new[] {
				"Orientation", "Spacing", "Background", "Margin", "Padding", "Width", "Height",
				"HorizontalAlignment", "VerticalAlignment", "Visibility"
			}
		};

		/// <summary>Common design-time events per control type, surfaced in the pad's VS-style
		/// Events view. The value of each is the XAML event attribute's handler name.</summary>
		static IReadOnlyList<string> EventsFor(string controlName)
		{
			if (ControlEvents.TryGetValue(controlName, out var specific))
				return specific;
			return DefaultEvents;
		}

		static readonly IReadOnlyList<string> DefaultEvents = new[] {
			"Loaded", "Tapped", "DoubleTapped", "PointerPressed", "PointerReleased"
		};

		static readonly Dictionary<string, IReadOnlyList<string>> ControlEvents = new(StringComparer.Ordinal) {
			["Button"] = new[] {
				"Click", "DoubleTapped", "Tapped", "Loaded", "PointerPressed", "PointerReleased",
				"KeyDown", "KeyUp", "GotFocus", "LostFocus"
			},
			["TextBlock"] = new[] {
				"Loaded", "Tapped", "DoubleTapped", "PointerPressed", "PointerReleased",
				"PointerEntered", "PointerExited", "GotFocus", "LostFocus"
			},
			["TextBox"] = new[] {
				"TextChanged", "LostFocus", "GotFocus", "KeyDown", "KeyUp", "Loaded", "Paste"
			},
			["ComboBox"] = new[] {
				"SelectionChanged", "DropDownOpened", "DropDownClosed", "Loaded", "GotFocus", "LostFocus"
			},
			["ListBox"] = new[] { "SelectionChanged", "Loaded", "DoubleTapped" },
			["ListView"] = new[] { "ItemClick", "SelectionChanged", "Loaded" },
			["Slider"] = new[] { "ValueChanged", "Loaded", "PointerPressed", "PointerReleased" },
			["Grid"] = new[] {
				"Loaded", "Tapped", "DoubleTapped", "PointerPressed", "PointerReleased", "SizeChanged"
			},
			["StackPanel"] = new[] {
				"Loaded", "Tapped", "DoubleTapped", "PointerPressed", "PointerReleased", "SizeChanged"
			},
			["Image"] = new[] { "Loaded", "ImageOpened", "ImageFailed", "Tapped", "DoubleTapped" },
			["ToggleSwitch"] = new[] { "Toggled", "Loaded" },
			["CheckBox"] = new[] { "Checked", "Unchecked", "Click", "Loaded" },
			["RadioButton"] = new[] { "Checked", "Unchecked", "Click", "Loaded" }
		};

		/// <summary>x:Key values from the document's ResourceDictionary(ies), for the resource picker.</summary>
		IReadOnlyList<string> CollectResourceKeys()
		{
			var keys = new List<string>();
			var root = documentRoot;
			if (root == null)
				return keys;
			foreach (var dictionary in root.DescendantsAndSelf()
				.Where(e => e.Name.LocalName == "ResourceDictionary" || e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)))
			{
				foreach (var key in dictionary.Descendants().Select(e => (string)e.Attribute(KeyName)).Where(k => !string.IsNullOrEmpty(k)))
				{
					if (!keys.Contains(key))
						keys.Add(key);
				}
			}
			return keys;
		}

		static readonly XName KeyName =
			XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");

		public AttributeCollection GetAttributes() => AttributeCollection.Empty;
		public string GetClassName() => element.Name.LocalName;
		public string GetComponentName() => (string)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ?? element.Name.LocalName;
		public TypeConverter GetConverter() => TypeDescriptor.GetConverter(typeof(object));
		public EventDescriptor GetDefaultEvent() => null;
		public PropertyDescriptor GetDefaultProperty() => null;
		public object GetEditor(Type editorBaseType) => null;
		public object GetPropertyOwner(PropertyDescriptor pd) => this;

		sealed class XamlAttributeDescriptor : PropertyDescriptor
		{
			readonly XElement element;
			readonly XName attributeName;
			readonly string currentValue;
			readonly Action<XElement, XName, string> setAttribute;
			readonly IReadOnlyList<string> resourceKeys;
			readonly bool isNew;

			public XamlAttributeDescriptor(XElement element, XName attributeName, string currentValue,
				Action<XElement, XName, string> setAttribute, IReadOnlyList<string> resourceKeys, bool isNew = false)
				: base(attributeName.LocalName, null)
			{
				this.element = element;
				this.attributeName = attributeName;
				this.currentValue = currentValue;
				this.setAttribute = setAttribute;
				this.resourceKeys = resourceKeys;
				this.isNew = isNew;
			}

			public override Type ComponentType => typeof(WinUIXamlElementPropertyAdapter);
			public override bool IsReadOnly => false;
			public override string Category => attributeName.NamespaceName.Length == 0 ? "XAML" : attributeName.NamespaceName;

			static bool IsThicknessProperty(string name)
				=> name == "Margin" || name == "Padding" || name == "BorderThickness";

			/// <summary>Enum types whose XAML value names match the CLR enum member names.</summary>
			static readonly Dictionary<string, Type> EnumMappings = new(StringComparer.Ordinal) {
				["HorizontalAlignment"] = typeof(HorizontalAlignment),
				["VerticalAlignment"] = typeof(VerticalAlignment),
				["Visibility"] = typeof(Visibility),
				["TextAlignment"] = typeof(TextAlignment),
				["TextWrapping"] = typeof(TextWrapping),
				["Orientation"] = typeof(Orientation),
				["Stretch"] = typeof(Stretch),
				["FontStyle"] = typeof(FontStyle),
				["FontWeight"] = typeof(FontWeight),
				["HorizontalContentAlignment"] = typeof(HorizontalAlignment),
				["VerticalContentAlignment"] = typeof(VerticalAlignment)
			};

			static bool IsBrushProperty(string name, string value)
				=> (name.Contains("Brush", StringComparison.Ordinal) || name.Contains("Color", StringComparison.Ordinal) || name == "Background" || name == "Foreground" || name == "BorderBrush")
					&& (value == null || !value.StartsWith("{", StringComparison.Ordinal));

			public override Type PropertyType
			{
				get
				{
					if (EnumMappings.TryGetValue(attributeName.LocalName, out var enumType))
						return enumType;
					if (IsThicknessProperty(attributeName.LocalName))
						return typeof(Thickness);
					if (IsBrushProperty(attributeName.LocalName, currentValue))
						return typeof(Color);
					return typeof(string);
				}
			}

			public override TypeConverter Converter
			{
				get
				{
					if (EnumMappings.TryGetValue(attributeName.LocalName, out var enumType))
						return new EnumValueConverter(enumType);
					if (IsThicknessProperty(attributeName.LocalName))
						return new ThicknessValueConverter();
					if (IsBrushProperty(attributeName.LocalName, currentValue))
						return new ColorValueConverter();
					return base.Converter;
				}
			}

			public override object GetValue(object component)
			{
				if (isNew)
					return null;
				if (EnumMappings.TryGetValue(attributeName.LocalName, out var enumType))
				{
					var text = element.Attribute(attributeName)?.Value;
					if (Enum.TryParse(enumType, text, ignoreCase: true, out var parsed))
						return parsed;
					return null;
				}
				if (IsThicknessProperty(attributeName.LocalName))
				{
					var text = element.Attribute(attributeName)?.Value;
					if (ThicknessValueConverter.TryParse(text, out var thickness))
						return thickness;
					return null;
				}
				if (IsBrushProperty(attributeName.LocalName, currentValue))
				{
					var text = element.Attribute(attributeName)?.Value;
					if (ColorValueConverter.TryParseColor(text, out var color))
						return color;
					return null;
				}
				return element.Attribute(attributeName)?.Value;
			}

			public override void SetValue(object component, object value)
			{
				if (EnumMappings.TryGetValue(attributeName.LocalName, out var enumType))
				{
					setAttribute(element, attributeName, value == null ? null : value.ToString());
					return;
				}
				if (IsThicknessProperty(attributeName.LocalName) && value is Thickness thickness)
				{
					setAttribute(element, attributeName, ThicknessValueConverter.Format(thickness));
					return;
				}
				if (IsBrushProperty(attributeName.LocalName, currentValue) && value is Color color)
				{
					setAttribute(element, attributeName, ColorValueConverter.FormatColor(color));
					return;
				}
				setAttribute(element, attributeName, value?.ToString());
			}

			// An explicitly-set attribute is a non-default value: it can be reset (removed),
			// which also drives the pad's default-value override indicator dot.
			public override bool CanResetValue(object component) => element.Attribute(attributeName) != null;
			public override void ResetValue(object component) => setAttribute(element, attributeName, null);
			public override bool ShouldSerializeValue(object component) => element.Attribute(attributeName) != null;
		}

		/// <summary>
		/// A design-time event, backed by the XAML event attribute (e.g. <c>Click="Button_Click"</c>).
		/// Consumed by the Properties pad's VS-style Events view: the grid lists it via
		/// <c>TypeDescriptor.GetEvents</c> and the handler name is read/written through the
		/// adapter's <see cref="IPropertyGridEventSource"/> implementation (the XAML attribute).
		/// </summary>
		sealed class XamlEventDescriptor : EventDescriptor, IPropertyGridEventTypeName
		{
			static readonly Dictionary<string, string> StandardDelegateNames = new(StringComparer.Ordinal) {
				["Click"] = "RoutedEventHandler", ["DoubleTapped"] = "DoubleTappedEventHandler",
				["Tapped"] = "TappedEventHandler", ["Loaded"] = "RoutedEventHandler",
				["PointerPressed"] = "PointerEventHandler", ["PointerReleased"] = "PointerEventHandler",
				["PointerEntered"] = "PointerEventHandler", ["PointerExited"] = "PointerEventHandler",
				["KeyDown"] = "KeyEventHandler", ["KeyUp"] = "KeyEventHandler",
				["GotFocus"] = "RoutedEventHandler", ["LostFocus"] = "RoutedEventHandler",
				["TextChanged"] = "TextChangedEventHandler", ["Paste"] = "TextControlPasteEventHandler",
				["SelectionChanged"] = "SelectionChangedEventHandler",
				["DropDownOpened"] = "EventHandler<object>", ["DropDownClosed"] = "EventHandler<object>",
				["ValueChanged"] = "RangeBaseValueChangedEventHandler", ["SizeChanged"] = "SizeChangedEventHandler",
				["ImageOpened"] = "RoutedEventHandler", ["ImageFailed"] = "ExceptionRoutedEventHandler",
				["Toggled"] = "RoutedEventHandler", ["Checked"] = "RoutedEventHandler",
				["Unchecked"] = "RoutedEventHandler", ["ItemClick"] = "ItemClickEventHandler"
			};

			readonly XElement element;
			readonly XName attributeName;
			readonly Action<XElement, XName, string> setAttribute;

			public XamlEventDescriptor(XElement element, XName attributeName, Action<XElement, XName, string> setAttribute)
				: base(attributeName.LocalName, new Attribute[] { new CategoryAttribute("Events") })
			{
				this.element = element;
				this.attributeName = attributeName;
				this.setAttribute = setAttribute;
			}

			public override string DisplayName => "⚡ " + attributeName.LocalName;
			public override Type ComponentType => typeof(WinUIXamlElementPropertyAdapter);
			public override Type EventType => typeof(EventHandler);
			public override bool IsMulticast => true;

			// The standard WinUI delegate type name for this event, so the pad shows e.g.
			// "RoutedEventHandler" for Click instead of a generic placeholder.
			public string HandlerTypeName => StandardDelegateNames.TryGetValue(attributeName.LocalName, out var name)
				? name
				: "EventHandler";

			public string GetHandlerName()
				=> element.Attribute(attributeName)?.Value ?? "";

			public void SetHandlerName(string handlerName)
			{
				var current = element.Attribute(attributeName)?.Value;
				if (string.IsNullOrEmpty(handlerName) ? current == null : current == handlerName)
					return;
				setAttribute(element, attributeName, string.IsNullOrEmpty(handlerName) ? null : handlerName);
			}

			public override void AddEventHandler(object component, Delegate handler)
				=> SetHandlerName(handler?.Method.Name ?? "");

			public override void RemoveEventHandler(object component, Delegate handler)
				=> SetHandlerName("");
		}

		/// <summary>Converts between XAML enum values and CLR enum instances.</summary>
		sealed class EnumValueConverter : TypeConverter
		{
			readonly Type enumType;

			public EnumValueConverter(Type enumType) => this.enumType = enumType;

			public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
				=> sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

			public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
				=> destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

			public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
			{
				if (value is string text && Enum.TryParse(enumType, text, ignoreCase: true, out var parsed))
					return parsed;
				return base.ConvertFrom(context, culture, value);
			}

			public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
			{
				if (destinationType == typeof(string) && value != null)
					return value.ToString();
				return base.ConvertTo(context, culture, value, destinationType);
			}
		}

		/// <summary>Converts between "l,t,r,b" (or "all") XAML values and Thickness.</summary>
		sealed class ThicknessValueConverter : TypeConverter
		{
			public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
				=> sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

			public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
				=> destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

			public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
			{
				if (value is string text && TryParse(text, out var thickness))
					return thickness;
				return base.ConvertFrom(context, culture, value);
			}

			public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
			{
				if (destinationType == typeof(string) && value is Thickness thickness)
					return Format(thickness);
				return base.ConvertTo(context, culture, value, destinationType);
			}

			internal static bool TryParse(string text, out Thickness thickness)
			{
				thickness = default;
				if (string.IsNullOrWhiteSpace(text))
					return false;
				var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var all))
				{
					thickness = new Thickness(all);
					return true;
				}
				if (parts.Length == 2
					&& double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lr)
					&& double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tb))
				{
					thickness = new Thickness(lr, tb, lr, tb);
					return true;
				}
				if (parts.Length == 4
					&& double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l)
					&& double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
					&& double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
					&& double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
				{
					thickness = new Thickness(l, t, r, b);
					return true;
				}
				return false;
			}

			internal static string Format(Thickness value)
			{
				if (value.Left == value.Top && value.Left == value.Right && value.Left == value.Bottom)
					return value.Left.ToString(CultureInfo.InvariantCulture);
				if (value.Left == value.Right && value.Top == value.Bottom)
					return $"{value.Left.ToString(CultureInfo.InvariantCulture)},{value.Top.ToString(CultureInfo.InvariantCulture)}";
				return $"{value.Left.ToString(CultureInfo.InvariantCulture)},{value.Top.ToString(CultureInfo.InvariantCulture)}," +
					$"{value.Right.ToString(CultureInfo.InvariantCulture)},{value.Bottom.ToString(CultureInfo.InvariantCulture)}";
			}
		}

		/// <summary>Converts between "#RRGGBB" (or "#AARRGGBB") XAML values and Color.</summary>
		sealed class ColorValueConverter : TypeConverter
		{
			public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
				=> sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

			public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
				=> destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

			public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
			{
				if (value is string text && TryParseColor(text, out var color))
					return color;
				return base.ConvertFrom(context, culture, value);
			}

			public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
			{
				if (destinationType == typeof(string) && value is Color color)
					return FormatColor(color);
				return base.ConvertTo(context, culture, value, destinationType);
			}

			internal static bool TryParseColor(string text, out Color color)
			{
				color = default;
				if (string.IsNullOrWhiteSpace(text))
					return false;
				try
				{
					color = (Color)ColorConverter.ConvertFromString(text);
					return true;
				}
				catch
				{
					return false;
				}
			}

			internal static string FormatColor(Color color)
				=> $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
		}

	}
}
