using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost;

/// <summary>
/// Makes unprefixed attached properties explicit before the runtime XAML parser sees them.
///
/// Per the XAML rules a name written with NO prefix belongs to the default xmlns, so
/// <c>AnimatedIcon.State="Normal"</c> means the framework's AnimatedIcon. WinUI's runtime parser
/// does not apply that rule to an attached property's declaring type: it qualifies the name with
/// whatever <c>using:</c> prefix happens to be in scope instead. In a document that also binds
/// <c>xmlns:local="using:TheApp.Controls"</c> it therefore looks for
/// <c>TheApp.Controls.AnimatedIcon</c>, which nobody declared, and reports
/// "The type 'AnimatedIcon' was not found" - naming the type as written, so the wrong namespace it
/// actually searched never appears. src/Samples/MicrosoftWinUISample/AttachedPropertyPage.xaml is
/// the minimal case: the page renders as soon as that one xmlns line is gone.
///
/// Correcting it inside the metadata provider was tried and does not work: answering the
/// misqualified name with the framework type means reporting a FullName that does not match what
/// was asked for, and that aliased entry then stands in for the type itself, breaking later
/// correctly-qualified lookups on it. Rewriting the markup keeps the provider honest - the parser
/// asks for the right name from the start.
///
/// Runs on the COMBINED document, not on each input. The theme markup carries unprefixed attached
/// properties of its own and resolves them correctly on its own, because a document that binds no
/// CLR prefix gives the parser nothing to misqualify with; only once injected under a page that
/// does bind one do they start resolving against the app's namespace. See
/// <see cref="XamlPrefixNormalizer"/> for the other half of the same combining problem.
///
/// Only documents that actually bind a <c>using:</c>/<c>clr-namespace:</c> prefix are touched -
/// those are the only ones where the misqualification can happen - so an ordinary page is handed
/// to the parser byte for byte as before.
/// </summary>
static class AttachedPropertyQualifier
{
    /// <summary>CLR namespaces the default XAML xmlns maps onto, controls first: an attached
    /// property's declaring type is nearly always a control, and the rest are the namespaces the
    /// framework's own markup reaches for (AutomationProperties, ToolTipService, Typography).</summary>
    static readonly string[] FrameworkNamespaces = [
        "Microsoft.UI.Xaml.Controls",
        "Microsoft.UI.Xaml.Controls.Primitives",
        "Microsoft.UI.Xaml.Automation",
        "Microsoft.UI.Xaml.Documents",
        "Microsoft.UI.Xaml",
        "Microsoft.UI.Xaml.Input",
        "Microsoft.UI.Xaml.Media",
        "Microsoft.UI.Xaml.Media.Animation",
        "Microsoft.UI.Xaml.Shapes",
    ];

    /// <summary>Rewrites <paramref name="root"/> in place, returning how many attributes were
    /// qualified (0 means nothing was changed and the original text can be reused).</summary>
    public static int Qualify(XElement root)
    {
        if (!BindsClrPrefix(root))
        {
            return 0;
        }
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var qualified = 0;

        string EnsurePrefix(string clrNamespace)
        {
            if (!prefixes.TryGetValue(clrNamespace, out var prefix))
            {
                // Underscored to stay clear of any prefix the document itself declares.
                prefix = "__wux" + prefixes.Count.ToString();
                prefixes.Add(clrNamespace, prefix);
                root.Add(new XAttribute(XNamespace.Xmlns + prefix, "using:" + clrNamespace));
            }
            return prefix;
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            // Materialized first: the loop replaces attributes on the element it is reading.
            var unprefixed = element.Attributes()
                .Where(a => !a.IsNamespaceDeclaration
                    && a.Name.Namespace == XNamespace.None
                    && a.Name.LocalName.Contains('.', StringComparison.Ordinal))
                .ToList();
            foreach (var attribute in unprefixed)
            {
                var name = attribute.Name.LocalName;
                var owner = name.Substring(0, name.IndexOf('.', StringComparison.Ordinal));
                if (FindFrameworkType(owner)?.Namespace is not { } clrNamespace)
                {
                    continue;
                }
                EnsurePrefix(clrNamespace);
                var value = attribute.Value;
                attribute.Remove();
                element.Add(new XAttribute(XName.Get(name, "using:" + clrNamespace), value));
                qualified++;
            }
        }

        // The same misqualification, in the one place an attribute NAME never reaches: a
        // VisualState setter addresses an attached property through a PATH STRING,
        // <c>Target="ContentPresenter.(AnimatedIcon.State)"</c>. The parser resolves the
        // parenthesised owner exactly like an attached property's declaring type, so it
        // misqualifies it against the app's `using:` prefix in precisely the same way - and the
        // loop above cannot see it, because here the name lives in an attribute's VALUE.
        //
        // WinUI-Gallery's Controls/CopyButton.xaml is the real case: it writes
        // AnimatedIcon.State BOTH as an attribute name (handled above) and three times as a
        // setter target (handled here). Its dictionary is merged into every page's app
        // resources, so the unfixed half failed unrelated pages - IconographyPage and
        // CustomXamlConditionalsPage among them - with "The type 'AnimatedIcon' was not found",
        // pointing at markup those pages do not even contain.
        foreach (var setter in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Setter"))
        {
            if (setter.Attribute("Target") is not { } target) continue;
            var rewritten = AttachedPropertyInPath.Replace(target.Value, match =>
            {
                var owner = match.Groups["owner"].Value;
                if (FindFrameworkType(owner)?.Namespace is not { } ns) return match.Value;
                return "(" + EnsurePrefix(ns) + ":" + owner + "." + match.Groups["property"].Value + ")";
            });
            if (rewritten == target.Value) continue;
            target.Value = rewritten;
            qualified++;
        }
        return qualified;
    }

    /// <summary>An UNPREFIXED attached property inside a setter target path: <c>(Owner.Property)</c>.
    /// A path that already carries a prefix (<c>(local:Owner.Property)</c>) is deliberately not
    /// matched - it says what it means already.</summary>
    static readonly System.Text.RegularExpressions.Regex AttachedPropertyInPath = new(
        @"\((?<owner>[A-Za-z_][A-Za-z0-9_]*)\.(?<property>[A-Za-z_][A-Za-z0-9_]*)\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Whether any element binds a prefix to a CLR namespace - the precondition for the
    /// parser misqualifying an unprefixed name.</summary>
    static bool BindsClrPrefix(XElement root)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration
                    && (attribute.Value.StartsWith("using:", StringComparison.Ordinal)
                        || attribute.Value.StartsWith("clr-namespace:", StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    static readonly Dictionary<string, Type?> resolved = new(StringComparer.Ordinal);

    static Type? FindFrameworkType(string shortName)
    {
        if (resolved.TryGetValue(shortName, out var cached))
        {
            return cached;
        }
        Type? found = null;
        foreach (var candidate in FrameworkNamespaces)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { found = assembly.GetType(candidate + "." + shortName, throwOnError: false); }
                catch { /* a broken or partially-loaded assembly must not fail the whole lookup */ }
                if (found != null) break;
            }
            if (found != null) break;
        }
        resolved[shortName] = found;
        return found;
    }
}
