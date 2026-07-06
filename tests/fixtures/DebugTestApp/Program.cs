using System;

namespace DebugTestApp
{
    internal static class Program
    {
        static void Main()
        {
            var greeting = "Hello, Debugger!";
            var answer = 42;
            var message = ComputeGreeting("World");
            Console.WriteLine(message);
            Console.WriteLine(greeting);
            Console.WriteLine(answer);
        }

        static string ComputeGreeting(string name)
        {
            var result = $"Hello, {name}!";
            return result;
        }
    }
}
