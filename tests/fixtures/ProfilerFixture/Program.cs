using System.Runtime.CompilerServices;

// Usage: ProfilerFixture startup [seconds] - spin 0.6 s straight away, print READY, then sleep
//        ProfilerFixture cpu [seconds]   - spin in HotLeaf (called from Outer) for that long
//        ProfilerFixture heap [seconds]  - keep HeldObjectCount live HeldObject instances for that long
var mode = args.Length > 0 ? args[0] : "cpu";
var seconds = args.Length > 1 ? double.Parse(args[1]) : 30;

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
else if (mode == "heap")
{
    var held = new List<HeldObject>();
    for (var i = 0; i < Fixture.HeldObjectCount; i++)
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

static class Fixture
{
    public const int HeldObjectCount = 12345;
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
