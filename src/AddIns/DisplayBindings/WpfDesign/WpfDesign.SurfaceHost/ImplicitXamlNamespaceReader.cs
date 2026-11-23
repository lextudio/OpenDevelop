using System.IO;
using System.Xml;

namespace ICSharpCode.WpfDesign.SurfaceHost
{
	/// <summary>The reader for a page's text. With Simpler XAML on, the default and <c>x</c> namespaces are the
	/// reader's ambient context, which is where System.Xaml itself reads prefixes a caller supplies
	/// (XamlReader.Load with a ParserContext); a declaration in the document overrides it, and the text is
	/// never rewritten, so nothing injected is saved back. With it off the reader is a plain one (BAML
	/// projects and XSG projects without Simpler XAML).</summary>
	public static class ImplicitXamlNamespaceReader
	{
		public const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
		public const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

		public static XmlReader Create(TextReader text, bool implicitNamespaces)
		{
			if (!implicitNamespaces)
				return XmlReader.Create(text);
			var names = new NameTable();
			var manager = new XmlNamespaceManager(names);
			manager.AddNamespace("", PresentationNamespace);
			manager.AddNamespace("x", XamlNamespace);
			return XmlReader.Create(text, null, new XmlParserContext(names, manager, null, XmlSpace.None));
		}

		/// <summary>An XmlDocument of a project XAML file that is NOT the design document (App.xaml, a merged
		/// dictionary): its pieces are re-parsed detached from their root, copying the root's xmlns attributes
		/// onto them, so the implicit declarations are made explicit on the root of this in-memory copy. The file
		/// on disk is never touched.</summary>
		public static XmlDocument LoadDocument(string text, bool implicitNamespaces)
		{
			var document = new XmlDocument();
			if (!implicitNamespaces)
			{
				document.LoadXml(text);
				return document;
			}
			using (var reader = Create(new StringReader(text), implicitNamespaces: true))
				document.Load(reader);
			var root = document.DocumentElement;
			if (root != null)
			{
				if (root.GetAttributeNode("xmlns") == null && string.IsNullOrEmpty(root.Prefix))
					root.SetAttribute("xmlns", PresentationNamespace);
				if (root.GetAttributeNode("xmlns:x") == null)
					root.SetAttribute("xmlns:x", XamlNamespace);
			}
			return document;
		}
	}
}
