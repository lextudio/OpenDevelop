using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost;

/// <summary>
/// Replaces the app's own controls that CANNOT be constructed at design time with a plain panel
/// that keeps their children, so the rest of the page still renders.
///
/// A control declared in XAML (<c>&lt;UserControl x:Class="TheApp.Controls.Example"&gt;</c>) gets a
/// generated <c>InitializeComponent</c> that calls
/// <c>Application.LoadComponent(this, new Uri("ms-appx:///Controls/Example.xaml"))</c>.
/// <see cref="AppResourceManagerProvider"/> makes that URI resolve, so the resource is no longer the
/// blocker - what remains is:
///
/// LoadComponent hands the parser the app's XAML, and app XAML routinely uses <c>{x:Bind}</c>, which
/// is a COMPILE-TIME feature. Its generated bindings live in code the runtime parser never runs, so
/// it reads `x:Bind` as a markup extension named `Bind` and fails with "The type 'Bind' was not
/// found". WinUI-Gallery's ControlExample has 14 of them. No amount of resource plumbing fixes that,
/// which is why substituting is the answer rather than a stopgap - and also why it still applies
/// when the app's resources ARE served.
///
/// Note the exception comes from the CONSTRUCTOR, not from parsing, so XamlReader reports it with
/// whatever position it last parsed - a position that points at unrelated markup and sent this
/// investigation down the wrong path twice. The message text is the reliable part.
///
/// Substituting matters because these wrappers are usually layout chrome: WinUI-Gallery puts every
/// sample inside a ControlExample, and keeping the children means the actual controls being
/// demonstrated still appear. Attributes are dropped (a StackPanel would reject the wrapper's own
/// properties) and so are property elements, which belong to the wrapper's own API.
/// </summary>
static class CompiledXamlControlSubstituter
{
    static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Rewrites <paramref name="root"/> in place, returning the substituted type names
    /// (empty when every element could be constructed).</summary>
    public static List<string> Substitute(XElement root)
    {
        var substituted = new List<string>();
        // Materialized first: elements are replaced while walking.
        foreach (var element in root.DescendantsAndSelf().ToList())
        {
            // A property element (<Foo.Bar>) names a member, not a type.
            if (element.Name.LocalName.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }
            if (ResolveClrType(element.Name) is not { } type || !NeedsCompiledXaml(type))
            {
                continue;
            }
            var replacement = new XElement(Xaml + "StackPanel");
            // x:Name travels so the element stays selectable and keeps its identity in the outline.
            if (element.Attribute(X + "Name") is { } name)
            {
                replacement.Add(new XAttribute(X + "Name", name.Value));
            }
            // The children may rely on prefixes declared on the element being replaced.
            foreach (var declaration in element.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                replacement.Add(new XAttribute(declaration.Name, declaration.Value));
            }
            foreach (var child in Salvage(element))
            {
                child.Remove();
                replacement.Add(child);
            }
            element.ReplaceWith(replacement);
            substituted.Add(type.FullName ?? type.Name);
        }
        return substituted;
    }

    /// <summary>
    /// The content worth keeping from a control being replaced: its direct children, plus the
    /// children of its PROPERTY ELEMENTS.
    ///
    /// Dropping property elements wholesale was the obvious reading - they name the wrapper's own
    /// API, which the stand-in panel does not have - and it silently emptied pages. A wrapper's
    /// real content is routinely assigned through one: WinUI-Gallery writes
    /// <c>&lt;ControlExample.Example&gt;</c> around the very controls being demonstrated, so
    /// discarding it left an empty panel that rendered as 0x0 while still reporting success.
    /// Lifting the grandchildren keeps what the page is actually about; only the wrapper's own
    /// chrome is lost, which is the point of substituting.
    /// </summary>
    static List<XElement> Salvage(XElement element)
    {
        var content = new List<XElement>();
        foreach (var child in element.Elements().ToList())
        {
            if (child.Name.LocalName.Contains('.', StringComparison.Ordinal))
            {
                // A property element can hold data objects rather than visuals - WinUI-Gallery's
                // <ControlExample.Substitutions> carries ControlExampleSubstitution (a Key/Value
                // pair). Adding one to a panel fails with "Cannot add instance of type ... to a
                // collection of type 'UIElementCollection'", so only visuals are lifted.
                content.AddRange(child.Elements().Where(IsVisual).ToList());
            }
            else
            {
                content.Add(child);
            }
        }
        return content;
    }

    /// <summary>Whether an element can live in a panel. An app type is checked against UIElement;
    /// anything whose type cannot be resolved as an app type is assumed to be framework markup and
    /// kept, since that is where the page's real content lives.
    ///
    /// The one exception: the X (xaml-language) namespace itself. Its intrinsics - <c>x:String</c>,
    /// <c>x:Int32</c>, <c>x:Array</c>, <c>x:Null</c>, ... - are never resolved by
    /// <see cref="ResolveClrType"/> (it only follows <c>using:</c>/<c>clr-namespace:</c>), so the
    /// "unresolved means keep it" fallback wrongly waved one through: WinUI-Gallery's
    /// <c>&lt;controls:ControlExample.Xaml&gt;</c> carries an <c>&lt;x:String&gt;</c> holding the
    /// sample's source text, and lifting it into the stand-in panel crashed with "Cannot add
    /// instance of type 'Windows.Foundation.String' to a collection of type 'UIElementCollection'".
    /// No <c>x:</c> intrinsic is ever a UIElement, so this namespace is excluded up front.</summary>
    static bool IsVisual(XElement element)
        => element.Name.Namespace != X
            && (ResolveClrType(element.Name) is not { } type
                || typeof(Microsoft.UI.Xaml.UIElement).IsAssignableFrom(type));

    /// <summary>Resolves an element name whose xmlns is a CLR namespace declaration; framework
    /// namespaces (the default xmlns) are not CLR-declared and return null, so only the app's own
    /// controls are ever considered.</summary>
    static Type? ResolveClrType(XName name)
    {
        var declared = name.NamespaceName;
        string? clrNamespace = null;
        if (declared.StartsWith("using:", StringComparison.Ordinal))
        {
            clrNamespace = declared.Substring("using:".Length);
        }
        else if (declared.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            clrNamespace = declared.Substring("clr-namespace:".Length).Split(';')[0];
        }
        if (string.IsNullOrEmpty(clrNamespace))
        {
            return null;
        }
        var fullName = clrNamespace + "." + name.LocalName;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (assembly.GetType(fullName, throwOnError: false) is { } found) return found;
            }
            catch { /* a broken or partially-loaded assembly must not fail the whole lookup */ }
        }
        return null;
    }

    static readonly Dictionary<Type, bool> needsCompiledXaml = new();

    /// <summary>
    /// Whether constructing this type would run a generated <c>InitializeComponent</c> - the method
    /// the XAML compiler emits for a type whose markup was compiled into the app. Types written
    /// purely in code have no such method and are left alone.
    ///
    /// Deliberately a STATIC signal. Probing instead - actually calling
    /// <c>Activator.CreateInstance</c> to see whether it throws - reads better on paper and hung
    /// the designer: these constructors run on the XAML parse thread and reach for a live
    /// dispatcher, so one of them never returned. Do not reintroduce that; a wrong substitution
    /// costs some fidelity, a hang costs the whole session.
    /// </summary>
    static bool NeedsCompiledXaml(Type type)
    {
        if (needsCompiledXaml.TryGetValue(type, out var cached))
        {
            return cached;
        }
        var result = false;
        for (var current = type; current != null && !result; current = current.BaseType)
        {
            // Generated as public on WinUI's partial classes, but accept either to stay robust.
            result = current.GetMethod("InitializeComponent",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                Type.EmptyTypes) != null;
        }
        needsCompiledXaml[type] = result;
        return result;
    }
}
