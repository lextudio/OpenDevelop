using System;
using System.ComponentModel;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.MewUIDesigner;

public sealed class MewUIPropertyAdapter
{
	readonly DesignerElementNode node;
	readonly Action<string, string> set;
	public MewUIPropertyAdapter(DesignerElementNode node, Action<string, string> set) { this.node = node; this.set = set; }
	[Category("Identity"), ReadOnly(true)] public string Type => node.Type;
	[Category("Identity")] public string Name { get => node.Name ?? node.Id; set => set("$name", value); }
	[Category("Common")] public string Text { get => Get("Text", ""); set => set("Text", value); }
	[Category("Common")] public string Content { get => Get("Content", ""); set => set("Content", value); }
	[Category("Layout")] public string Margin { get => Get("Margin", ""); set => set("Margin", value); }
	[Category("Layout")] public string Padding { get => Get("Padding", ""); set => set("Padding", value); }
	[Category("Layout")] public string Spacing { get => Get("Spacing", ""); set => set("Spacing", value); }
	[Category("Appearance")] public string Background { get => Get("Background", ""); set => set("Background", value); }
	[Category("Appearance")] public string Foreground { get => Get("Foreground", ""); set => set("Foreground", value); }
	[Category("Behavior")] public string IsEnabled { get => Get("IsEnabled", "true"); set => set("IsEnabled", value); }
	string Get(string key, string fallback) => node.Properties.FirstOrDefault(p => p.Name == key)?.Value ?? fallback;
}
