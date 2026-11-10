using System;
using System.Linq;

namespace DebugTestApp
{
    internal static class Program
    {
        static void Main()
        {
            var greeting = "Hello, Debugger!";
            var xml = "<root><item>visualizer</item></root>";
            var numbers = new[] { 7, 11 };
            var answer = 42;
            var doubled = numbers.Select(n =>
            {
                var twice = n * 2;
                return twice;
            }).ToArray();
            var message = ComputeGreeting("World");
            Console.WriteLine(message);
            Console.WriteLine(greeting);
            Console.WriteLine(xml);
            Console.WriteLine(numbers.Length);
            Console.WriteLine(answer);
            Console.WriteLine(doubled.Length);
            // Run Without Debugging must not attach a debugger; the integration test reads this line.
            Console.WriteLine("Debugger.IsAttached=" + System.Diagnostics.Debugger.IsAttached);
        }

        static string ComputeGreeting(string name)
        {
            var result = $"Hello, {name}!";
            return result;
        }
    }
}
