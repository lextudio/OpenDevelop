using System;
using System.IO;
using System.Linq;
using System.Xml;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.WorkflowDesigner;

/// <summary>Attaches to a .xaml file whose root element is an <c>Activity</c> in the WF
/// activities namespace - the same sniff the dead SharpDevelop-era addin used
/// (doc/technotes/workflow-designer.md), and the exact root name/namespace
/// <c>WpfDisplayBinding.CanAttachTo</c> already excludes itself on, so this and the WPF
/// designer never fight over the same .xaml file.</summary>
public sealed class WorkflowDesignerDisplayBinding : ISecondaryDisplayBinding
{
	const string ActivitiesNamespace = "http://schemas.microsoft.com/netfx/2009/xaml/activities";

	public bool ReattachWhenParserServiceIsReady => false;

	public bool CanAttachTo(IViewContent content)
	{
		if (!string.Equals(Path.GetExtension(content?.PrimaryFileName), ".xaml", StringComparison.OrdinalIgnoreCase)) return false;
		var text = content!.GetService<ICSharpCode.SharpDevelop.Editor.ITextEditor>()?.Document.Text;
		if (string.IsNullOrWhiteSpace(text)) return false;
		try {
			using var reader = new XmlTextReader(new StringReader(text)) { XmlResolver = null };
			while (reader.Read() && reader.NodeType != XmlNodeType.Element) { }
			return reader.LocalName == "Activity" && reader.NamespaceURI == ActivitiesNamespace;
		} catch (XmlException) {
			return false;
		}
	}

	public IViewContent[] CreateSecondaryViewContent(IViewContent content) =>
		content.SecondaryViewContents.Any(v => v is WorkflowDesignerViewContent)
			? Array.Empty<IViewContent>() : new IViewContent[] { new WorkflowDesignerViewContent(content.PrimaryFile) };
}
