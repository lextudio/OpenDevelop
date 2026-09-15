using System;
using System.Linq;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// Makes a document whose ROOT cannot be rendered previewable by designing its CONTENT instead.
	///
	/// The case that forces this is <c>&lt;Window&gt;</c>. WinUI declares it as
	/// <c>unsealed runtimeclass Window</c> with no base class (microsoft.ui.xaml.coretypes2.idl):
	/// it is a plain WinRT object, not a DependencyObject, and a runtime XAML tree is built out of
	/// DependencyObjects - so <c>XamlReader.Load</c> fails before it looks at anything in the file.
	/// It reports <c>XamlParseException (0x802B000A): XAML parsing failed.</c>, with no element and
	/// no line, which reads like the document is broken. It is not: every WinUI app's
	/// MainWindow.xaml is shaped this way and its content renders perfectly well.
	///
	/// Deliberately NOT keyed on the type name. An earlier version matched <c>Window</c> in the
	/// WinUI namespace and swapped it for a <c>Grid</c>, which was wrong three ways: Window is
	/// <c>unsealed</c>, so a custom window base class missed the match and still failed opaquely;
	/// the Window-only attributes had to be stripped from a hand-written list that silently rots as
	/// WinUI adds members; and retyping the root made the surface claim the document's root was a
	/// Grid, so selecting it offered Grid properties for a Window element.
	///
	/// Working off the document's shape instead - "the root has exactly one content child" - needs
	/// none of that, and generalises: <c>Flyout</c> is a DependencyObject but not a
	/// FrameworkElement, so it loads and then has nothing to draw, and the same unwrap gives it a
	/// preview too.
	///
	/// The root element itself is left in the source untouched. Nothing here renames or fakes it,
	/// so the outline and the document keep showing the real root; it is simply not the element
	/// being rendered.
	/// </summary>
	static class UnrenderableRootUnwrapper
	{
		/// <summary>
		/// Extracts the root's single content child as a standalone document, or returns false when
		/// the root has no unambiguous content (no child elements, or several - neither of which is
		/// a design surface). Must run BEFORE the application's resource dictionaries are merged in,
		/// or they would be merged onto the root that is about to be discarded.
		/// </summary>
		public static bool TryUnwrapContentRoot(string xaml, out string contentXaml)
		{
			contentXaml = null;
			if (string.IsNullOrEmpty(xaml))
			{
				return false;
			}
			XDocument document;
			try
			{
				document = XDocument.Parse(xaml);
			}
			catch (Exception)
			{
				// Unparseable markup is the real XamlReader error's business to report, not ours.
				return false;
			}
			if (document.Root is not { } root)
			{
				return false;
			}
			// In XAML a dot in an element name means a property element (<Window.Resources>,
			// <Grid.RowDefinitions>); everything else is content.
			var content = root.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
			if (content.Count != 1)
			{
				return false;
			}
			var newRoot = content[0];

			MoveResources(root, newRoot);

			// The declarations live on the old root, and a detached subtree only auto-emits the ones
			// its own element/attribute NAMES use - a prefix that appears solely inside a markup
			// extension or an attribute VALUE ({local:Foo}, "using:App.Converters") would be lost.
			foreach (var declaration in root.Attributes().Where(a => a.IsNamespaceDeclaration))
			{
				if (newRoot.Attribute(declaration.Name) == null)
				{
					newRoot.SetAttributeValue(declaration.Name, declaration.Value);
				}
			}

			newRoot.Remove();
			contentXaml = newRoot.ToString(SaveOptions.DisableFormatting);
			return true;
		}

		/// <summary>
		/// Carries the discarded root's <c>.Resources</c> over to the new one. Without this, any
		/// <c>{StaticResource}</c> in the content that resolved against a resource the outer element
		/// declared becomes unresolvable - which in WinUI throws during parsing, i.e. it would
		/// reintroduce exactly the opaque failure this class exists to remove.
		/// </summary>
		static void MoveResources(XElement root, XElement newRoot)
		{
			var outer = FindResourcesProperty(root);
			if (outer == null)
			{
                return;
			}
			var inner = FindResourcesProperty(newRoot);
			if (inner == null)
			{
				newRoot.AddFirst(new XElement(
					newRoot.Name.Namespace + (newRoot.Name.LocalName + ".Resources"),
					outer.Attributes(),
					outer.Nodes()));
				return;
			}
			// Both declare resources. The outer ones go in as a merged dictionary rather than being
			// poured into the same dictionary: merged entries lose to directly declared ones, which
			// is the precedence the original tree had, and merging them flat would instead throw on
			// any key the two happen to share.
			var dictionary = AsExplicitDictionary(inner);
			var merged = dictionary.Elements()
				.FirstOrDefault(e => e.Name.LocalName == "ResourceDictionary.MergedDictionaries");
			if (merged == null)
			{
				merged = new XElement(dictionary.Name.Namespace + "ResourceDictionary.MergedDictionaries");
				dictionary.AddFirst(merged);
			}
			merged.Add(AsExplicitDictionary(outer));
		}

		static XElement FindResourcesProperty(XElement element)
			=> element.Elements().FirstOrDefault(e =>
				string.Equals(e.Name.LocalName, element.Name.LocalName + ".Resources", StringComparison.Ordinal));

		/// <summary>
		/// The <c>&lt;ResourceDictionary&gt;</c> inside a <c>.Resources</c> property element,
		/// creating one around its entries when the markup used the implicit form
		/// (<c>&lt;Grid.Resources&gt;&lt;SolidColorBrush/&gt;&lt;/Grid.Resources&gt;</c>).
		/// MergedDictionaries can only be expressed on an explicit dictionary.
		/// </summary>
		static XElement AsExplicitDictionary(XElement resourcesProperty)
		{
			var existing = resourcesProperty.Elements().ToList();
			if (existing.Count == 1 && existing[0].Name.LocalName == "ResourceDictionary")
			{
				return existing[0];
			}
			var dictionary = new XElement(
				resourcesProperty.Name.Namespace + "ResourceDictionary",
				resourcesProperty.Attributes(),
				resourcesProperty.Nodes());
			resourcesProperty.RemoveAttributes();
			resourcesProperty.ReplaceNodes(dictionary);
			return dictionary;
		}
	}
}
