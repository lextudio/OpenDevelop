using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

// Prints "READY" once the window has loaded; from then on the UI thread is busy about 3/4 of the time.
internal static class Program
{
    // WPF needs a single-threaded apartment; top-level statements would start in the default one and fail.
    [STAThread]
    private static int Main()
    {
        var app = new Application();
        var window = new Window
        {
            Title = "ProfilerWpfFixture",
            Width = 120,
            Height = 80,
            ShowActivated = false,
            ShowInTaskbar = false,
            Content = new TextBlock { Text = "busy" },
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => UiWork.Spin();
        window.Loaded += (_, _) =>
        {
            timer.Start();
            Console.WriteLine("READY");
            Console.Out.Flush();
        };
        return app.Run(window);
    }
}

internal static class UiWork
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Spin()
    {
        long value = Environment.TickCount;
        for (var i = 0; i < 4_000_000; i++)
            value = value * 31 + i;
        return value;
    }
}
