using System;
using System.Collections.Generic;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WinUIXamlDesigner;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>The Surface renderer's property catalog (the Uno child has none: its call fails and the pad keeps its fixed list).</summary>
sealed partial class UnoDesignRuntimeHost : IWinUIXamlPropertyCatalog
{
	public IReadOnlyList<DesignerPropertyInfo> DescribeProperties(string elementPath)
	{
		if (client == null)
			return Array.Empty<DesignerPropertyInfo>();
		try {
			return client.DescribePropertiesAsync(elementPath).GetAwaiter().GetResult() ?? Array.Empty<DesignerPropertyInfo>();
		} catch (Exception e) {
			LoggingService.Debug("WinUI property catalog unavailable: " + e.GetBaseException().Message);
			return Array.Empty<DesignerPropertyInfo>();
		}
	}
}