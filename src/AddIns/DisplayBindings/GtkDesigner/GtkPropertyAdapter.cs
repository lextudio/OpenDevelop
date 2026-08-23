using System;
using System.ComponentModel;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.GtkDesigner;

public sealed class GtkPropertyAdapter
{
	readonly DesignerElementNode node; readonly Action<string, string> set;
	public GtkPropertyAdapter(DesignerElementNode node, Action<string, string> set) { this.node = node; this.set = set; }
	[Category("Identity")] public string Id { get => node.Id; set => set("$id", value); }
	[Category("Identity"), ReadOnly(true)] public string Class => node.Type;
	[Category("Common")] public string Label { get => Get("label"); set => set("label", value); }
	[Category("Common"), DisplayName("Placeholder text")] public string PlaceholderText { get => Get("placeholder-text"); set => set("placeholder-text", value); }
	[Category("Window")] public string Title { get => Get("title"); set => set("title", value); }
	[Category("Layout")] public string Orientation { get => Get("orientation", "horizontal"); set => set("orientation", value); }
	[Category("Layout")] public string Spacing { get => Get("spacing", "0"); set => set("spacing", value); }
	[Category("Layout"), DisplayName("Margin start")] public string MarginStart { get => Get("margin-start", "0"); set => set("margin-start", value); }
	[Category("Layout"), DisplayName("Margin end")] public string MarginEnd { get => Get("margin-end", "0"); set => set("margin-end", value); }
	[Category("Behavior")] public string Sensitive { get => Get("sensitive", "True"); set => set("sensitive", value.ToLowerInvariant()); }
	[Category("Behavior")] public string Visible { get => Get("visible", "True"); set => set("visible", value.ToLowerInvariant()); }
	string Get(string name, string fallback = "") => node.Properties.FirstOrDefault(p => p.Name == name)?.Value ?? fallback;
}
