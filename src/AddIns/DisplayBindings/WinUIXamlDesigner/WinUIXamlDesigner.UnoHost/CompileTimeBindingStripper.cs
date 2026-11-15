using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost;

/// <summary>
/// Drops <c>{x:Bind}</c> expressions before the runtime parser sees them.
///
/// <c>x:Bind</c> is a COMPILE-TIME feature: the XAML compiler turns each one into generated code in
/// the page's partial class, so the markup itself never has to be understood at runtime. A designer
/// parses the page's SOURCE, where those expressions are still present, and the runtime parser reads
/// <c>x:Bind</c> as a markup extension named <c>Bind</c> - failing the entire page with
/// "The type 'Bind' was not found". It is not a corner case: 75 of WinUI-Gallery's 120 sample pages
/// use it, so leaving it in place blocks most of a real corpus.
///
/// Removing the attribute leaves the property at its default, which is what a compiled binding shows
/// at design time anyway - there is no page instance, no code-behind state, nothing for it to
/// resolve against. Standard designer behaviour is exactly this: show the structure, do not evaluate
/// compiled bindings. Note this is NOT the same as the app's own controls, whose compiled XAML can
/// be loaded for real (see AppResourceManagerProvider/CompiledXamlMirror) - a page under design has
/// no compiled form to fall back on, because the file being edited is the source.
///
/// Only <c>{x:Bind ...}</c> is touched. <c>{Binding ...}</c> is a runtime expression the parser
/// handles on its own, and it degrades to an empty value without failing.
/// </summary>
static class CompileTimeBindingStripper
{
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Rewrites <paramref name="root"/> in place. Simple compiled member paths become runtime
    /// bindings; the design host supplies the page instance as the root DataContext after loading.
    /// This preserves the item DataContext below a DataTemplate, which is precisely the scope that
    /// <c>x:DataType</c> gives to an <c>x:Bind</c>. Expressions that cannot have the same meaning
    /// as a runtime Binding (functions and BindBack) are still removed rather than making the
    /// runtime parser reject the whole page.
    /// </summary>
    public static BindingRewriteResult Strip(XElement root, bool rewriteBindings = true, object? designRoot = null)
    {
        var result = new BindingRewriteResult();
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }
                // x:DataType is the other half of the same feature: it tells the XAML COMPILER what
                // x:Bind expressions in a DataTemplate bind against, and the runtime parser has no
                // such property - "The property 'DataType' was not found in type 'DataTemplate'".
                // It has to go by NAME; unlike x:Bind it is a plain type name, not an expression.
                if (attribute.Name != X + "DataType" && !IsCompiledBinding(attribute.Value))
                {
                    continue;
                }
                if (attribute.Name == X + "DataType")
                {
                    attribute.Remove();
                    result.DataTypesRemoved++;
                }
                else if (rewriteBindings && TryEvaluateRootFunction(element, attribute.Value, designRoot, out var value))
                {
                    attribute.Value = value;
                    result.BindingsEvaluated++;
                }
                else if (rewriteBindings && TryRewriteAsRuntimeBinding(attribute.Value, out var binding))
                {
                    attribute.Value = binding;
                    result.BindingsRewritten++;
                }
				else if (rewriteBindings && element.Ancestors().Any(a => a.Name.LocalName == "DataTemplate")
					&& TryCreateTemplatePlan(element, attribute, result))
				{
					attribute.Remove();
					result.BindingsDropped++;
				}
                else
                {
                    attribute.Remove();
                    result.BindingsDropped++;
                }
            }
        }
        return result;
    }

	static bool TryCreateTemplatePlan(XElement element, XAttribute attribute, BindingRewriteResult result)
	{
		var body = attribute.Value.Trim();
		if (!IsCompiledBinding(body)) return false;
		body = body["{x:Bind".Length..^1];
		var comma = FindBindingOptionSeparator(body);
		var expression = (comma < 0 ? body : body[..comma]).Trim();
		var options = comma < 0 ? "" : body[(comma + 1)..];
		if (!expression.Contains('(') && !options.Contains("BindBack", StringComparison.OrdinalIgnoreCase)) return false;
		var token = "__od_xbind_" + result.TemplatePlans.Count.ToString(CultureInfo.InvariantCulture);
		var host = (XNamespace)"using:ICSharpCode.WinUIXamlDesigner.UnoHost";
		element.SetAttributeValue(XNamespace.Xmlns + "odhost", host.NamespaceName);
		element.SetAttributeValue(host + "TemplatePlanMarker.Token", token);
		string? staticType = null;
		var open = expression.IndexOf('('); var target = open < 0 ? expression : expression[..open];
		var colon = target.IndexOf(':'); var dot = target.LastIndexOf('.');
		if (colon > 0 && dot > colon && element.GetNamespaceOfPrefix(target[..colon])?.NamespaceName is { } ns && ns.StartsWith("using:", StringComparison.Ordinal)) {
			staticType = ns["using:".Length..] + "." + target[(colon + 1)..dot];
			expression = target[(dot + 1)..] + expression[open..];
		}
		result.TemplatePlans.Add(new TemplateXBindPlan(token, attribute.Name.LocalName, expression,
			Option(options, "BindBack"), Option(options, "Mode"), staticType,
			Option(options, "Converter"), Option(options, "ConverterParameter"), Option(options, "ConverterLanguage"),
			Option(options, "FallbackValue"), Option(options, "TargetNullValue")));
		return true;
	}

	static string? Option(string text, string name) => text.Split(',').Select(part => part.Split('=', 2))
		.Where(pair => pair.Length == 2 && string.Equals(pair[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
		.Select(pair => pair[1].Trim()).FirstOrDefault();


    /// <summary>
    /// Evaluates the small, unambiguous part of a root x:Bind function expression that a runtime
    /// Binding cannot represent. This deliberately excludes templates (where the current item is
    /// not available while the document is being repaired), static calls and any binding options
    /// that change value semantics. The host owns a last-built page instance solely as design-time
    /// state; no compiled page is displayed.
    /// </summary>
    static bool TryEvaluateRootFunction(XElement element, string value, object? designRoot, out string result)
    {
        result = "";
        if (designRoot == null || element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "DataTemplate"))
            return false;
        var text = value.Trim();
        if (!IsCompiledBinding(text))
            return false;
        var body = text["{x:Bind".Length..^1];
        var comma = FindBindingOptionSeparator(body);
        var expression = (comma < 0 ? body : body[..comma]).Trim();
        var options = comma < 0 ? "" : body[comma..];
        if (options.IndexOf("BindBack", StringComparison.OrdinalIgnoreCase) >= 0 ||
            options.IndexOf("Converter", StringComparison.OrdinalIgnoreCase) >= 0 ||
            options.IndexOf("FallbackValue", StringComparison.OrdinalIgnoreCase) >= 0 ||
            options.IndexOf("TargetNullValue", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        var open = expression.IndexOf('(');
        if (open <= 0 || !expression.EndsWith(')') || expression.IndexOf('(', open + 1) >= 0)
            return false;
        var methodName = expression[..open].Trim();
        var arguments = expression[(open + 1)..^1].Trim();
        var values = arguments.Length == 0
            ? Array.Empty<object?>()
            : arguments.Split(',').Select(argument => ResolveMemberPath(designRoot, argument.Trim())).ToArray();
        if (values.Any(value => value is Unresolved))
            return false;

        var (receiver, name, flags) = ResolveFunctionTarget(element, designRoot, methodName);
        if (receiver == null || name == null)
            return false;
        var method = receiver.GetMethods(flags)
            .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == values.Length
                && ParametersAccept(candidate.GetParameters(), values));
        if (method == null)
            return false;
        try
        {
            var evaluated = method.Invoke(method.IsStatic ? null : designRoot, values);
            if (evaluated == null)
                return false;
            result = Convert.ToString(evaluated, CultureInfo.InvariantCulture) ?? "";
            return true;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
    }

    static (Type? Receiver, string? Name, BindingFlags Flags) ResolveFunctionTarget(XElement element, object designRoot, string methodName)
    {
        // Instance syntax: Shout(Title). A dotted name without an xmlns prefix is not guessed;
        // it can be a nested receiver or a static class and needs Roslyn's full scope model.
        if (!methodName.Contains(':'))
            return methodName.Contains('.')
                ? (null, null, BindingFlags.Default)
                : (designRoot.GetType(), methodName, BindingFlags.Instance | BindingFlags.Public);

        // Static syntax: local:Helpers.Format(Title), where local maps to using:My.App.
        var colon = methodName.IndexOf(':');
        var finalDot = methodName.LastIndexOf('.');
        if (colon <= 0 || finalDot <= colon + 1 || finalDot == methodName.Length - 1)
            return (null, null, BindingFlags.Default);
        var xmlns = element.GetNamespaceOfPrefix(methodName[..colon])?.NamespaceName ?? "";
        const string usingPrefix = "using:";
        if (!xmlns.StartsWith(usingPrefix, StringComparison.Ordinal))
            return (null, null, BindingFlags.Default);
        var typeName = xmlns[usingPrefix.Length..] + "." + methodName[(colon + 1)..finalDot];
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => {
            try { return assembly.GetType(typeName, false); }
            catch (Exception) { return null; }
        }).FirstOrDefault(candidate => candidate != null);
        return (type, methodName[(finalDot + 1)..], BindingFlags.Static | BindingFlags.Public);
    }

    sealed class Unresolved { }

    static object? ResolveMemberPath(object root, string path)
    {
        if (path.Length == 0 || path.Any(character => !(char.IsLetterOrDigit(character) || character == '_' || character == '.')))
            return new Unresolved();
        object? current = root;
        foreach (var memberName in path.Split('.'))
        {
            if (current == null)
                return null;
            var type = current.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                current = property.GetValue(current);
                continue;
            }
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (field != null)
            {
                current = field.GetValue(current);
                continue;
            }
            return new Unresolved();
        }
        return current;
    }

    static bool ParametersAccept(ParameterInfo[] parameters, object?[] values)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            if (values[index] == null)
            {
                if (parameters[index].ParameterType.IsValueType && Nullable.GetUnderlyingType(parameters[index].ParameterType) == null)
                    return false;
            }
            else if (!parameters[index].ParameterType.IsInstanceOfType(values[index]))
                return false;
        }
        return true;
    }

    /// <summary>The comma after a compiled-binding expression introduces options; commas inside
    /// a function call are argument separators and must remain part of the expression.</summary>
    static int FindBindingOptionSeparator(string body)
    {
        var depth = 0;
        char quote = '\0';
        for (var index = 0; index < body.Length; index++)
        {
            var character = body[index];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (character == '(') { depth++; continue; }
            if (character == ')' && depth > 0) { depth--; continue; }
            if (character == ',' && depth == 0) return index;
        }
        return -1;
    }

    /// <summary>
    /// The common subset has identical path/converter/fallback syntax in Binding. Do not turn a
    /// function into a path: it would render a plausible but wrong preview. BindBack is likewise
    /// deliberately deferred until the designer has a write-back protocol.
    /// </summary>
    static bool TryRewriteAsRuntimeBinding(string value, out string binding)
    {
        binding = "";
        var text = value.Trim();
        if (!IsCompiledBinding(text))
            return false;
        var body = text["{x:Bind".Length..^1];
        if (body.IndexOf("BindBack", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        var comma = FindBindingOptionSeparator(body);
        var expression = (comma < 0 ? body : body[..comma]).Trim();
        if (expression.IndexOf('(') >= 0 || expression.IndexOf(')') >= 0)
            return false;
        // WinUI's runtime parser does not consistently accept Binding's positional Path form
        // when it receives hand-authored source through XamlReader. Be explicit so this works
        // on both the Uno and Microsoft hosts.
        binding = "{Binding Path=" + expression + (comma < 0 ? "" : body[comma..]) + "}";
        return true;
    }

    internal sealed class BindingRewriteResult
    {
		public List<TemplateXBindPlan> TemplatePlans { get; } = [];
        public int BindingsRewritten { get; set; }
        public int BindingsEvaluated { get; set; }
        public int BindingsDropped { get; set; }
        public int DataTypesRemoved { get; set; }
    }

    /// <summary>Whether a value is a compiled-binding expression. Matched on the prefix that the
    /// XAML language reserves for it rather than on any prefix ending in <c>:Bind</c>, so an
    /// app-defined extension that happens to be called Bind is left alone.</summary>
    static bool IsCompiledBinding(string value)
    {
        var text = value.AsSpan().TrimStart();
        if (!text.StartsWith("{x:Bind", StringComparison.Ordinal))
        {
            return false;
        }
        // "{x:Bind}" and "{x:Bind ..." are bindings; "{x:BindSomething" is a different extension.
        var rest = text["{x:Bind".Length..];
        return rest.IsEmpty || rest[0] is '}' or ' ' or '\t' or '\r' or '\n';
    }
}
