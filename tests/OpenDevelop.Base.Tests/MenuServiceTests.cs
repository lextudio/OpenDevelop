using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Tests in this collection replace the process-wide SharpDevelop service container (the AddIn tree),
/// so they must not overlap another collection that may be reading the current one.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalServicesCollection
{
    public const string Name = "Global services";
}

/// <summary>
/// MenuService builds a type="Menu" item with one dummy child and fills its children inside that
/// item's SubmenuOpened. SubmenuOpened bubbles, so the handler also runs for every descendant that
/// opens; rebuilding an ancestor then removed the item whose popup was opening (Help &gt; Web &gt;
/// SharpDevelop never showed a third level and closed the whole chain). Only the item's own event
/// may replace its children.
/// </summary>
[Collection(GlobalServicesCollection.Name)]
public sealed class MenuServiceTests : IDisposable
{
    const string Manifest = """
<AddIn name="MenuTest">
  <Manifest><Identity name="MenuTest" version="1.0"/></Manifest>
  <Path name="/Test/Menu">
    <MenuItem id="Top" label="Top" type="Menu">
      <MenuItem id="Mid" label="Mid" type="Menu">
        <MenuItem id="Leaf" label="Leaf" type="Menu">
          <MenuItem id="LeafItem" label="Leaf Item" link="https://example.com/"/>
        </MenuItem>
      </MenuItem>
    </MenuItem>
  </Path>
</AddIn>
""";

    public MenuServiceTests() => SD.InitializeForUnitTests();

    public void Dispose() => SD.TearDownForUnitTests();

    [Xunit.UIFact]
    public void ADescendantsSubmenuOpened_DoesNotRebuildItsAncestors()
    {
        var tree = (AddInTreeImpl)SD.GetRequiredService<IAddInTree>();
        AddIn addIn = AddIn.Load(tree, new StringReader(Manifest));
        addIn.Enabled = true;
        tree.InsertAddIn(addIn);

        var owner = new Border();
        var items = MenuService.CreateMenuItems(owner, null, "/Test/Menu", "Test");
        var top = Assert.IsAssignableFrom<MenuItem>(Assert.Single(items));

        // Open each level the way WPF does; a type="Menu" item only gets its real children here.
        top.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, top));
        var mid = Assert.IsAssignableFrom<MenuItem>(Assert.Single(top.Items.OfType<MenuItem>()));
        Assert.Equal("Mid", mid.Header);
        mid.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, mid));
        var leaf = Assert.IsAssignableFrom<MenuItem>(Assert.Single(mid.Items.OfType<MenuItem>()));
        Assert.Equal("Leaf", leaf.Header);

        int midChanges = 0, topChanges = 0;
        ((INotifyCollectionChanged)mid.Items).CollectionChanged += (_, _) => midChanges++;
        ((INotifyCollectionChanged)top.Items).CollectionChanged += (_, _) => topChanges++;

        leaf.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, leaf));

        Assert.Equal(0, midChanges);
        Assert.Equal(0, topChanges);
        Assert.Contains(leaf, mid.Items.OfType<MenuItem>());
        Assert.Contains(mid, top.Items.OfType<MenuItem>());
        Assert.Equal("Leaf Item", Assert.Single(leaf.Items.OfType<MenuItem>()).Header);
    }

    [Xunit.UIFact]
    public void OpeningAMenu_ReplacesOnlyThatItemsOwnChildren()
    {
        var tree = (AddInTreeImpl)SD.GetRequiredService<IAddInTree>();
        AddIn addIn = AddIn.Load(tree, new StringReader(Manifest));
        addIn.Enabled = true;
        tree.InsertAddIn(addIn);

        var items = MenuService.CreateMenuItems(new Border(), null, "/Test/Menu", "Test");
        var top = Assert.IsAssignableFrom<MenuItem>(Assert.Single(items));

        // The dummy child exists so the item renders as a header before its first open.
        Assert.Single(top.Items);
        top.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, top));
        Assert.Equal("Mid", Assert.Single(top.Items.OfType<MenuItem>()).Header);
    }
}
