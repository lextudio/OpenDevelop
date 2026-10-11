using System.Collections.Generic;
using System.Linq;
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
}
