using System.Runtime.CompilerServices;

// Prints "READY <url>" once Kestrel is listening on a free local port.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
var app = builder.Build();
app.MapGet("/work", () => WebWork.Handle(Environment.TickCount).ToString());
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("READY " + string.Join(",", app.Urls));
    Console.Out.Flush();
});
app.Run();

static class WebWork
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Handle(int seed)
    {
        long result = 0;
        for (var i = 0; i < 30; i++)
            result += Spin(seed + i);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Spin(int seed)
    {
        long value = seed;
        for (var i = 0; i < 300_000; i++)
            value = value * 31 + i;
        return value;
    }
}
