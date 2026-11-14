using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using System.Windows.Controls;
using ICSharpCode.SharpDevelop.Designer.Remote;
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
	sealed class WinUIXamlElementPropertyAdapter : ICustomTypeDescriptor, IPropertyGridEventSource, IEventBindingHost
	{
		readonly XElement element;
		readonly XElement documentRoot;
		readonly Action<XElement, XName, string> setAttribute;
		readonly Action<XElement, XName, string> setEventHandler;
		readonly Action<XElement> editRichText;
		readonly Func<XElement, bool> canEditRichText;
		readonly Func<XElement, IReadOnlyList<ICSharpCode.SharpDevelop.Designer.Remote.DesignerPropertyInfo>> runtimeProperties;

		public WinUIXamlElementPropertyAdapter(XElement element, XElement documentRoot, Action<XElement, XName, string> setAttribute, Action<XElement, XName, string> setEventHandler = null, Action<XElement> editRichText = null, Func<XElement, bool> canEditRichText = null,
			Func<XElement, IReadOnlyList<ICSharpCode.SharpDevelop.Designer.Remote.DesignerPropertyInfo>> runtimeProperties = null)
		{
			this.runtimeProperties = runtimeProperties;
			this.element = element ?? throw new ArgumentNullException(nameof(element));
			this.documentRoot = documentRoot;
			this.setAttribute = setAttribute ?? throw new ArgumentNullException(nameof(setAttribute));
			// Event-handler assignment can drive a distinct incremental render RPC
			// (design/set-event) from a plain attribute edit (design/set-property); callers that
			// don't care about that distinction may omit it and share the property callback.
			this.setEventHandler = setEventHandler ?? setAttribute;
			this.editRichText = editRichText;
			this.canEditRichText = canEditRichText;
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
			setEventHandler(element, attribute, string.IsNullOrEmpty(handlerName) ? null : handlerName);
		}

		// IEventBindingHost: VS-style double-click on an Events row creates the conventional
		// <element>_<event> handler reference in the XAML event attribute (the code-behind method
		// creation is a separate concern; the attribute write flows through the same
		// design/set-event path as a typed binding).
		void IEventBindingHost.BindEvent(string eventName)
		{
			if (!string.IsNullOrEmpty(element.Attribute(XName.Get(eventName))?.Value))
				return; // already bound - a second double-click is a no-op
			var elementName = (string)element.Attribute(WinUIXamlDocumentEditor.NameDirective)
				?? element.Name.LocalName;
			((IPropertyGridEventSource)this).SetEventHandler(eventName, elementName + "_" + eventName);
		}

		public PropertyDescriptorCollection GetProperties() => GetProperties(null);

		public PropertyDescriptorCollection GetProperties(Attribute[] attributes)
		{
			var descriptors = new List<PropertyDescriptor>();
			var resourceKeys = CollectResourceKeys();
			var present = new HashSet<string>(StringComparer.Ordinal);
			var eventNames = EventsFor(element.Name.LocalName);
			// The renderer's own list for this element (when it has one): every settable property with its real category, type and choices.
			var runtime = RuntimeCatalog();
			foreach (var attribute in element.Attributes()) {
				if (attribute.IsNamespaceDeclaration) continue;
				present.Add(attribute.Name.LocalName);
				// Event attributes (Click="...") live in the Events view, not the property list.
				if (eventNames.Contains(attribute.Name.LocalName))
					continue;
				runtime.TryGetValue(attribute.Name.LocalName, out var authoredInfo);
				descriptors.Add(new XamlAttributeDescriptor(element, attribute.Name, attribute.Value, setAttribute, resourceKeys, runtime: authoredInfo));
			}
			if (element.Name.LocalName == "RichTextBlock" && editRichText != null && (canEditRichText?.Invoke(element) ?? false))
				descriptors.Add(new RichTextBlockDescriptor(element, editRichText));
			// The pad lists the element's common properties even when they are not set yet,
			// so users can add them (VS-style) instead of only editing existing attributes.
			var offered = runtime.Count > 0
				? runtime.Values.Where(IsSettable).Select(info => info.Name)
				: CommonProperties(element.Name.LocalName);
			foreach (var name in offered)
			{
				if (present.Contains(name))
					continue;
				// Attribute names without a namespace serialize without a prefix, and the
				// XAML reader resolves them into the element's default namespace - the
				// idiomatic form, rather than a generated "p7:" prefix.
				runtime.TryGetValue(name, out var info);
				// The renderer's value fills the value cell while the source still has no
				// attribute, so the pad shows what the design currently renders (and the
				// description says it came from the renderer, not the markup).
				descriptors.Add(new XamlAttributeDescriptor(element, XName.Get(name), null, setAttribute, resourceKeys, runtime: info,
					fallbackValue: info?.Value));
			}
			// The parent container's attached layout properties: a Grid child can be placed in a
			// cell and a Canvas child at an offset even when the source never authored the attribute.
			foreach (var attached in AttachedProperties(element))
			{
				if (present.Contains(attached.Name))
					continue;
				descriptors.Add(new XamlAttributeDescriptor(element, XName.Get(attached.Name), null, setAttribute, resourceKeys,
					fallbackValue: attached.DefaultValue));
			}
			return new PropertyDescriptorCollection(descriptors.ToArray(), readOnly: true);
		}

		/// <summary>The attached layout properties the element's parent container understands, with
		/// the value WinUI uses when the attribute is not authored.</summary>
		static IEnumerable<(string Name, string DefaultValue)> AttachedProperties(XElement element)
		{
			switch (element.Parent?.Name.LocalName)
			{
				case "Grid":
					yield return ("Grid.Row", "0");
					yield return ("Grid.Column", "0");
					yield return ("Grid.RowSpan", "1");
					yield return ("Grid.ColumnSpan", "1");
					break;
				case "Canvas":
					yield return ("Canvas.Left", "0");
					yield return ("Canvas.Top", "0");
					break;
			}
		}

		/// <summary>The renderer's properties for this element by name, in the renderer's order; empty without a catalog.</summary>
		Dictionary<string, ICSharpCode.SharpDevelop.Designer.Remote.DesignerPropertyInfo> RuntimeCatalog()
		{
			var byName = new Dictionary<string, ICSharpCode.SharpDevelop.Designer.Remote.DesignerPropertyInfo>(StringComparer.Ordinal);
			try {
				foreach (var info in runtimeProperties?.Invoke(element) ?? Array.Empty<ICSharpCode.SharpDevelop.Designer.Remote.DesignerPropertyInfo>())
					byName.TryAdd(info.Name, info);
			} catch (Exception) {
				// A catalog that fails leaves the fixed list; the pad must still open.
			}
			return byName;
		}

		static readonly HashSet<string> TextEditableTypes = new(StringComparer.Ordinal) {
			"Boolean", "String", "Double", "Single", "Int32", "Thickness", "CornerRadius", "Brush", "FontFamily"
		};

		/// <summary>A runtime property the pad can offer: not read-only, and either with a closed set of values or of a type that round-trips as attribute text.</summary>
		static bool IsSettable(ICSharpCode.SharpDevelop.Designer.Remote.DesignerPropertyInfo info)
			=> !info.IsReadOnly && info.Name != "Name" && (info.AllowedValues.Count > 0 || TextEditableTypes.Contains(info.TypeName) || info.Name == "Content");

		public EventDescriptorCollection GetEvents() => GetEvents(null);

		public EventDescriptorCollection GetEvents(Attribute[] attributes)
		{
			var eventNames = EventsFor(element.Name.LocalName);
			var descriptors = new EventDescriptor[eventNames.Count];
			for (var i = 0; i < eventNames.Count; i++)
				descriptors[i] = new XamlEventDescriptor(element, XName.Get(eventNames[i]), setEventHandler);
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

		/// <summary>Descriptor name for an attribute; the Properties pad's binding-path parser
		/// would otherwise split a dotted attached-property name.</summary>
		internal static string BindingSafeName(string localName) => localName.Replace('.', '_');

		/// <summary>A synthetic row for the structured child content of a RichTextBlock.
		/// It deliberately has no XAML attribute counterpart: the editor owns the direct
		/// Paragraph/Run subtree and commits it as one source-document operation.</summary>
		sealed class RichTextBlockDescriptor : PropertyDescriptor
		{
			readonly XElement element;
			readonly Action<XElement> edit;

			public RichTextBlockDescriptor(XElement element, Action<XElement> edit)
				: base("FormattedText", new Attribute[] {
					new EditorAttribute(typeof(WinUIRichTextBlockEditor), typeof(Xceed.Wpf.Toolkit.PropertyGrid.Editors.ITypeEditor))
				})
			{
				this.element = element;
				this.edit = edit;
			}

			public override Type ComponentType => typeof(WinUIXamlElementPropertyAdapter);
			public override string DisplayName => "Formatted Text";
			public override string Category => "XAML";
			public override Type PropertyType => typeof(string);
			public override bool IsReadOnly => false;
			public override object GetValue(object component) => "Edit rich text…";
			public override void SetValue(object component, object value) => edit(element);
			public override bool CanResetValue(object component) => false;
			public override void ResetValue(object component) { }
			public override bool ShouldSerializeValue(object component) => false;
		}

		sealed class XamlAttributeDescriptor : PropertyDescriptor
		{
			readonly XElement element;
			readonly XName attributeName;
			readonly string fallbackValue;
			readonly Action<XElement, XName, string> setAttribute;
			readonly IReadOnlyList<string> resourceKeys;
			readonly ICSharpCode.SharpDevelop.Designer.Remote.DesignerPropertyInfo runtime;

			public XamlAttributeDescriptor(XElement element, XName attributeName, string currentValue,
				Action<XElement, XName, string> setAttribute, IReadOnlyList<string> resourceKeys, string fallbackValue = null,
				ICSharpCode.SharpDevelop.Designer.Remote.DesignerPropertyInfo runtime = null)
				// Xceed binds each row with new Binding(descriptor.Name); an attached attribute such
				// as "Grid.Row" would be parsed as a property path and silently drop the edit.
				: base(BindingSafeName(attributeName.LocalName), null)
			{
				this.element = element;
				this.attributeName = attributeName;
				this.fallbackValue = fallbackValue;
				this.setAttribute = setAttribute;
				this.resourceKeys = resourceKeys;
				this.runtime = runtime;
			}

			public override Type ComponentType => typeof(WinUIXamlElementPropertyAdapter);
			public override string DisplayName => attributeName.LocalName;
			public override bool IsReadOnly => false;
			// The renderer's category (Layout, Appearance, Text, ...) when it knows the property; an
			// attached property is a layout fact about the element; otherwise the source-level grouping.
			public override string Category => attributeName.LocalName.Contains('.')
				? "Layout"
				: !string.IsNullOrEmpty(runtime?.Category) && attributeName.NamespaceName.Length == 0
					? runtime.Category
					: attributeName.NamespaceName.Length == 0 ? "XAML" : attributeName.NamespaceName;

			/// <summary>The text the pad edits: the authored attribute, else the value the renderer
			/// reports for an unset property, else the container's default.</summary>
			string EffectiveText => element.Attribute(attributeName)?.Value ?? fallbackValue;

			public override string Description
			{
				get
				{
					var text = EffectiveText;
					var source = element.Attribute(attributeName) != null ? "authored"
						: fallbackValue != null ? "current value from the renderer"
						: "not set";
					if (string.IsNullOrEmpty(text) && runtime == null)
						return base.Description;
					return "Value: " + (string.IsNullOrEmpty(text) ? "(empty)" : text)
						+ (runtime == null || string.IsNullOrEmpty(runtime.TypeName) ? "" : " (" + runtime.TypeName + ")")
						+ " - " + source;
				}
			}

			static bool IsThicknessProperty(string name)
				=> name == "Margin" || name == "Padding" || name == "BorderThickness";

			static bool IsCornerRadiusProperty(string name) => name == "CornerRadius";

			static bool IsFontFamilyProperty(string name) => name == "FontFamily";

			bool RuntimeTypeIs(params string[] typeNames)
				=> runtime != null && Array.IndexOf(typeNames, runtime.TypeName) >= 0;

			bool RuntimeKindIs(string kind) => runtime != null && string.Equals(runtime.Kind, kind, StringComparison.Ordinal);

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
					var text = EffectiveText;
					if (TryGetResourceReference(text, out _))
						return typeof(string);
					if (EnumMappings.TryGetValue(attributeName.LocalName, out var enumType))
						return enumType;
					if (RuntimeChoices != null)
						return typeof(string);
					// The renderer's catalog knows the real type even when the source has no attribute.
					if (RuntimeTypeIs("Brush", "Color") || RuntimeKindIs("Brush") || RuntimeKindIs("Color"))
						return typeof(Color);
					if (RuntimeTypeIs("CornerRadius") || IsCornerRadiusProperty(attributeName.LocalName))
						return typeof(CornerRadius);
					if (RuntimeTypeIs("Thickness") || KindIsThickness() || IsThicknessProperty(attributeName.LocalName))
						return typeof(Thickness);
					if (RuntimeTypeIs("FontFamily") || IsFontFamilyProperty(attributeName.LocalName))
						return typeof(string);
					if (IsBrushProperty(attributeName.LocalName, text))
						return typeof(Color);
					return typeof(string);
				}
			}

			bool KindIsThickness() => RuntimeKindIs("Thickness");

			public override TypeConverter Converter
			{
				get
				{
					var text = EffectiveText;
					if (TryGetResourceReference(text, out var resourceReferenceKind))
						return new ResourceReferenceValueConverter(resourceKeys, resourceReferenceKind);
					if (EnumMappings.TryGetValue(attributeName.LocalName, out var enumType))
						return new EnumValueConverter(enumType);
					if (RuntimeChoices != null)
						return new RuntimeChoiceConverter(RuntimeChoices);
					if (RuntimeTypeIs("CornerRadius") || IsCornerRadiusProperty(attributeName.LocalName))
						return new CornerRadiusValueConverter();
					if (RuntimeTypeIs("Thickness") || KindIsThickness() || IsThicknessProperty(attributeName.LocalName))
						return new ThicknessValueConverter();
					if (RuntimeTypeIs("Brush", "Color") || RuntimeKindIs("Brush") || RuntimeKindIs("Color")
						|| IsBrushProperty(attributeName.LocalName, text))
						return new ColorValueConverter();
					if (RuntimeTypeIs("FontFamily") || IsFontFamilyProperty(attributeName.LocalName))
						return new FontFamilyValueConverter();
					return base.Converter;
				}
			}

			/// <summary>The renderer's closed set of values for a property the fixed tables do not know (any enum, or a boolean), or null.</summary>
			IReadOnlyList<string> RuntimeChoices => runtime != null && runtime.AllowedValues.Count > 0
				&& !EnumMappings.ContainsKey(attributeName.LocalName) && !TryGetResourceReference(EffectiveText, out _)
				? runtime.AllowedValues : null;

			static bool TryGetResourceReference(string value, out string kind)
			{
				kind = null;
				if (string.IsNullOrWhiteSpace(value))
					return false;
				foreach (var candidate in new[] { "StaticResource", "ThemeResource" })
				{
					if (value.StartsWith("{" + candidate + " ", StringComparison.Ordinal)
						&& value.EndsWith("}", StringComparison.Ordinal))
					{
						kind = candidate;
						return true;
					}
				}
				return false;
			}

			public override object GetValue(object component)
			{
				var text = EffectiveText;
				if (string.IsNullOrEmpty(text))
					return null;
				if (TryGetResourceReference(text, out _))
					return text;
				if (EnumMappings.TryGetValue(attributeName.LocalName, out var enumType))
					return Enum.TryParse(enumType, text, ignoreCase: true, out var parsed) ? parsed : text;
				if (RuntimeTypeIs("CornerRadius") || IsCornerRadiusProperty(attributeName.LocalName))
					return CornerRadiusValueConverter.TryParse(text, out var cornerRadius) ? cornerRadius : text;
				if (RuntimeTypeIs("Thickness") || KindIsThickness() || IsThicknessProperty(attributeName.LocalName))
					return ThicknessValueConverter.TryParse(text, out var thickness) ? thickness : text;
				if (RuntimeTypeIs("Brush", "Color") || RuntimeKindIs("Brush") || RuntimeKindIs("Color")
					|| IsBrushProperty(attributeName.LocalName, text))
					return ColorValueConverter.TryParseColor(text, out var color) ? color : text;
				return text;
			}

			public override void SetValue(object component, object value)
			{
				if (EnumMappings.TryGetValue(attributeName.LocalName, out var enumType))
				{
					setAttribute(element, attributeName, value == null ? null : value.ToString());
					return;
				}
				if ((RuntimeTypeIs("CornerRadius") || IsCornerRadiusProperty(attributeName.LocalName)) && value is CornerRadius cornerRadius)
				{
					setAttribute(element, attributeName, CornerRadiusValueConverter.Format(cornerRadius));
					return;
				}
				if ((RuntimeTypeIs("Thickness") || KindIsThickness() || IsThicknessProperty(attributeName.LocalName)) && value is Thickness thickness)
				{
					setAttribute(element, attributeName, ThicknessValueConverter.Format(thickness));
					return;
				}
				if (value is Color color)
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

		/// <summary>A dropdown of the renderer's legal values for an enum or boolean property.</summary>
		sealed class RuntimeChoiceConverter : StringConverter
		{
			readonly IReadOnlyList<string> choices;
			public RuntimeChoiceConverter(IReadOnlyList<string> choices) => this.choices = choices;
			public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
			public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => true;
			public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context) => new StandardValuesCollection(choices.ToArray());
		}

		/// <summary>
		/// Supplies local XAML resource keys as actual PropertyGrid standard values for an existing
		/// resource reference.  Keep the reference kind chosen by the author: changing a
		/// <c>ThemeResource</c> must not silently turn it into a <c>StaticResource</c>.
		/// </summary>
		sealed class ResourceReferenceValueConverter : StringConverter
		{
			readonly IReadOnlyList<string> resourceKeys;
			readonly string resourceReferenceKind;

			public ResourceReferenceValueConverter(IReadOnlyList<string> resourceKeys, string resourceReferenceKind)
			{
				this.resourceKeys = resourceKeys;
				this.resourceReferenceKind = resourceReferenceKind;
			}

			public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
			// The page cannot see all resources that the XAML loader can resolve (App.xaml,
			// merged dictionaries and theme resources are outside documentRoot).  These are useful
			// suggestions, not an exhaustive domain: keep the normal text editor available for
			// those valid external references and for switching back to a literal brush value.
			public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => false;

			public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
				=> new StandardValuesCollection(resourceKeys
					.Select(key => "{" + resourceReferenceKind + " " + key + "}")
					.ToArray());
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

			public override string DisplayName => attributeName.LocalName;
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

		/// <summary>Converts WinUI CornerRadius XAML: one number for all corners, or four
		/// (top-left, top-right, bottom-right, bottom-left). WinUI does not accept the
		/// two-value form Thickness allows, so it gets its own converter.</summary>
		sealed class CornerRadiusValueConverter : TypeConverter
		{
			public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
				=> sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

			public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
				=> destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

			public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
			{
				if (value is string text && TryParse(text, out var radius))
					return radius;
				return base.ConvertFrom(context, culture, value);
			}

			public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
			{
				if (destinationType == typeof(string) && value is CornerRadius radius)
					return Format(radius);
				return base.ConvertTo(context, culture, value, destinationType);
			}

			internal static bool TryParse(string text, out CornerRadius radius)
			{
				radius = default;
				if (string.IsNullOrWhiteSpace(text))
					return false;
				var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var all))
				{
					radius = new CornerRadius(all);
					return true;
				}
				if (parts.Length == 4
					&& double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var topLeft)
					&& double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var topRight)
					&& double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var bottomRight)
					&& double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var bottomLeft))
				{
					radius = new CornerRadius(topLeft, topRight, bottomRight, bottomLeft);
					return true;
				}
				return false;
			}

			internal static string Format(CornerRadius value)
			{
				if (value.TopLeft == value.TopRight && value.TopLeft == value.BottomRight && value.TopLeft == value.BottomLeft)
					return value.TopLeft.ToString(CultureInfo.InvariantCulture);
				return $"{value.TopLeft.ToString(CultureInfo.InvariantCulture)},{value.TopRight.ToString(CultureInfo.InvariantCulture)}," +
					$"{value.BottomRight.ToString(CultureInfo.InvariantCulture)},{value.BottomLeft.ToString(CultureInfo.InvariantCulture)}";
			}
		}

		/// <summary>Installed font families as non-exclusive suggestions for FontFamily: any family
		/// the application can resolve can still be typed, and WinUI's family names match the system's.</summary>
		sealed class FontFamilyValueConverter : StringConverter
		{
			static IReadOnlyList<string> installed;

			static IReadOnlyList<string> Installed()
			{
				if (installed != null)
					return installed;
				try {
					installed = Fonts.SystemFontFamilies
						.Select(family => family.Source)
						.Where(name => !string.IsNullOrEmpty(name))
						.Distinct(StringComparer.OrdinalIgnoreCase)
						.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
						.ToArray();
				} catch {
					// A platform without a font enumeration still gets the text editor.
					installed = Array.Empty<string>();
				}
				return installed;
			}

			public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => Installed().Count > 0;
			public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => false;
			public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
				=> new StandardValuesCollection(Installed().ToArray());
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
