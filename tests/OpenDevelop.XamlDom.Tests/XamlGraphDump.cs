using System.Collections;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace OpenDevelop.XamlDom.Tests;

/// <summary>Renders the object graph a XAML loader produced as stable text, so two loaders can be diffed.
/// It records what the markup expresses (types, explicitly set dependency properties, resources, logical
/// children), not incidental state such as layout.</summary>
static class XamlGraphDump
{
	/// <summary>Attached properties each loader uses for its own bookkeeping (base URI, namespace maps, the
	/// <c>mc:Ignorable</c>/<c>d:</c> design-time values), which say nothing about what the markup expresses.</summary>
	static readonly HashSet<string> Infrastructure = new() { "BaseUriHelper", "XmlAttributeProperties", "NameScope", "MarkupCompatibilityProperties", "DesignTimeProperties", "TextEditor", "UndoManager" };

	public static string Dump(object? root)
	{
		var text = new StringBuilder();
		Write(text, root, 0);
		return text.ToString();
	}

	static void Write(StringBuilder text, object? value, int depth)
	{
		var indent = new string(' ', depth * 2);
		switch (value) {
			case null:
				text.Append(indent).AppendLine("null");
				return;
			case ResourceDictionary dictionary:
				text.Append(indent).AppendLine("ResourceDictionary");
				foreach (var key in dictionary.Keys.Cast<object>().OrderBy(KeyText, StringComparer.Ordinal)) {
					text.Append(indent).Append("  [").Append(KeyText(key)).AppendLine("]");
					Write(text, dictionary[key], depth + 2);
				}
				foreach (var merged in dictionary.MergedDictionaries) {
					text.Append(indent).AppendLine("  merged");
					Write(text, merged, depth + 2);
				}
				return;
			// Styles, setters and triggers are listed before the DependencyObject arm: some derive from it, and
			// their content (TargetType, Property/Value pairs, trigger conditions) is exactly what must be compared.
			case Style style:
				text.Append(indent).Append("Style ").Append(style.TargetType?.Name).Append(" basedOn=").AppendLine(style.BasedOn == null ? "none" : "set");
				foreach (var setterBase in style.Setters)
					Write(text, setterBase, depth + 1);
				foreach (var trigger in style.Triggers)
					Write(text, trigger, depth + 1);
				return;
			case Setter setter:
				text.Append(indent).Append("Setter ").Append(setter.Property?.OwnerType.Name).Append('.').Append(setter.Property?.Name)
					.Append(string.IsNullOrEmpty(setter.TargetName) ? "" : " target=" + setter.TargetName).Append(" = ").AppendLine(Scalar(setter.Value));
				return;
			case Trigger trigger:
				text.Append(indent).Append("Trigger ").Append(trigger.Property?.Name).Append(" = ").AppendLine(Scalar(trigger.Value));
				foreach (var triggerSetter in trigger.Setters)
					Write(text, triggerSetter, depth + 1);
				return;
			case DataTrigger dataTrigger:
				text.Append(indent).Append("DataTrigger ").Append(Scalar(dataTrigger.Binding)).Append(" = ").AppendLine(Scalar(dataTrigger.Value));
				foreach (var triggerSetter in dataTrigger.Setters)
					Write(text, triggerSetter, depth + 1);
				return;
			case DependencyObject dependencyObject:
				text.Append(indent).AppendLine(value.GetType().Name);
				var locals = new List<string>();
				var enumerator = dependencyObject.GetLocalValueEnumerator();
				while (enumerator.MoveNext()) {
					var entry = enumerator.Current;
					if (Infrastructure.Contains(entry.Property.OwnerType.Name))
						continue;
					locals.Add(entry.Property.OwnerType.Name + "." + entry.Property.Name + " = " + Scalar(entry.Value));
				}
				foreach (var local in locals.OrderBy(l => l, StringComparer.Ordinal))
					text.Append(indent).Append("  @").AppendLine(local);
				if (value is FrameworkElement { Resources.Count: > 0 } element) {
					text.Append(indent).AppendLine("  Resources:");
					Write(text, element.Resources, depth + 2);
				}
				// An ItemsControl's logical children are generated containers; its Items are what the markup holds.
				var children = dependencyObject is ItemsControl itemsControl ? itemsControl.Items.Cast<object>() : LogicalTreeHelper.GetChildren(dependencyObject).Cast<object>();
				foreach (var child in children) {
					if (child is DependencyObject)
						Write(text, child, depth + 1);
					else
						text.Append(indent).Append("  child ").AppendLine(Scalar(child));
				}
				return;
			default:
				text.Append(indent).AppendLine(Scalar(value));
				return;
		}
	}

	static string KeyText(object key) => key is Type type ? "{x:Type " + type.Name + "}" : Convert.ToString(key, CultureInfo.InvariantCulture) ?? "";

	static string Scalar(object? value) => value switch {
		null => "null",
		string s => "\"" + s + "\"",
		bool or int or long or short or byte or float or double or decimal or char or Enum => value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture),
		SolidColorBrush brush => "SolidColorBrush:" + brush.Color,
		Style style => "Style:" + style.TargetType?.Name + "/" + style.Setters.Count,
		Type type => "Type:" + type.Name,
		Uri uri => "Uri:" + uri,
		Thickness or CornerRadius or Color or GridLength or Point or Size or Rect => value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture),
		BindingExpression expression => Scalar(expression.ParentBinding),
		Binding binding => "Binding(Path=" + binding.Path?.Path + ", Mode=" + binding.Mode + ", Trigger=" + binding.UpdateSourceTrigger
			+ ", Fallback=" + Scalar(binding.FallbackValue) + ", Format=" + binding.StringFormat
			+ ", RelativeSource=" + (binding.RelativeSource == null ? "none" : binding.RelativeSource.Mode + "/" + binding.RelativeSource.AncestorType?.Name)
			+ ", Converter=" + Scalar(binding.Converter) + ", Parameter=" + Scalar(binding.ConverterParameter)
			+ ", ElementName=" + binding.ElementName + ", XPath=" + binding.XPath + ")",
		ResourceDictionary => "ResourceDictionary",
		IEnumerable and not string => value.GetType().Name + "[" + string.Join(",", ((IEnumerable)value).Cast<object?>().Select(Scalar)) + "]",
		_ => value.GetType().Name
	};
}
