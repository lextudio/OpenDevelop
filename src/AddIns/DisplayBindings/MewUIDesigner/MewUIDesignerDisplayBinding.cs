using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.MewUIDesigner;

public sealed class MewUIDesignerDisplayBinding : ISecondaryDisplayBinding
{
	public bool ReattachWhenParserServiceIsReady => false;
	public bool CanAttachTo(IViewContent content)
	{
		if (!string.Equals(Path.GetExtension(content?.PrimaryFileName), ".mxaml", StringComparison.OrdinalIgnoreCase)) return false;
		var text = content.GetService<ICSharpCode.SharpDevelop.Editor.ITextEditor>()?.Document.Text;
		if (string.IsNullOrWhiteSpace(text)) return false;
		try {
			var doc = XDocument.Parse(text);
			return doc.Root?.Name.LocalName == "Window" && !string.IsNullOrEmpty((string?)doc.Root.Attribute("Class"));
		} catch { return false; }
	}
	public IViewContent[] CreateSecondaryViewContent(IViewContent content) =>
		content.SecondaryViewContents.Any(v => v is MewUIDesignerViewContent)
			? Array.Empty<IViewContent>() : new IViewContent[] { new MewUIDesignerViewContent(content.PrimaryFile) };
}
