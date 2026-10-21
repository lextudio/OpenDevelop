using Microsoft.Diagnostics.Tracing;

namespace OpenDevelop.Profiler.Heap;

/// <summary>
/// Rebuilds a heap snapshot from the runtime's GC heap-dump events (<c>GCBulkNode</c>, <c>GCBulkEdge</c>,
/// <c>GCBulkRootEdge</c> and <c>TypeBulkType</c>) in an EventPipe file, the same data <c>dotnet-gcdump</c>
/// turns into a <c>.gcdump</c>. Going through the events keeps the capture a plain, portable <c>.nettrace</c>.
/// </summary>
public static class NettraceHeapReader
{
    public static HeapSnapshot Read(string nettracePath)
    {
        using var source = new EventPipeEventSource(nettracePath);
        var typeNames = new Dictionary<ulong, string>();
        var nodeAddress = new List<ulong>();
        var nodeTypeId = new List<ulong>();
        var nodeSize = new List<long>();
        var nodeEdgeCount = new List<long>();
        var edgeTarget = new List<ulong>();
        var rootAddress = new List<ulong>();
        var rootKind = new List<string>();

        source.Clr.TypeBulkType += data =>
        {
            for (var i = 0; i < data.Count; i++)
            {
                var type = data.Values(i);
                typeNames[type.TypeID] = type.TypeName;
            }
        };
        source.Clr.GCBulkNode += data =>
        {
            for (var i = 0; i < data.Count; i++)
            {
                var node = data.Values(i);
                nodeAddress.Add(node.Address);
                nodeTypeId.Add(node.TypeID);
                nodeSize.Add((long)node.Size);
                nodeEdgeCount.Add(node.EdgeCount);
            }
        };
        source.Clr.GCBulkEdge += data =>
        {
            for (var i = 0; i < data.Count; i++)
                edgeTarget.Add(data.Values(i).Target);
        };
        source.Clr.GCBulkRootEdge += data =>
        {
            for (var i = 0; i < data.Count; i++)
            {
                var root = data.Values(i);
                rootAddress.Add(root.RootedNodeAddress);
                rootKind.Add(root.GCRootKind.ToString());
            }
        };
        source.Process();

        // Type ids are per load context; several can carry one name, so everything is keyed by name.
        var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        int IndexOfType(ulong typeId)
        {
            var name = typeNames.TryGetValue(typeId, out var known) && known.Length > 0 ? known : $"<type 0x{typeId:X}>";
            if (!nameIndex.TryGetValue(name, out var index))
            {
                index = names.Count;
                names.Add(name);
                nameIndex[name] = index;
            }
            return index;
        }

        var nodeType = new int[nodeAddress.Count];
        var perType = new Dictionary<int, (long Count, long Size)>();
        for (var i = 0; i < nodeType.Length; i++)
        {
            nodeType[i] = IndexOfType(nodeTypeId[i]);
            var entry = perType.GetValueOrDefault(nodeType[i]);
            perType[nodeType[i]] = (entry.Count + 1, entry.Size + nodeSize[i]);
        }

        var types = perType.Select(p => new HeapTypeStat(names[p.Key], p.Value.Count, p.Value.Size)).ToList();
        var graph = BuildGraph(names.ToArray(), nodeType, nodeAddress, nodeSize, nodeEdgeCount, edgeTarget, rootAddress, rootKind);
        return new HeapSnapshot(types, graph);
    }

    // Edges arrive as one flat list in node order, each node owning the next EdgeCount of them. If the counts
    // do not add up (a truncated capture), the type table is still right but there is no graph to offer.
    static HeapGraph? BuildGraph(
        string[] typeNames, int[] nodeType, List<ulong> nodeAddress, List<long> nodeSize, List<long> nodeEdgeCount,
        List<ulong> edgeTarget, List<ulong> rootAddress, List<string> rootKind)
    {
        if (nodeType.Length == 0 || nodeEdgeCount.Sum() != edgeTarget.Count)
            return null;

        var byAddress = new Dictionary<ulong, int>(nodeAddress.Count);
        for (var i = 0; i < nodeAddress.Count; i++)
            byAddress[nodeAddress[i]] = i;

        var edgeStart = new int[nodeType.Length + 1];
        for (var i = 0; i < nodeType.Length; i++)
            edgeStart[i + 1] = edgeStart[i] + (int)nodeEdgeCount[i];

        var edgeTo = new int[edgeTarget.Count];
        for (var e = 0; e < edgeTo.Length; e++)
            edgeTo[e] = byAddress.GetValueOrDefault(edgeTarget[e], -1);

        var roots = new List<int>();
        var kinds = new List<string>();
        for (var i = 0; i < rootAddress.Count; i++)
        {
            if (byAddress.TryGetValue(rootAddress[i], out var node))
            {
                roots.Add(node);
                kinds.Add(rootKind[i]);
            }
        }
        return new HeapGraph(
            typeNames, nodeType, edgeStart, edgeTo, roots.ToArray(), kinds.ToArray(), nodeAddress.ToArray(), nodeSize.ToArray());
    }
}
