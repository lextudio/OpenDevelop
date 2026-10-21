using OpenDevelop.Profiler.Heap;
using Xunit;

namespace OpenDevelop.Profiler.Tests;

public class HeapGraphTests
{
    // Types: 0 Holder, 1 List, 2 Array, 3 Item, 4 Orphan.
    // Objects: 0 Holder -> 1 List -> 2 Array -> {3 Item, 4 Item}; 5 Orphan -> 3 Item; 6 Item (nothing refers to it).
    // Roots: object 0 (a static), object 5 is NOT rooted, so it is garbage waiting to be collected.
    static HeapGraph Build(int[]? roots = null) => new(
        ["Holder", "List", "Array", "Item", "Orphan"],
        nodeType: [0, 1, 2, 3, 3, 4, 3],
        edgeStart: [0, 1, 2, 4, 4, 4, 5, 5],
        edgeTo: [1, 2, 3, 4, 3],
        roots: roots ?? [0],
        rootKinds: (roots ?? [0]).Select(_ => "StaticVar").ToArray());

    // The same graph with sizes and addresses: object i is 0x1000 + i and weighs Sizes[i] bytes.
    static readonly long[] Sizes = [10, 20, 30, 1, 2, 5, 100];

    static HeapGraph BuildSized(int[]? roots = null) => new(
        ["Holder", "List", "Array", "Item", "Orphan"],
        nodeType: [0, 1, 2, 3, 3, 4, 3],
        edgeStart: [0, 1, 2, 4, 4, 4, 5, 5],
        edgeTo: [1, 2, 3, 4, 3],
        roots: roots ?? [0],
        rootKinds: (roots ?? [0]).Select(_ => "StaticVar").ToArray(),
        nodeAddress: Enumerable.Range(0, 7).Select(i => 0x1000UL + (ulong)i).ToArray(),
        nodeSize: Sizes);

    [Fact]
    public void RetainedByType_SumsWhatEachTypeKeepsAlive_LeavingOutGarbage()
    {
        var retained = BuildSized().RetainedByType().ToDictionary(t => t.TypeName, t => t.RetainedSize);

        // Holder keeps everything reachable: 10 + 20 + 30 + 1 + 2. The orphan (5) and the unreferenced item (100) are garbage.
        Assert.Equal(63, retained["Holder"]);
        Assert.Equal(53, retained["List"]);
        Assert.Equal(33, retained["Array"]);
        Assert.Equal(3, retained["Item"]); // items 3 and 4 only; item 6 is unreachable
        Assert.DoesNotContain("Orphan", retained.Keys);
        Assert.Equal("Holder", BuildSized().RetainedByType()[0].TypeName);
    }

    [Fact]
    public void RetainedByType_DoesNotCountAChainOfTheSameTypeOncePerLink()
    {
        // Node -> Node -> Node, rooted at the first: the first retains all three; the others are inside it.
        var graph = new HeapGraph(
            ["Node"], nodeType: [0, 0, 0], edgeStart: [0, 1, 2, 2], edgeTo: [1, 2], roots: [0], rootKinds: ["Local"],
            nodeAddress: [1, 2, 3], nodeSize: [10, 10, 10]);

        Assert.Equal(30, graph.RetainedByType().Single().RetainedSize);
    }

    [Fact]
    public void Dominators_AnObjectReachedByTwoRootsIsRetainedByNeither()
    {
        // Two roots a (0) and b (1) both reference c (2): freeing either leaves c alive.
        var graph = new HeapGraph(
            ["A", "B", "C"], nodeType: [0, 1, 2], edgeStart: [0, 1, 2, 2], edgeTo: [2, 2], roots: [0, 1],
            rootKinds: ["Local", "Local"], nodeAddress: [1, 2, 3], nodeSize: [10, 20, 40]);

        var retained = graph.RetainedByType().ToDictionary(t => t.TypeName, t => t.RetainedSize);

        Assert.Equal((10, 20, 40), (retained["A"], retained["B"], retained["C"]));
    }

    [Fact]
    public void Instances_ListsTheLargestRetainersOfATypeFirst()
    {
        var items = BuildSized().Instances("Array", 5);

        var array = Assert.Single(items);
        Assert.Equal((2, 0x1002UL, 30L, 33L), (array.Node, array.Address, array.Size, array.RetainedSize));
        Assert.Equal([4, 3], BuildSized().Instances("Item", 5).Select(i => i.Node)); // 4 retains 2 bytes, 3 retains 1; 6 is garbage
        Assert.Single(BuildSized().Instances("Item", 1));
        Assert.Empty(BuildSized().Instances("NoSuchType", 5));
    }

    [Fact]
    public void PathToRoot_OfOneInstanceFollowsItsOwnChain()
    {
        var path = BuildSized().PathToRoot(4);

        Assert.NotNull(path);
        Assert.Equal(["Holder", "List", "Array", "Item"], path.Types);
        Assert.Null(BuildSized().PathToRoot(6)); // nothing rooted reaches it
    }

    [Theory]
    [InlineData("System.EventHandler", true)]
    [InlineData("System.EventHandler`1[System.EventArgs]", true)]
    [InlineData("System.Action", true)]
    [InlineData("System.Action`2[System.Int32,System.String]", true)]
    [InlineData("System.Func`1[System.Int32]", true)]
    [InlineData("System.ComponentModel.PropertyChangedEventHandler", true)]
    [InlineData("System.Collections.Specialized.NotifyCollectionChangedEventHandler", true)]
    [InlineData("System.MulticastDelegate", true)]
    [InlineData("System.String", false)]
    [InlineData("MyApp.HandlerRegistry", false)]
    [InlineData("System.Collections.Generic.List`1[System.Action]", false)]
    public void IsDelegateType_RecognisesDelegatesByName(string typeName, bool expected) =>
        Assert.Equal(expected, HeapGraph.IsDelegateType(typeName));

    [Fact]
    public void LargestRetainers_ListsTheObjectsKeepingTheMostAliveAcrossTypes()
    {
        var top = BuildSized().LargestRetainers(3);

        Assert.Equal(["Holder", "List", "Array"], top.Select(o => o.TypeName));
        Assert.Equal([63L, 53L, 33L], top.Select(o => o.Instance.RetainedSize));
    }

    // Types: 0 Root, 1 Publisher, 2 EventHandler, 3 Subscriber, 4 Handler[] (the invocation list).
    // Root -> Publisher -> EventHandler (the combined delegate) -> Handler[] -> {EventHandler, EventHandler}, each -> Subscriber.
    static HeapGraph BuildSubscriptions() => new(
        ["Root", "Publisher", "System.EventHandler", "Subscriber", "System.EventHandler[]"],
        nodeType: [0, 1, 2, 4, 2, 2, 3, 3, 3],
        edgeStart: [0, 1, 2, 3, 5, 6, 7, 7, 7, 7],
        edgeTo: [1, 2, 3, 4, 5, 6, 7, 6],
        roots: [0], rootKinds: ["StaticVar"],
        nodeAddress: Enumerable.Range(0, 9).Select(i => 0x2000UL + (ulong)i).ToArray(),
        nodeSize: [8, 8, 32, 40, 64, 64, 100, 100, 5]);

    [Fact]
    public void DelegateTargets_CountsTheSubscribersEventHandlersKeepAlive()
    {
        var targets = BuildSubscriptions().DelegateTargets(5);

        // Two handlers (nodes 4 and 5) point at subscribers 6 and 7: one each. The combined delegate and the
        // invocation array are plumbing, and node 8 (a subscriber nothing points at) is not a target.
        Assert.Equal([("Subscriber", 2L)], targets.Select(t => (t.TypeName, t.Delegates)));
        Assert.Equal(200, targets[0].RetainedSize); // subscribers 6 and 7, 100 bytes each
    }

    [Fact]
    public void DelegateTargets_IgnoresDelegatesNothingKeepsAlive()
    {
        // The same graph with no GC root: nothing is reachable, so nothing is held.
        var graph = new HeapGraph(
            ["Root", "Publisher", "System.EventHandler", "Subscriber", "System.EventHandler[]"],
            nodeType: [0, 1, 2, 4, 2, 2, 3, 3, 3],
            edgeStart: [0, 1, 2, 3, 5, 6, 7, 7, 7, 7],
            edgeTo: [1, 2, 3, 4, 5, 6, 7, 6],
            roots: [], rootKinds: [],
            nodeAddress: new ulong[9], nodeSize: new long[9]);

        Assert.Empty(graph.DelegateTargets(5));
    }

    [Fact]
    public void Counts_ObjectsReferencesAndRoots()
    {
        var graph = Build();

        Assert.Equal((7, 5, 1), (graph.ObjectCount, graph.ReferenceCount, graph.RootCount));
    }

    [Fact]
    public void PathToRoot_IsTheShortestChainFromARoot()
    {
        var path = Build().PathToRoot("Item");

        Assert.NotNull(path);
        Assert.Equal("StaticVar", path.RootKind);
        Assert.Equal(["Holder", "List", "Array", "Item"], path.Types);
    }

    [Fact]
    public void PathToRoot_PrefersTheFewestHops()
    {
        // Both a direct route (root 0 -> 2 Array... via List) and a longer one reach Array; the root that holds
        // the array directly wins over the chain through Holder and List.
        var path = Build(roots: [0, 2]).PathToRoot("Array");

        Assert.NotNull(path);
        Assert.Equal(["Array"], path.Types);
    }

    [Fact]
    public void PathToRoot_IsNullForATypeNothingRootedReaches()
    {
        Assert.Null(Build().PathToRoot("Orphan"));
        Assert.Null(Build().PathToRoot("NoSuchType"));
    }

    [Fact]
    public void ReferencedBy_ListsHoldersByNumberOfReferences()
    {
        var holders = Build().ReferencedBy("Item");

        // Two Items are referenced by the Array (3 and 4), one more (3) also by the Orphan.
        Assert.Equal([("Array", 2L), ("Orphan", 1L)], holders.Select(h => (h.TypeName, h.References)));
    }

    [Fact]
    public void References_ListsWhatATypeHolds()
    {
        var held = Build().References("Array");

        Assert.Equal([("Item", 2L)], held.Select(h => (h.TypeName, h.References)));
        Assert.Empty(Build().References("Item"));
        Assert.Empty(Build().References("NoSuchType"));
    }
}
