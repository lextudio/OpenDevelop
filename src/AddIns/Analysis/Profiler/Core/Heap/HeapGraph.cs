namespace OpenDevelop.Profiler.Heap;

/// <summary>One edge of the type-level reference summary: a neighbouring type and how many references link it.</summary>
public sealed record TypeLink(string TypeName, long References);

/// <summary>The shortest chain of objects from a GC root to an object, as type names root first.</summary>
public sealed record HeapPath(string RootKind, IReadOnlyList<string> Types);

/// <summary>One object: where it lives, its own size, and everything that would be freed with it.</summary>
public sealed record HeapInstance(int Node, ulong Address, long Size, long RetainedSize);

/// <summary>An object with the type it is, for lists that mix types.</summary>
public sealed record HeapObject(string TypeName, HeapInstance Instance);

/// <summary>A type whose objects delegates (event handlers, callbacks) hold on to, and how many delegates do.</summary>
public sealed record DelegateTarget(string TypeName, long Delegates, long RetainedSize);

/// <summary>What a type keeps alive: the memory freed if every object of the type, and all they alone hold, went away.</summary>
public sealed record TypeRetained(string TypeName, long RetainedSize);

/// <summary>
/// The object graph of a heap snapshot in compact arrays: every object's type and size, the references
/// between objects, and the GC roots. It answers the questions the Types view cannot: who holds a type
/// alive, what it holds, the shortest path from a root to it, and how much memory it keeps alive.
/// </summary>
public sealed class HeapGraph
{
    readonly string[] typeNames;
    readonly int[] nodeType;
    readonly ulong[] nodeAddress;
    readonly long[] nodeSize;
    readonly int[] edgeStart; // length nodeCount + 1; node i's edges are edgeTo[edgeStart[i] .. edgeStart[i + 1])
    readonly int[] edgeTo;
    readonly int[] roots;
    readonly string[] rootKinds;
    readonly Dictionary<string, int> typeIndex;
    readonly Lazy<Dominators> dominators;

    internal HeapGraph(
        string[] typeNames, int[] nodeType, int[] edgeStart, int[] edgeTo, int[] roots, string[] rootKinds,
        ulong[]? nodeAddress = null, long[]? nodeSize = null)
    {
        this.typeNames = typeNames;
        this.nodeType = nodeType;
        this.edgeStart = edgeStart;
        this.edgeTo = edgeTo;
        this.roots = roots;
        this.rootKinds = rootKinds;
        this.nodeAddress = nodeAddress ?? new ulong[nodeType.Length];
        this.nodeSize = nodeSize ?? new long[nodeType.Length];
        typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < typeNames.Length; i++)
            typeIndex[typeNames[i]] = i;
        dominators = new Lazy<Dominators>(() => Dominators.Compute(this.nodeSize, edgeStart, edgeTo, roots));
    }

    public int ObjectCount => nodeType.Length;
    public int ReferenceCount => edgeTo.Length;
    public int RootCount => roots.Length;

    /// <summary>The types whose objects reference objects of <paramref name="typeName"/>, most references first.</summary>
    public IReadOnlyList<TypeLink> ReferencedBy(string typeName)
    {
        if (!typeIndex.TryGetValue(typeName, out var target))
            return [];
        var counts = new Dictionary<int, long>();
        for (var node = 0; node < nodeType.Length; node++)
        {
            for (var e = edgeStart[node]; e < edgeStart[node + 1]; e++)
            {
                var to = edgeTo[e];
                if (to >= 0 && nodeType[to] == target)
                    counts[nodeType[node]] = counts.GetValueOrDefault(nodeType[node]) + 1;
            }
        }
        return Links(counts);
    }

    /// <summary>The types that objects of <paramref name="typeName"/> reference, most references first.</summary>
    public IReadOnlyList<TypeLink> References(string typeName)
    {
        if (!typeIndex.TryGetValue(typeName, out var source))
            return [];
        var counts = new Dictionary<int, long>();
        for (var node = 0; node < nodeType.Length; node++)
        {
            if (nodeType[node] != source)
                continue;
            for (var e = edgeStart[node]; e < edgeStart[node + 1]; e++)
            {
                var to = edgeTo[e];
                if (to >= 0)
                    counts[nodeType[to]] = counts.GetValueOrDefault(nodeType[to]) + 1;
            }
        }
        return Links(counts);
    }

    /// <summary>
    /// The shortest reference chain from any GC root to an object of the type, found breadth first so it is
    /// the fewest hops, or <c>null</c> when no instance is reachable from a root (it is only waiting to be collected).
    /// </summary>
    public HeapPath? PathToRoot(string typeName) =>
        typeIndex.TryGetValue(typeName, out var target) ? ShortestPath(node => nodeType[node] == target) : null;

    /// <summary>The shortest chain from a GC root to one particular object, or <c>null</c> if no root reaches it.</summary>
    public HeapPath? PathToRoot(int node) => ShortestPath(candidate => candidate == node);

    /// <summary>
    /// The objects of a type with the largest retained size, largest first. Unreachable objects (garbage not yet
    /// collected) are left out because nothing keeps them alive and their retained size is not meaningful.
    /// </summary>
    public IReadOnlyList<HeapInstance> Instances(string typeName, int maxCount)
    {
        if (!typeIndex.TryGetValue(typeName, out var type))
            return [];
        var info = dominators.Value;
        var result = new List<HeapInstance>();
        for (var node = 0; node < nodeType.Length; node++)
        {
            if (nodeType[node] == type && info.IsReachable(node))
                result.Add(new HeapInstance(node, nodeAddress[node], nodeSize[node], info.Retained[node]));
        }
        return result.OrderByDescending(i => i.RetainedSize).ThenBy(i => i.Address).Take(maxCount).ToList();
    }

    /// <summary>
    /// Retained size per type, largest first. For a type, an object counts only if no other object of the same
    /// type holds it, so a chain of objects of one type is not counted once per link.
    /// </summary>
    public IReadOnlyList<TypeRetained> RetainedByType()
    {
        var info = dominators.Value;
        var perType = new long[typeNames.Length];
        var depth = new int[typeNames.Length]; // objects of the type on the dominator path being walked
        var stack = new Stack<(int Node, bool Entered)>();
        foreach (var top in info.Children(info.Root))
            stack.Push((top, false));

        while (stack.Count > 0)
        {
            var (node, entered) = stack.Pop();
            var type = nodeType[node];
            if (entered)
            {
                depth[type]--;
                continue;
            }
            if (depth[type]++ == 0)
                perType[type] += info.Retained[node];
            stack.Push((node, true));
            foreach (var child in info.Children(node))
                stack.Push((child, false));
        }

        return typeNames
            .Select((name, i) => new TypeRetained(name, perType[i]))
            .Where(t => t.RetainedSize > 0)
            .OrderByDescending(t => t.RetainedSize)
            .ThenBy(t => t.TypeName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The objects that keep the most memory alive, whatever their type, largest first. Unreachable objects are left out.</summary>
    public IReadOnlyList<HeapObject> LargestRetainers(int count)
    {
        var info = dominators.Value;
        var all = new List<HeapObject>();
        for (var node = 0; node < nodeType.Length; node++)
        {
            if (info.IsReachable(node))
                all.Add(new HeapObject(typeNames[nodeType[node]], new HeapInstance(node, nodeAddress[node], nodeSize[node], info.Retained[node])));
        }
        return all.OrderByDescending(o => o.Instance.RetainedSize).ThenBy(o => o.Instance.Address).Take(count).ToList();
    }

    /// <summary>
    /// The types that reachable delegates point at, most delegates first. A delegate (an event handler or callback) holds
    /// its target object alive, so a type that many delegates target, and that retains a lot, is where to look for
    /// subscriptions that were never removed. Delegates are recognised by type name, so this is a heuristic.
    /// </summary>
    public IReadOnlyList<DelegateTarget> DelegateTargets(int count)
    {
        var info = dominators.Value;
        var isDelegate = typeNames.Select(IsDelegateType).ToArray();
        var delegates = new Dictionary<int, long>();
        var targets = new Dictionary<int, HashSet<int>>();
        for (var node = 0; node < nodeType.Length; node++)
        {
            if (!isDelegate[nodeType[node]] || !info.IsReachable(node))
                continue;
            for (var e = edgeStart[node]; e < edgeStart[node + 1]; e++)
            {
                var to = edgeTo[e];
                // Delegates and the arrays that hold an invocation list are plumbing; what matters is what they point at.
                if (to < 0 || isDelegate[nodeType[to]] || typeNames[nodeType[to]].EndsWith("[]", StringComparison.Ordinal))
                    continue;
                delegates[nodeType[to]] = delegates.GetValueOrDefault(nodeType[to]) + 1;
                if (!targets.TryGetValue(nodeType[to], out var set))
                    targets[nodeType[to]] = set = [];
                set.Add(to);
            }
        }

        return delegates
            .Select(pair => new DelegateTarget(
                typeNames[pair.Key], pair.Value, targets[pair.Key].Sum(target => info.Retained[target])))
            .OrderByDescending(t => t.Delegates)
            .ThenByDescending(t => t.RetainedSize)
            .ThenBy(t => t.TypeName, StringComparer.Ordinal)
            .Take(count)
            .ToList();
    }

    internal static bool IsDelegateType(string typeName)
    {
        // Generic arguments are not part of what makes a type a delegate: look at the name before them.
        var bracket = typeName.IndexOf('[');
        var name = bracket < 0 ? typeName : typeName[..bracket];
        var dot = name.LastIndexOf('.');
        var simple = dot < 0 ? name : name[(dot + 1)..];
        return simple is "Action" or "Delegate" or "MulticastDelegate" or "Predicate`1" or "Comparison`1" or "Converter`2"
            || simple.StartsWith("Action`", StringComparison.Ordinal)
            || simple.StartsWith("Func`", StringComparison.Ordinal)
            || simple is "EventHandler" or "RoutedEventHandler" or "PropertyChangedEventHandler" or "NotifyCollectionChangedEventHandler"
            || simple.StartsWith("EventHandler`", StringComparison.Ordinal);
    }

    HeapPath? ShortestPath(Func<int, bool> isTarget)
    {
        var parent = new int[nodeType.Length];
        Array.Fill(parent, -2); // -2 unvisited, -1 a root
        var rootKindOf = new Dictionary<int, string>();
        var queue = new Queue<int>();
        for (var i = 0; i < roots.Length; i++)
        {
            var node = roots[i];
            if (node < 0 || parent[node] != -2)
                continue;
            parent[node] = -1;
            rootKindOf[node] = rootKinds[i];
            queue.Enqueue(node);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (isTarget(node))
                return BuildPath(node, parent, rootKindOf);
            for (var e = edgeStart[node]; e < edgeStart[node + 1]; e++)
            {
                var to = edgeTo[e];
                if (to < 0 || parent[to] != -2)
                    continue;
                parent[to] = node;
                queue.Enqueue(to);
            }
        }
        return null;
    }

    HeapPath BuildPath(int end, int[] parent, Dictionary<int, string> rootKindOf)
    {
        var chain = new List<string>();
        var node = end;
        while (parent[node] >= 0)
        {
            chain.Add(typeNames[nodeType[node]]);
            node = parent[node];
        }
        chain.Add(typeNames[nodeType[node]]);
        chain.Reverse();
        return new HeapPath(rootKindOf[node], chain);
    }

    IReadOnlyList<TypeLink> Links(Dictionary<int, long> counts) =>
        counts
            .Select(pair => new TypeLink(typeNames[pair.Key], pair.Value))
            .OrderByDescending(link => link.References)
            .ThenBy(link => link.TypeName, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The dominator tree of the object graph (Cooper, Harvey and Kennedy's iterative algorithm) with a virtual
    /// root above every GC root, and each object's retained size: its own size plus that of everything it dominates.
    /// </summary>
    sealed class Dominators
    {
        public int Root { get; private init; }
        public long[] Retained { get; private init; } = [];
        int[] idom = [];
        int[] childStart = [];
        int[] childList = [];

        public bool IsReachable(int node) => idom[node] >= 0;

        public ReadOnlySpan<int> Children(int node) =>
            childList.AsSpan(childStart[node], childStart[node + 1] - childStart[node]);

        public static Dominators Compute(long[] size, int[] edgeStart, int[] edgeTo, int[] roots)
        {
            var n = size.Length;
            var root = n;

            // Depth-first post-order from the virtual root; the virtual root's successors are the GC roots.
            var post = new int[n + 1];
            Array.Fill(post, -1);
            var order = new List<int>(n + 1);
            var visited = new bool[n + 1];
            var stack = new Stack<(int Node, int Next)>();
            visited[root] = true;
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                var count = node == root ? roots.Length : edgeStart[node + 1] - edgeStart[node];
                var advanced = false;
                for (var i = next; i < count; i++)
                {
                    var to = node == root ? roots[i] : edgeTo[edgeStart[node] + i];
                    if (to < 0 || visited[to])
                        continue;
                    visited[to] = true;
                    stack.Push((node, i + 1));
                    stack.Push((to, 0));
                    advanced = true;
                    break;
                }
                if (!advanced)
                {
                    post[node] = order.Count;
                    order.Add(node);
                }
            }

            // Predecessors of the reachable nodes, as arrays.
            var predCount = new int[n + 2];
            void ForEachSuccessor(int node, Action<int> visit)
            {
                if (node == root)
                {
                    foreach (var r in roots)
                        if (r >= 0)
                            visit(r);
                    return;
                }
                for (var e = edgeStart[node]; e < edgeStart[node + 1]; e++)
                    if (edgeTo[e] >= 0)
                        visit(edgeTo[e]);
            }

            foreach (var node in order)
                ForEachSuccessor(node, to => predCount[to + 1]++);
            for (var i = 1; i < predCount.Length; i++)
                predCount[i] += predCount[i - 1];
            var predStart = (int[])predCount.Clone();
            var predList = new int[predCount[^1]];
            var fill = new int[n + 1];
            foreach (var node in order)
                ForEachSuccessor(node, to => predList[predStart[to] + fill[to]++] = node);

            var idom = new int[n + 1];
            Array.Fill(idom, -1);
            idom[root] = root;
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var k = order.Count - 2; k >= 0; k--) // reverse post-order, skipping the virtual root
                {
                    var node = order[k];
                    var newIdom = -1;
                    for (var p = predStart[node]; p < predStart[node + 1]; p++)
                    {
                        var pred = predList[p];
                        if (idom[pred] < 0)
                            continue;
                        newIdom = newIdom < 0 ? pred : Intersect(pred, newIdom, idom, post);
                    }
                    if (newIdom != idom[node])
                    {
                        idom[node] = newIdom;
                        changed = true;
                    }
                }
            }

            // Retained size: children come before their dominator in post-order, so one pass adds each node's
            // total into its dominator's.
            var retained = new long[n + 1];
            for (var i = 0; i < n; i++)
                retained[i] = size[i];
            foreach (var node in order)
            {
                if (node != root)
                    retained[idom[node]] += retained[node];
            }

            // Children of every node in the dominator tree, for walking it top down.
            var childStart = new int[n + 2];
            foreach (var node in order)
                if (node != root)
                    childStart[idom[node] + 1]++;
            for (var i = 1; i < childStart.Length; i++)
                childStart[i] += childStart[i - 1];
            var childList = new int[childStart[^1]];
            var childFill = new int[n + 1];
            foreach (var node in order)
            {
                if (node != root)
                    childList[childStart[idom[node]] + childFill[idom[node]]++] = node;
            }

            return new Dominators { Root = root, Retained = retained, idom = idom, childStart = childStart, childList = childList };
        }

        static int Intersect(int a, int b, int[] idom, int[] post)
        {
            while (a != b)
            {
                while (post[a] < post[b])
                    a = idom[a];
                while (post[b] < post[a])
                    b = idom[b];
            }
            return a;
        }
    }
}
