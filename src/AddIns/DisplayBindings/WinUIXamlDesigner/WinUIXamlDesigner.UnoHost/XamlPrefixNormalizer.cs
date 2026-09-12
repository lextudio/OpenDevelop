using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost;

/// <summary>
/// Renames xmlns prefixes that mean two different things in one document.
///
/// Combining documents is what creates the problem. The default theme resources are injected under
/// the page being designed, and their elements carry their own stamped prefixes - including
/// <c>xmlns:local="using:Microsoft.UI.Xaml.Controls"</c>, because that is what the framework's own
/// markup calls its controls namespace. A page that declares
/// <c>xmlns:local="using:TheApp.Controls"</c> then has TWO meanings for <c>local</c>: the app's in
/// the page scope, the framework's inside every injected element that re-declares it.
///
/// XML says the inner declaration wins for that subtree, and XLinq serializes exactly that. WinUI's
/// runtime parser does not consistently honour it: a property element written
/// <c>&lt;local:AnimatedIcon.FallbackIconSource&gt;</c> inside such an element resolved its owner
/// through the OUTER binding, decided the owner was not the parent element's type, and reported
/// "The attachable property 'FallbackIconSource' was not found in type 'AnimatedIcon'" - naming a
/// real property of a real type as if it were a missing attached one.
///
/// Giving every conflicting binding its own unique prefix removes the ambiguity, so no scope
/// resolution is needed to get the right namespace. Documents with no conflict are left untouched.
/// </summary>
static class XamlPrefixNormalizer
{
    /// <summary>Rewrites <paramref name="root"/> in place, returning how many declarations were
    /// renamed (0 means the document was already unambiguous).</summary>
    public static int Normalize(XElement root)
    {
        var meanings = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var declaration in NamespaceDeclarations(element))
            {
                if (!meanings.TryGetValue(declaration.Name.LocalName, out var namespaces))
                {
                    namespaces = new HashSet<string>(StringComparer.Ordinal);
                    meanings.Add(declaration.Name.LocalName, namespaces);
                }
                namespaces.Add(declaration.Value);
            }
        }
        var ambiguous = meanings.Where(entry => entry.Value.Count > 1)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (ambiguous.Count == 0)
        {
            return 0;
        }

        var renamed = 0;
        foreach (var element in root.DescendantsAndSelf().ToList())
        {
            // The root's own bindings stay as authored - it is the document being designed, and its
            // prefixes are the ones a reader recognises. Only the re-declarations are renamed.
            if (element == root)
            {
                continue;
            }
            foreach (var declaration in NamespaceDeclarations(element).ToList())
            {
                var prefix = declaration.Name.LocalName;
                if (!ambiguous.Contains(prefix))
                {
                    continue;
                }
                var value = declaration.Value;
                var replacement = "__ns" + renamed.ToString();
                declaration.Remove();
                element.Add(new XAttribute(XNamespace.Xmlns + replacement, value));
                // Element and attribute NAMES follow automatically: XLinq keys those off the
                // namespace and picks whatever prefix is in scope when serializing. Attribute
                // VALUES are opaque text to it, so `TargetType="local:CopyButton"` and
                // `{local:SomeExtension}` have to be rewritten here or they lose their binding.
                RewriteValueReferences(element, prefix, replacement);
                renamed++;
            }
        }
        return renamed;
    }

    static IEnumerable<XAttribute> NamespaceDeclarations(XElement element)
        => element.Attributes().Where(a => a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.Xmlns);

    static void RewriteValueReferences(XElement subtree, string prefix, string replacement)
    {
        var from = prefix + ":";
        var to = replacement + ":";
        foreach (var element in subtree.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || !attribute.Value.Contains(from, StringComparison.Ordinal))
                {
                    continue;
                }
                attribute.Value = attribute.Value.Replace(from, to, StringComparison.Ordinal);
            }
        }
    }
}
