using System.Collections;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenDevelop.XamlDom.Tests;

/// <summary>Renders the object graph a XAML loader produced as stable text, so two loaders can be diffed.
/// It records what the markup expresses (types, explicitly set dependency properties, resources, logical
/// children, templates, triggers, bindings, animations), not incidental state such as layout.</summary>
static class XamlGraphDump
{
	/// <summary>Attached properties each loader uses for its own bookkeeping (base URI, namespace maps, the
	/// <c>mc:Ignorable</c>/<c>d:</c> design-time values), which say nothing about what the markup expresses.</summary>
	static readonly HashSet<string> Infrastructure = new() { "BaseUriHelper", "XmlAttributeProperties", "NameScope", "MarkupCompatibilityProperties", "DesignTimeProperties", "TextEditor", "UndoManager" };

	const int MaxDepth = 14;

	public static string Dump(object? root)
	{
		var text = new StringBuilder();
		Write(text, root, 0);
		return text.ToString();
	}

	static void Line(StringBuilder text, int depth, string line) => text.Append(' ', depth * 2).AppendLine(line);

	static void Write(StringBuilder text, object? value, int depth)
	{
		if (depth > MaxDepth) {
			Line(text, depth, "...");
			return;
		}
		switch (value) {
			case null:
				Line(text, depth, "null");
				return;
			case ResourceDictionary dictionary:
				Line(text, depth, "ResourceDictionary");
				foreach (var key in dictionary.Keys.Cast<object>().OrderBy(KeyText, StringComparer.Ordinal)) {
					Line(text, depth + 1, "[" + KeyText(key) + "]");
					Write(text, dictionary[key], depth + 2);
				}
				foreach (var merged in dictionary.MergedDictionaries) {
					Line(text, depth + 1, "merged");
					Write(text, merged, depth + 2);
				}
				return;
			// Styles, setters, triggers and templates are listed before the DependencyObject arm: some derive from
			// it, and their content is exactly what must be compared.
			case Style style:
				Line(text, depth, "Style " + style.TargetType?.Name + " basedOn=" + (style.BasedOn == null ? "none" : "set"));
				foreach (var setterBase in style.Setters)
					Write(text, setterBase, depth + 1);
				foreach (var trigger in style.Triggers)
					Write(text, trigger, depth + 1);
				return;
			case Setter setter:
				Line(text, depth, "Setter " + setter.Property?.OwnerType.Name + "." + setter.Property?.Name
					+ (string.IsNullOrEmpty(setter.TargetName) ? "" : " target=" + setter.TargetName) + " = " + Scalar(setter.Value));
				return;
			case Trigger trigger:
				Line(text, depth, "Trigger " + trigger.Property?.Name + " = " + Scalar(trigger.Value)
					+ (string.IsNullOrEmpty(trigger.SourceName) ? "" : " source=" + trigger.SourceName));
				foreach (var triggerSetter in trigger.Setters)
					Write(text, triggerSetter, depth + 1);
				return;
			case DataTrigger dataTrigger:
				Line(text, depth, "DataTrigger " + Scalar(dataTrigger.Binding) + " = " + Scalar(dataTrigger.Value));
				foreach (var triggerSetter in dataTrigger.Setters)
					Write(text, triggerSetter, depth + 1);
				return;
			case EventTrigger eventTrigger:
				Line(text, depth, "EventTrigger " + eventTrigger.RoutedEvent?.Name);
				foreach (var action in eventTrigger.Actions)
					Write(text, action, depth + 1);
				return;
			case BeginStoryboard begin:
				Line(text, depth, "BeginStoryboard " + begin.Name);
				Write(text, begin.Storyboard, depth + 1);
				return;
			case FrameworkTemplate template:
				Line(text, depth, template.GetType().Name + (template is ControlTemplate ct ? " target=" + ct.TargetType?.Name : "")
					+ (template is DataTemplate dt ? " dataType=" + Scalar(dt.DataType) : ""));
				if (template is ControlTemplate { Triggers.Count: > 0 } controlTemplate)
					foreach (var templateTrigger in controlTemplate.Triggers)
						Write(text, templateTrigger, depth + 1);
				try {
					Write(text, template.LoadContent(), depth + 1);
				} catch (Exception e) {
					Line(text, depth + 1, "content: " + e.GetType().Name);
				}
				return;
			case KeyBinding keyBinding:
				Line(text, depth, "KeyBinding " + keyBinding.Key + "+" + keyBinding.Modifiers + " -> " + Scalar(keyBinding.Command));
				return;
			case CommandBinding commandBinding:
				Line(text, depth, "CommandBinding " + Scalar(commandBinding.Command));
				return;
			case TimelineGroup group:
				Line(text, depth, group.GetType().Name + Locals(group));
				foreach (var child in group.Children)
					Write(text, child, depth + 1);
				return;
			case IKeyFrameAnimation keyFrameAnimation when value is DependencyObject animation:
				Line(text, depth, value.GetType().Name + Locals(animation));
				foreach (var keyFrame in keyFrameAnimation.KeyFrames.Cast<IKeyFrame>())
					Line(text, depth + 1, "keyframe " + keyFrame.GetType().Name + " " + Scalar(keyFrame.KeyTime) + " = " + Scalar(keyFrame.Value));
				return;
			case DependencyObject dependencyObject:
				Line(text, depth, value.GetType().Name);
				foreach (var local in LocalValues(dependencyObject))
					Line(text, depth + 1, "@" + local);
				if (value is FrameworkElement element) {
					if (element.Resources.Count > 0) {
						Line(text, depth + 1, "Resources:");
						Write(text, element.Resources, depth + 2);
					}
					foreach (var trigger in element.Triggers)
						Write(text, trigger, depth + 1);
					foreach (var binding in element.InputBindings.Cast<object>())
						Write(text, binding, depth + 1);
					foreach (var binding in element.CommandBindings.Cast<object>())
						Write(text, binding, depth + 1);
				}
				// An ItemsControl's logical children are generated containers; its Items are what the markup holds.
				var children = dependencyObject is ItemsControl itemsControl ? itemsControl.Items.Cast<object>() : LogicalTreeHelper.GetChildren(dependencyObject).Cast<object>();
				foreach (var child in children) {
					if (child is DependencyObject)
						Write(text, child, depth + 1);
					else
						Line(text, depth + 1, "child " + Scalar(child));
				}
				return;
			default:
				Line(text, depth, Scalar(value));
				return;
		}
	}

	static List<string> LocalValues(DependencyObject dependencyObject)
	{
		var locals = new List<string>();
		var enumerator = dependencyObject.GetLocalValueEnumerator();
		while (enumerator.MoveNext()) {
			var entry = enumerator.Current;
			if (Infrastructure.Contains(entry.Property.OwnerType.Name))
				continue;
			locals.Add(entry.Property.OwnerType.Name + "." + entry.Property.Name + " = " + Scalar(entry.Value));
		}
		locals.Sort(StringComparer.Ordinal);
		return locals;
	}

	static string Locals(DependencyObject dependencyObject) => " {" + string.Join("; ", LocalValues(dependencyObject)) + "}";

	static string KeyText(object key) => key is Type type ? "{x:Type " + type.Name + "}" : Convert.ToString(key, CultureInfo.InvariantCulture) ?? "";

	/// <summary>One-line form of a value, recursing into Freezables, templates and collections so nested
	/// content (gradient stops, transforms, geometries, template trees) takes part in the comparison.</summary>
	static string Scalar(object? value)
	{
		switch (value) {
			case null: return "null";
			case string s: return "\"" + s + "\"";
			case bool or int or long or short or byte or float or double or decimal or char or Enum:
				return value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
			case SolidColorBrush brush: return "SolidColorBrush:" + brush.Color + "/" + brush.Opacity.ToString(CultureInfo.InvariantCulture);
			case Type type: return "Type:" + type.Name;
			case Uri uri: return "Uri:" + uri;
			case Geometry geometry: return geometry.GetType().Name + ":" + geometry.ToString(CultureInfo.InvariantCulture);
			case PropertyPath path: return "PropertyPath:" + path.Path;
			case RoutedCommand command: return "RoutedCommand:" + command.Name;
			case Thickness or CornerRadius or Color or GridLength or Point or Size or Rect or Duration or TimeSpan or KeyTime or RepeatBehavior or Matrix:
				return value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
			case BindingExpression expression: return Scalar(expression.ParentBinding);
			case Binding binding:
				return "Binding(Path=" + binding.Path?.Path + ", Mode=" + binding.Mode + ", Trigger=" + binding.UpdateSourceTrigger
					+ ", Fallback=" + Scalar(binding.FallbackValue) + ", Format=" + binding.StringFormat
					+ ", RelativeSource=" + (binding.RelativeSource == null ? "none" : binding.RelativeSource.Mode + "/" + binding.RelativeSource.AncestorType?.Name + "/" + binding.RelativeSource.AncestorLevel)
					+ ", Converter=" + Scalar(binding.Converter) + ", Parameter=" + Scalar(binding.ConverterParameter)
					+ ", ElementName=" + binding.ElementName + ", XPath=" + binding.XPath + ")";
			case MultiBinding multi:
				return "MultiBinding(Mode=" + multi.Mode + ", Format=" + multi.StringFormat + ", [" + string.Join(", ", multi.Bindings.Select(Scalar)) + "])";
			case ResourceDictionary: return "ResourceDictionary";
			case IEnumerable and not string: return value.GetType().Name + "[" + string.Join(",", ((IEnumerable)value).Cast<object?>().Select(Scalar)) + "]";
			case DependencyObject or FrameworkTemplate or Style or TriggerBase or SetterBase:
				var nested = new StringBuilder();
				Write(nested, value, 0);
				return "{" + string.Join(" | ", nested.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))) + "}";
			default: return value.GetType().Name;
		}
	}
}
