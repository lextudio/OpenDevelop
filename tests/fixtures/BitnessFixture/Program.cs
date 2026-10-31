using System;

namespace BitnessFixture
{
    internal static class Program
    {
        static void Main()
        {
            // The debugger journey requires this line to run while the session is already live:
            // without the pause the four-line program finishes before the adapter binds the
            // breakpoint, and the run then reports "no stop" instead of a failure to break.
            System.Threading.Thread.Sleep(4000);
            var message = ComputeGreeting("World");
            Console.WriteLine(message);
        }

        static string ComputeGreeting(string name)
        {
            var result = $"Hello, {name}!";
            return result;
        }
    }
}
