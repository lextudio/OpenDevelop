using ICSharpCode.Core;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// AddIn.Suspended switches an AddIn's contributions off at run time without removing anything: Codon.BuildItem builds nothing from a suspended
/// AddIn's codons (as if their conditions failed), and switching it back on restores them.
/// </summary>
public class AddInSuspensionTests
{
	sealed class EchoDoozer : IDoozer
	{
		public bool HandleConditions => false;
		public object BuildItem(BuildItemArgs args) => "built:" + args.Codon.Id;
	}

	static AddIn Load(AddInTreeImpl tree, string identity, string ids)
	{
		var xml = $"<AddIn name='{identity}'><Manifest><Identity name='{identity}'/></Manifest><Path name='/Test/Items'>"
			+ string.Concat(ids.Split(',').Select(id => $"<Echo id='{id}'/>")) + "</Path></AddIn>";
		var addIn = AddIn.Load(tree, new StringReader(xml));
		addIn.Enabled = true;
		tree.InsertAddIn(addIn);
		return addIn;
	}

	static AddInTreeImpl NewTree()
	{
		var tree = new AddInTreeImpl(null);
		tree.Doozers.TryAdd("Echo", new EchoDoozer());
		return tree;
	}

	[Fact]
	public void ASuspendedAddIn_BuildsNothing_AndBuildsAgainWhenResumed()
	{
		var tree = NewTree();
		var addIn = Load(tree, "Test.One", "a,b");
		Assert.Equal(new[] { "built:a", "built:b" }, tree.BuildItems<string>("/Test/Items", null));

		addIn.Suspended = true;
		Assert.Empty(tree.BuildItems<string>("/Test/Items", null));
		Assert.True(addIn.Enabled, "suspending is not disabling: the persisted state and the pending action are untouched");
		Assert.Equal(AddInAction.Enable, addIn.Action);

		addIn.Suspended = false;
		Assert.Equal(new[] { "built:a", "built:b" }, tree.BuildItems<string>("/Test/Items", null));
	}

	[Fact]
	public void OnlyTheSuspendedAddInsCodonsAreLeftOut()
	{
		var tree = NewTree();
		var first = Load(tree, "Test.First", "a");
		Load(tree, "Test.Second", "b");

		first.Suspended = true;

		// The other AddIn contributes to the same path and keeps doing so
		Assert.Equal(new[] { "built:b" }, tree.BuildItems<string>("/Test/Items", null));
	}

	[Fact]
	public void BuildItem_OfASuspendedAddIn_YieldsNull()
	{
		var tree = NewTree();
		var addIn = Load(tree, "Test.One", "a");
		Assert.Equal("built:a", tree.BuildItem("/Test/Items/a", null));
		addIn.Suspended = true;
		Assert.Null(tree.BuildItem("/Test/Items/a", null));
	}
}
