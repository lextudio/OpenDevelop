using System;

namespace BitnessFixture
{
    internal static class Program
    {
        static void Main()
        {
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
