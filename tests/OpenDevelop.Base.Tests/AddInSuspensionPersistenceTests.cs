using System.Xml.Linq;
using ICSharpCode.Core;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// A suspended AddIn stays suspended across restarts until it is resumed explicitly: AddInManager.Suspend remembers it in the AddIn configuration file
/// (AddIns.xml, a &lt;Suspend addin='identity'/&gt; element) and CoreStartup applies it right after the AddIns are loaded.
/// The configuration file name is a static of AddInManager, hence the collection.
/// </summary>
[Collection("AddInManager configuration file")]
public sealed class AddInSuspensionPersistenceTests : IDisposable
{
	readonly string file = Path.Combine(Path.GetTempPath(), "od-addins-" + Guid.NewGuid().ToString("N") + ".xml");
	readonly string previous = AddInManager.ConfigurationFileName;

	public AddInSuspensionPersistenceTests()
	{
		AddInManager.ConfigurationFileName = file;
	}

	public void Dispose()
	{
		AddInManager.ConfigurationFileName = previous;
		File.Delete(file);
	}

	sealed class EchoDoozer : IDoozer
	{
		public bool HandleConditions => false;
		public object BuildItem(BuildItemArgs args) => "built:" + args.Codon.Id;
	}

	/// <summary>One "run" of the application: a fresh AddIn tree with the AddIn loaded, as at startup.</summary>
	static (AddInTreeImpl Tree, AddIn AddIn) Start(string identity, string id)
	{
		var tree = new AddInTreeImpl(null);
		tree.Doozers.TryAdd("Echo", new EchoDoozer());
		var addIn = AddIn.Load(tree, new StringReader(
			$"<AddIn name='{identity}'><Manifest><Identity name='{identity}'/></Manifest><Path name='/Test/Items'><Echo id='{id}'/></Path></AddIn>"));
		addIn.Enabled = true;
		tree.InsertAddIn(addIn);
		return (tree, addIn);
	}

	IReadOnlyList<string> Suspended()
	{
		if (!File.Exists(file))
			return Array.Empty<string>();
		return XDocument.Load(file).Root!.Elements("Suspend").Select(e => (string)e.Attribute("addin")!).ToList();
	}

	[Fact]
	public void Suspend_TakesEffectAtOnce_AndIsRemembered()
	{
		var (tree, addIn) = Start("Test.One", "a");

		AddInManager.Suspend(new[] { addIn });

		Assert.True(addIn.Suspended);
		Assert.Empty(tree.BuildItems<string>("/Test/Items", null));
		Assert.Equal(new[] { "Test.One" }, Suspended());
	}

	[Fact]
	public void ASuspendedAddIn_IsStillSuspendedAfterARestart_UntilItIsResumed()
	{
		var (_, first) = Start("Test.One", "a");
		AddInManager.Suspend(new[] { first });

		// Restart: nothing is carried over but the file. As at startup, the AddIns are loaded and the remembered state applied.
		var (secondTree, second) = Start("Test.One", "a");
		Assert.True(second.Enabled && !second.Suspended, "a freshly loaded AddIn is not suspended by itself");
		AddInManager.ApplySuspendedConfiguration(secondTree);
		Assert.True(second.Suspended);
		Assert.Empty(secondTree.BuildItems<string>("/Test/Items", null));

		// ...and again after another restart: it stays suspended until it is resumed explicitly
		var (thirdTree, third) = Start("Test.One", "a");
		AddInManager.ApplySuspendedConfiguration(thirdTree);
		Assert.True(third.Suspended);

		AddInManager.Resume(new[] { third });
		Assert.False(third.Suspended);
		Assert.Equal(new[] { "built:a" }, thirdTree.BuildItems<string>("/Test/Items", null));
		Assert.Empty(Suspended());

		var (fourthTree, fourth) = Start("Test.One", "a");
		AddInManager.ApplySuspendedConfiguration(fourthTree);
		Assert.False(fourth.Suspended);
	}

	[Fact]
	public void EnablingOrDisablingAnotherAddIn_DoesNotResumeTheSuspendedOnes()
	{
		var (_, suspended) = Start("Test.Suspended", "a");
		var (_, other) = Start("Test.Other", "b");
		AddInManager.Suspend(new[] { suspended });

		// These rewrite the whole file through the overload that did not know about suspended AddIns
		AddInManager.Disable(new[] { other });
		Assert.Equal(new[] { "Test.Suspended" }, Suspended());
		AddInManager.Enable(new[] { other });
		Assert.Equal(new[] { "Test.Suspended" }, Suspended());
	}

	[Fact]
	public void ResumingOneAddIn_LeavesTheOthersSuspended()
	{
		var (_, one) = Start("Test.One", "a");
		var (_, two) = Start("Test.Two", "b");
		AddInManager.Suspend(new[] { one, two });

		AddInManager.Resume(new[] { one });

		Assert.False(one.Suspended);
		Assert.True(two.Suspended);
		Assert.Equal(new[] { "Test.Two" }, Suspended());
	}

	[Fact]
	public void WithoutAConfigurationFile_NothingIsSuspended()
	{
		var (tree, addIn) = Start("Test.One", "a");

		AddInManager.ApplySuspendedConfiguration(tree);

		Assert.False(addIn.Suspended);
		Assert.Equal(new[] { "built:a" }, tree.BuildItems<string>("/Test/Items", null));
	}
}
