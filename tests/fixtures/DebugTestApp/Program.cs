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
        }

        static string ComputeGreeting(string name)
        {
            var result = $"Hello, {name}!";
            return result;
        }
    }
}
