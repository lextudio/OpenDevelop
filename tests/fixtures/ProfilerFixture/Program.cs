using System.Runtime.CompilerServices;

// Usage: ProfilerFixture startup [seconds] - spin 0.6 s straight away, print READY, then sleep
//        ProfilerFixture cpu [seconds]   - spin in HotLeaf (called from Outer) for that long
//        ProfilerFixture alloc [seconds] - allocate byte arrays in Allocator.Buffers (most of the bytes) and strings in
//                                          Allocator.Labels, and drop them straight away
//        ProfilerFixture contents [seconds] - hold 2000 equal strings (separate instances) and an int[200000] that is all zeros but three
//        ProfilerFixture heap [seconds]  - keep HeldObjectCount live HeldObject instances for that long
//        ProfilerFixture heap [seconds] [count] - the same with count instances, for a snapshot that takes a while to write
var mode = args.Length > 0 ? args[0] : "cpu";
var seconds = args.Length > 1 ? double.Parse(args[1]) : 30;
var heldCount = args.Length > 2 ? int.Parse(args[2]) : Fixture.HeldObjectCount;

if (mode == "startup")
{
    // Real work before anything could attach to a running process: only a capture that was recording from
    // the very start sees it.
    var begun = System.Diagnostics.Stopwatch.StartNew();
    long total = 0;
    while (begun.Elapsed < TimeSpan.FromMilliseconds(600))
        total += Fixture.Outer(total);
    Console.WriteLine("READY");
    Console.Out.Flush();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    GC.KeepAlive(total.ToString());
}
else if (mode == "alloc")
{
    Console.WriteLine("READY");
    Console.Out.Flush();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        Allocator.Buffers(4096);
        Allocator.Labels(8);
    }
}
else if (mode == "leak")
{
    // A leak that grows at a steady rate, so two snapshots taken apart differ by a known direction.
    Console.WriteLine("READY");
    Console.Out.Flush();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        for (var i = 0; i < LeakedItem.PerTick; i++)
            Leak.Items.Add(new LeakedItem { Payload = new byte[64] });
        Thread.Sleep(50);
    }
}
else if (mode == "threads")
{
    var workers = Enumerable.Range(0, Fixture.ThreadCount).Select(index => new Thread(() =>
    {
        long accumulator = index;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
            accumulator += Fixture.Outer(accumulator);
        GC.KeepAlive(accumulator.ToString());
    }) { Name = "spin-" + index }).ToList();
    workers.ForEach(t => t.Start());
    Console.WriteLine("READY");
    Console.Out.Flush();
    workers.ForEach(t => t.Join());
}
else if (mode == "starve")
{
    // Blocking inside pool work items is the classic starvation: the pool answers by injecting threads slowly.
    Console.WriteLine("READY");
    Console.Out.Flush();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        while (Environment.ProcessorCount * 6 > Fixture.Blocked)
        {
            Interlocked.Increment(ref Fixture.Blocked);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(30_000); // long enough that nothing completes, which is what the pool reads as starvation
                Interlocked.Decrement(ref Fixture.Blocked);
            });
        }
        Thread.Sleep(100);
    }
}
else if (mode == "contents")
{
    // Equal values, distinct instances: new string(...) does not intern.
    var duplicates = new List<string>();
    for (var i = 0; i < 2000; i++)
        duplicates.Add(new string("duplicate-string-held-by-the-fixture-0123456789".AsSpan()));
    var sparse = new int[200_000];
    sparse[0] = 1;
    sparse[100_000] = 2;
    sparse[199_999] = 3;
    Console.WriteLine("READY");
    Console.Out.Flush();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    GC.KeepAlive(duplicates);
    GC.KeepAlive(sparse);
}
else if (mode == "heap")
{
    var held = new List<HeldObject>();
    for (var i = 0; i < heldCount; i++)
        held.Add(new HeldObject { Name = "item-" + i, Payload = new byte[100] });
    // Subscriptions that are never removed: the static event keeps every listener alive.
    for (var i = 0; i < Fixture.ListenerCount; i++)
        Publisher.Changed += new Listener().OnChanged;
    Console.WriteLine("READY");
    Console.Out.Flush();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    GC.KeepAlive(held);
}
else
{
    Console.WriteLine("READY");
    Console.Out.Flush();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    long accumulator = 0;
    while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
        accumulator += Fixture.Outer(accumulator);
    Console.WriteLine(accumulator);
}

static class Allocator
{
    public static object? Sink;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Buffers(int count)
    {
        for (var i = 0; i < count; i++)
            Sink = new byte[1024];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Labels(int count)
    {
        for (var i = 0; i < count; i++)
            Sink = new string('x', 40 + i);
    }
}

static class Fixture
{
    public const int HeldObjectCount = 12345;
    public const int ThreadCount = 4;
    public static int Blocked;
    public const int ListenerCount = 200;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Outer(long seed)
    {
        long result = 0;
        for (var i = 0; i < 20; i++)
            result += HotLeaf(seed + i);
        return result + Light(seed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long HotLeaf(long seed)
    {
        var value = seed;
        for (var i = 0; i < 200_000; i++)
            value = value * 31 + i;
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Light(long seed)
    {
        var value = seed;
        for (var i = 0; i < 2_000; i++)
            value ^= i;
        return value;
    }
}

sealed class Listener
{
    public void OnChanged(object? sender, EventArgs e)
    {
    }
}

static class Publisher
{
    public static event EventHandler? Changed;
}

sealed class HeldObject
{
    public string Name = "";
    public byte[] Payload = [];
}

sealed class LeakedItem
{
    public const int PerTick = 200;
    public byte[]? Payload;
}

static class Leak
{
    public static readonly List<LeakedItem> Items = [];
}
