using System.Collections.Generic;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Gui;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class SharedToolboxTests
{
	[Fact]
	public void CategoryPriority_ReordersCategoriesButPreservesItemRegistrationOrder()
	{
		var toolbox = SharedToolbox.Instance;
		const string scope = "base-test-category-priority";
		toolbox.AddItems(new[] {
			new SharedToolboxItem("Inputs", "First input", scope),
			new SharedToolboxItem("Containers", "First container", scope),
			new SharedToolboxItem("Inputs", "Second input", scope)
		});

		toolbox.SetActiveScopes(owner: new object(), selectFirst: false,
			categoryPriorities: new Dictionary<string, int> { ["Containers"] = 0 }, scopes: new[] { scope });
		var visible = toolbox.VisibleItems.Where(item => item.Scope == scope).ToArray();

		Assert.Equal(new[] { "First container", "First input", "Second input" }, visible.Select(item => item.DisplayName));
	}

	static DesignerToolboxFilterInfo Rule(string text, string type) => new() { FilterString = text, FilterType = type };

	static string[] VisibleNames(SharedToolbox toolbox, string scope, params DesignerToolboxFilterInfo[] consumerFilters)
	{
		toolbox.SetActiveScopes(owner: new object(), selectFirst: false, categoryPriorities: null,
			consumerFilters: consumerFilters, scopes: new[] { scope });
		return toolbox.VisibleItems.Where(item => item.Scope == scope).Select(item => item.DisplayName).ToArray();
	}

	[Fact]
	public void ConsumerFilters_RequireShowsItemOnlyWhenConsumerDeclaresTheFilter()
	{
		var toolbox = SharedToolbox.Instance;
		const string scope = "base-test-filter-require";
		toolbox.AddItems(new[] {
			new SharedToolboxItem("Controls", "Plain", scope),
			new SharedToolboxItem("Controls", "Needs form", scope, filters: new[] { Rule("designer.form", "Require") })
		});

		Assert.Equal(new[] { "Plain", "Needs form" }, VisibleNames(toolbox, scope, Rule("designer.form", "Require")));
		Assert.Equal(new[] { "Plain" }, VisibleNames(toolbox, scope, Rule("designer.other", "Require")));
	}

	[Fact]
	public void ConsumerFilters_PreventHidesItemWhenConsumerDeclaresTheFilter_CaseInsensitively()
	{
		var toolbox = SharedToolbox.Instance;
		const string scope = "base-test-filter-prevent";
		toolbox.AddItems(new[] {
			new SharedToolboxItem("Controls", "Plain", scope),
			new SharedToolboxItem("Controls", "Not in web", scope, filters: new[] { Rule("Designer.Web", "Prevent") })
		});

		Assert.Equal(new[] { "Plain" }, VisibleNames(toolbox, scope, Rule("designer.web", "Require")));
		Assert.Equal(new[] { "Plain", "Not in web" }, VisibleNames(toolbox, scope, Rule("designer.form", "Require")));
	}

	[Fact]
	public void ConsumerFilters_NoneDeclared_OrCustomRule_NeverHidesAnItem()
	{
		var toolbox = SharedToolbox.Instance;
		const string scope = "base-test-filter-neutral";
		toolbox.AddItems(new[] {
			new SharedToolboxItem("Controls", "Needs form", scope, filters: new[] { Rule("designer.form", "Require") }),
			new SharedToolboxItem("Controls", "Custom", scope, filters: new[] { Rule("designer.custom", "Custom") })
		});

		// A designer that has not opted in must keep today's behaviour.
		Assert.Equal(new[] { "Needs form", "Custom" }, VisibleNames(toolbox, scope));
		// Custom needs a designer evaluator, so it must not hide a row on its own.
		Assert.Equal(new[] { "Needs form", "Custom" }, VisibleNames(toolbox, scope, Rule("designer.form", "Require")));
	}
}
