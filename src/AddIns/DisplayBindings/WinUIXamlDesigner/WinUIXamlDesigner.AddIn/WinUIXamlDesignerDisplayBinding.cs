using System;
using System.IO;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.WinUIXamlDesigner;

public sealed class WinUIXamlDesignerDisplayBinding : ISecondaryDisplayBinding
{
	// WinUI project evaluation (especially a solution using .slnx plus imported props) can finish
	// after the source view is first opened.  The initial detector result is then Unknown and no
	// secondary Design view is attached; opting into the parser-ready reattach is what lets the
	// already-open document acquire its WinUI designer once project evidence is available.
	public bool ReattachWhenParserServiceIsReady => true;
	public bool CanAttachTo(IViewContent content)
	{
		if (!string.Equals(Path.GetExtension(content?.PrimaryFileName), ".xaml", StringComparison.OrdinalIgnoreCase)) return false;
		var kind = XamlFrameworkDetector.Detect(content.PrimaryFileName.ToString()).Kind;
		return kind is XamlFrameworkKind.WinUI or XamlFrameworkKind.Uno;
	}
	public IViewContent[] CreateSecondaryViewContent(IViewContent viewContent) => new IViewContent[] {
		new WinUIXamlDesignerViewContent(viewContent.PrimaryFile, XamlFrameworkDetector.Detect(viewContent.PrimaryFileName.ToString()))
	};
}
