using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Rewrites <paramref name="root"/> in place, returning how many compile-time
    /// constructs were dropped.</summary>
    public static int Strip(XElement root)
    {
        var stripped = 0;
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
                attribute.Remove();
                stripped++;
            }
        }
        return stripped;
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
