using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;
using OpenDevelop.Profiler.Heap;

namespace OpenDevelop.Profiler.Dump;

/// <summary>One managed thread in a dump: who it is, what state it is in, the exception it was handling, and its stack.</summary>
/// <param name="Frames">Top of the stack first.</param>
public sealed record DumpThread(int ManagedId, uint OsId, string State, string? Exception, IReadOnlyList<string> Frames);

/// <summary>What a dump is: the runtime in it, the machine it came from, and its size.</summary>
public sealed record DumpInfo(string RuntimeVersion, string Architecture, string Platform, long FileBytes);

/// <summary>Everything read from one memory dump: the heap as a snapshot, what its contents hold, and the threads.</summary>
public sealed class DumpReport(DumpInfo info, HeapSnapshot heap, ContentInsights contents, IReadOnlyList<DumpThread> threads)
{
    public DumpInfo Info { get; } = info;

    /// <summary>The managed heap in the same form as a captured snapshot, so every heap view applies.</summary>
    public HeapSnapshot Heap { get; } = heap;

    public ContentInsights Contents { get; } = contents;
    public IReadOnlyList<DumpThread> Threads { get; } = threads;
}

/// <summary>
/// Reads a memory dump (a Windows minidump or full dump, a Linux or macOS core file) with ClrMD, offline: nothing is attached to
/// and no process is paused. The heap is walked once, producing the type table, the object graph and the content findings
/// together. Reading needs the runtime's own data access library (mscordaccore); ClrMD finds it beside the runtime in the dump or
/// on this machine, and otherwise fetches it from the Microsoft symbol server.
/// </summary>
public static class DumpReader
{
    // The graph is held in memory as arrays; past this many objects the read stops with a message instead of exhausting memory.
    const long MaxObjects = 20_000_000;
    const int MaxFramesPerThread = 200;

    /// <summary>Recognises a dump by its header: a Windows minidump, a Linux (ELF) core file or a macOS (Mach-O) core file.</summary>
    public static bool IsDumpFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[20];
            if (stream.Read(header, 0, header.Length) < header.Length)
                return false;
            if (header[0] == 'M' && header[1] == 'D' && header[2] == 'M' && header[3] == 'P')
                return true; // MDMP
            if (header[0] == 0x7F && header[1] == 'E' && header[2] == 'L' && header[3] == 'F')
                return BitConverter.ToUInt16(header, 16) == 4; // ET_CORE, little endian
            if (header[0] == 0xCF && header[1] == 0xFA && header[2] == 0xED && header[3] == 0xFE)
                return BitConverter.ToUInt32(header, 12) == 4; // MH_CORE, 64-bit little endian
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static DumpReport Read(string path, CancellationToken cancellationToken = default)
    {
        // Checked here rather than left to ClrMD, which leaves a file it cannot open as a dump held open.
        if (!IsDumpFile(path))
            throw new ProfilingException($"'{Path.GetFileName(path)}' is not a memory dump (a Windows dump, or a Linux or macOS core file).");

        try
        {
            using var target = DataTarget.LoadDump(path);
            var clr = target.ClrVersions.FirstOrDefault()
                ?? throw new ProfilingException("The dump has no .NET runtime, so there is no managed heap to read.");
            using var runtime = clr.CreateRuntime();

            var info = new DumpInfo(
                clr.Version.ToString(),
                target.DataReader.Architecture.ToString(),
                PlatformName(target.DataReader.TargetPlatform),
                new FileInfo(path).Length);
            var (heap, contents) = ReadHeap(runtime.Heap, cancellationToken);
            return new DumpReport(info, heap, contents, ReadThreads(runtime));
        }
        catch (Exception exception) when (exception is not (ProfilingException or OperationCanceledException))
        {
            throw new ProfilingException(
                $"Could not read '{Path.GetFileName(path)}': {exception.Message} "
                + "A minidump, or a dump from another machine, needs the matching runtime data access library (mscordaccore), "
                + "which is fetched from the Microsoft symbol server.",
                exception);
        }
    }

    static string PlatformName(OSPlatform platform) =>
        platform == OSPlatform.Windows ? "Windows"
        : platform == OSPlatform.Linux ? "Linux"
        : platform == OSPlatform.OSX ? "macOS"
        : platform.ToString();

    static (HeapSnapshot Heap, ContentInsights Contents) ReadHeap(ClrHeap heap, CancellationToken cancellationToken)
    {
        var scanner = new ContentScanner();
        var typeIds = new Dictionary<ulong, int>();
        var typeNames = new List<string>();
        var typeCount = new List<long>();
        var typeSize = new List<long>();
        var nodeType = new List<int>();
        var nodeAddress = new List<ulong>();
        var nodeSize = new List<long>();
        var nodeEdgeCount = new List<long>();
        var edgeTarget = new List<ulong>();

        foreach (var obj in heap.EnumerateObjects())
        {
            if ((nodeType.Count & 0xFFFF) == 0xFFFF)
                cancellationToken.ThrowIfCancellationRequested();
            if (nodeType.Count >= MaxObjects)
                throw new ProfilingException(
                    $"The heap has more than {MaxObjects:N0} objects; reading it whole would need more memory than is reasonable.");
            var type = obj.Type;
            if (type is null)
                continue;

            if (!typeIds.TryGetValue(type.MethodTable, out var id))
            {
                id = typeNames.Count;
                typeIds[type.MethodTable] = id;
                typeNames.Add(type.Name ?? "<unknown type>");
                typeCount.Add(0);
                typeSize.Add(0);
            }
            var size = (long)obj.Size;
            typeCount[id]++;
            typeSize[id] += size;

            nodeType.Add(id);
            nodeAddress.Add(obj.Address);
            nodeSize.Add(size);
            long edges = 0;
            foreach (var reference in obj.EnumerateReferenceAddresses())
            {
                if (reference == 0)
                    continue;
                edgeTarget.Add(reference);
                edges++;
            }
            nodeEdgeCount.Add(edges);
            scanner.Add(obj, heap);
        }

        var rootAddress = new List<ulong>();
        var rootKind = new List<string>();
        foreach (var root in heap.EnumerateRoots())
        {
            if (root.Object.Address == 0)
                continue;
            rootAddress.Add(root.Object.Address);
            rootKind.Add(root.RootKind.ToString());
        }

        var types = Enumerable.Range(0, typeNames.Count)
            .Select(i => new HeapTypeStat(typeNames[i], typeCount[i], typeSize[i]))
            .ToList();
        var graph = NettraceHeapReader.BuildGraph(
            typeNames.ToArray(), nodeType.ToArray(), nodeAddress, nodeSize, nodeEdgeCount, edgeTarget, rootAddress, rootKind);
        return (new HeapSnapshot(types, graph), scanner.Build());
    }

    static IReadOnlyList<DumpThread> ReadThreads(ClrRuntime runtime)
    {
        var threads = new List<DumpThread>();
        foreach (var thread in runtime.Threads)
        {
            var frames = new List<string>();
            foreach (var frame in thread.EnumerateStackTrace(includeContext: false))
            {
                frames.Add(frame.ToString() ?? "?");
                if (frames.Count >= MaxFramesPerThread)
                    break;
            }
            var exception = thread.CurrentException;
            threads.Add(new DumpThread(
                thread.ManagedThreadId,
                thread.OSThreadId,
                thread.IsFinalizer ? "Finalizer" : thread.IsGc ? "GC" : thread.IsAlive ? "Alive" : "Dead",
                exception is null ? null : $"{exception.Type?.Name ?? "Exception"}: {exception.Message}",
                frames));
        }
        return threads.OrderBy(t => t.ManagedId).ToList();
    }
}
