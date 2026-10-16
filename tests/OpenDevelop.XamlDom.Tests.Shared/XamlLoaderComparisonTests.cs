using System.IO;
using System.Windows.Markup;
using ICSharpCode.WpfDesign.XamlDom;
using Xunit;

namespace OpenDevelop.XamlDom.Tests;

public sealed class XamlLoaderComparisonTests
{
	/// <summary>Loads on its own thread with a deadline, so a loader that hangs on one input is reported
	/// as a difference instead of stalling the whole run.</summary>
	static string Load(string xaml, bool designer)
	{
		string? result = null;
		var thread = new Thread(() => {
			try { result = LoadCore(xaml, designer); }
			finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
		}) { IsBackground = true };
		if (TestHost.IsWindows)
			thread.SetApartmentState(ApartmentState.STA); // real WPF refuses to build UI objects off an STA thread
		thread.Start();
		return thread.Join(TimeSpan.FromSeconds(20)) ? result! : "HANG (no result within 20s)";
	}

	static string LoadCore(string xaml, bool designer)
	{
		try {
			object? root = designer
				? XamlParser.Parse(new StringReader(xaml)).RootInstance
				: XamlReader.Parse(xaml);
			return XamlGraphDump.Dump(root);
		} catch (Exception e) {
			return "EXCEPTION " + e.GetType().Name + ": " + e.GetBaseException().Message;
		}
	}

	static string Diff(string expected, string actual)
	{
		var e = expected.Split('\n');
		var a = actual.Split('\n');
		var missing = e.Except(a).Take(6).Select(l => "    - " + l.TrimEnd());
		var extra = a.Except(e).Take(6).Select(l => "    + " + l.TrimEnd());
		return string.Join("\n", missing.Concat(extra));
	}

	/// <summary>Cases where XamlDom still builds a different graph than Microsoft's loader, with why. A case
	/// that starts to match must be removed from here, so the list only ever shrinks.</summary>
	static readonly Dictionary<string, string> KnownDifferences = new() {
		["ControlTemplate and DataTemplate"] = "a DataTemplate with only DataType gets no implicit DataTemplateKey",
		["Content property and text"] = "inline text between Run/Italic elements is trimmed (whitespace-only Runs are dropped, 'Hello ' becomes 'Hello')",
		["x:Name x:Uid x:Key forms"] = "x:Uid is not applied to the instance",
		["Uri and Key as attribute order"] = "x:Uid is not applied to the instance (Image.Source local value also differs)",
		["Triggers"] = "DataTrigger.Binding is null: a {Binding} assigned to a trigger's Binding property is lost",
		["ToolTip, ContextMenu and Menu"] = "MenuItem.Role differs: items are attached to their parent after the subtree is built, so Role is computed against the wrong parent",
	};

	public static IEnumerable<TheoryDataRow<string>> CaseNames() => XamlCorpus.Cases.Select(c => new TheoryDataRow<string>(c.Name));

	[Theory]
	[MemberData(nameof(CaseNames))]
	public void LoadsTheSameAsMicrosoftsXamlReader(string name)
	{
		var body = XamlCorpus.Cases.Single(c => c.Name == name).Body;
		var xaml = XamlCorpus.Prefix + "\n" + body + "\n</StackPanel>";
		var microsoft = Load(xaml, designer: false);
		var designer = Load(xaml, designer: true);
		// The reference load must succeed, or "both loaders fail alike" would count as a match.
		Assert.False(microsoft.StartsWith("EXCEPTION") || microsoft.StartsWith("HANG"), "Microsoft's XamlReader did not load the case: " + microsoft);
		// A case XamlDom cannot load is listed in XamlCorpus.NotLoadableByXamlDom, and only those.
		var designerFailed = designer.StartsWith("EXCEPTION") || designer.StartsWith("HANG");
		if (XamlCorpus.NotLoadableByXamlDom.TryGetValue(name, out var notLoadable)) {
			Assert.True(designerFailed, "XamlDom now loads this case; remove it from XamlCorpus.NotLoadableByXamlDom (was: " + notLoadable + ")");
			return;
		}
		Assert.False(designerFailed, "XamlDom failed to load a case that is not listed as not loadable: " + designer);
		if (KnownDifferences.TryGetValue(name, out var reason)) {
			Assert.True(microsoft != designer, "This case now matches; remove it from KnownDifferences (was: " + reason + ")");
			return;
		}
		Assert.True(microsoft == designer, "XamlDom differs from XamlReader (- Microsoft, + XamlDom):\n" + Diff(microsoft, designer));
	}
}
