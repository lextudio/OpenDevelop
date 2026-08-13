using System;
using System.IO;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.WinUIXamlDesigner;

public sealed class WinUIXamlDesignerDisplayBinding : ISecondaryDisplayBinding
{
	public bool ReattachWhenParserServiceIsReady => false;
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
