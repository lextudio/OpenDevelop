using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.GtkDesigner;

public sealed class GtkDesignerDisplayBinding : ISecondaryDisplayBinding
{
	public bool ReattachWhenParserServiceIsReady => false;
	public bool CanAttachTo(IViewContent content)
	{
		if (!string.Equals(Path.GetExtension(content?.PrimaryFileName), ".ui", StringComparison.OrdinalIgnoreCase)) return false;
		var text = content.GetService<ITextEditor>()?.Document.Text;
		if (string.IsNullOrWhiteSpace(text)) return false;
		try {
			var doc = XDocument.Parse(text);
			return doc.Root?.Name.LocalName == "interface" && doc.Root.Elements().Any(e => e.Name.LocalName == "requires"
				&& (string?)e.Attribute("lib") == "gtk" && ((string?)e.Attribute("version") ?? "").StartsWith("4", StringComparison.Ordinal));
		} catch { return false; }
	}
	public IViewContent[] CreateSecondaryViewContent(IViewContent content) => new IViewContent[] { new GtkDesignerViewContent(content.PrimaryFile) };
}
