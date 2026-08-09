using System;

namespace RuntimeUpgradeApp
{
    internal static class Program
    {
        static void Main()
        {
            var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            var message = ComputeGreeting("Runtime");
            Console.WriteLine(message);
            Console.WriteLine(runtime);
        }

        static string ComputeGreeting(string name)
        {
            var result = $"Hello, {name}!";
            return result;
        }
    }
}
