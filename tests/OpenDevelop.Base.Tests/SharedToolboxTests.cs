using System.Collections.Generic;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Surface;
using ICSharpCode.SharpDevelop.Gui;
using Xunit;
using Fact = Xunit.UIFactAttribute;

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

	[Fact]
	public void ConsumerFilters_CustomRule_UsesTheActiveDesignersEvaluatorWithoutLoadingThePayload()
	{
		var toolbox = SharedToolbox.Instance;
		const string scope = "base-test-filter-custom-evaluator";
		var permittedPayload = new object();
		var rejectedPayload = new object();
		toolbox.AddItems(new[] {
			new SharedToolboxItem("Controls", "Existing require", scope,
				filters: new[] { Rule("designer.form", "Require") }),
			new SharedToolboxItem("Controls", "Permitted custom", scope, payload: permittedPayload,
				filters: new[] { Rule("designer.document", "Custom") }),
			new SharedToolboxItem("Controls", "Rejected custom", scope, payload: rejectedPayload,
				filters: new[] { Rule("designer.document", "Custom") })
		});

		object? seenPayload = null;
		toolbox.SetActiveScopes(owner: new object(), selectFirst: false, categoryPriorities: null,
			consumerFilters: Array.Empty<DesignerToolboxFilterInfo>(),
			customFilterEvaluator: (item, rule) => {
				Assert.Equal("designer.document", rule.FilterString);
				seenPayload = item.Payload;
				return ReferenceEquals(item.Payload, permittedPayload);
			}, scopes: new[] { scope });

		Assert.Equal(new[] { "Existing require", "Permitted custom" }, toolbox.VisibleItems.Where(item => item.Scope == scope).Select(item => item.DisplayName));
		Assert.Same(rejectedPayload, seenPayload);
	}

	[Fact]
	public void ConsumerFilters_CustomEvaluatorFailure_PreservesTheExistingVisibleRow()
	{
		var toolbox = SharedToolbox.Instance;
		const string scope = "base-test-filter-custom-evaluator-failure";
		toolbox.AddItems(new[] {
			new SharedToolboxItem("Controls", "Custom", scope, filters: new[] { Rule("designer.document", "Custom") })
		});

		toolbox.SetActiveScopes(owner: new object(), selectFirst: false, categoryPriorities: null,
			consumerFilters: Array.Empty<DesignerToolboxFilterInfo>(),
			customFilterEvaluator: (_, _) => throw new InvalidOperationException("test evaluator failure"), scopes: new[] { scope });

		Assert.Equal(new[] { "Custom" }, toolbox.VisibleItems.Where(item => item.Scope == scope).Select(item => item.DisplayName));
	}

	[Fact]
	public void DesignerToolboxScope_CustomEvaluatorFiltersItsMetadataAndReactivationKeepsTheSameCallback()
	{
		const string scopeName = "base-test-designer-toolbox-custom-evaluator";
		var scope = new DesignerToolboxScope(new object(), mapper: null);
		scope.Register(scopeName, new[] {
			new DesignerToolboxItemInfo { Name = "Allowed", DisplayName = "Allowed", TypeName = "test.Allowed", Filters = { Rule("designer.document", "Custom") } },
			new DesignerToolboxItemInfo { Name = "Rejected", DisplayName = "Rejected", TypeName = "test.Rejected", Filters = { Rule("designer.document", "Custom") } }
		});
		scope.SetScopes(scopeName);
		var evaluations = 0;
		scope.SetCustomFilterEvaluator((item, filter) => {
			evaluations++;
			Assert.Equal("designer.document", filter.FilterString);
			return item.Name == "Allowed";
		});

		scope.Activate();
		Assert.Equal(1, scope.VisibleItemCount);
		Assert.True(scope.Select("test.Allowed"));
		evaluations = 0;
		scope.Activate();

		Assert.Equal(0, evaluations);
		Assert.Equal("Allowed", scope.SelectedItem!.Name);
	}

	[Fact]
	public void DesignerVocabulary_WpfOnlyControlIsOfferedToWpfAndHiddenFromWinForms()
	{
		var toolbox = SharedToolbox.Instance;
		const string scope = "base-test-filter-vocabulary";
		// The attributes of the WPF toolbox discovery fixture (GreetingBadge).
		toolbox.AddItems(new[] {
			new SharedToolboxItem("Controls", "Plain", scope),
			new SharedToolboxItem("Controls", "WpfOnly", scope, filters: new[] {
				Rule(DesignerToolboxFilters.WpfDesigner, "Allow"),
				Rule(DesignerToolboxFilters.WinFormsDesigner, "Prevent"),
				Rule(DesignerToolboxFilters.WpfSurface, "Require")
			})
		});

		Assert.Equal(new[] { "Plain", "WpfOnly" }, VisibleNames(toolbox, scope, DesignerToolboxFilters.Wpf.ToArray()));
		Assert.Equal(new[] { "Plain" }, VisibleNames(toolbox, scope, DesignerToolboxFilters.WinForms.ToArray()));
	}
}
