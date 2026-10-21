namespace OpenDevelop.Profiler.Cpu;

/// <summary>Decides which frames count as the user's own code for "Show Just My Code".</summary>
public static class UserCode
{
    // Module names as the profiler reports them: managed ones without the file extension, native
    // ones as the library's base name. Prefixes cover the runtime, BCL and UI frameworks.
    static readonly string[] FrameworkPrefixes =
    [
        "System", "Microsoft", "mscorlib", "netstandard", "WindowsBase", "PresentationCore",
        "PresentationFramework", "UIAutomation", "ProGPU", "LibreWPF", "coreclr", "clrjit", "libcoreclr",
        "libclrjit", "libSystem", "ntdll", "kernel", "kernelbase", "user32", "gdi32", "combase", "ole32",
        "UNKNOWN", "libc", "libpthread",
    ];

    /// <summary>User code is anything from one of the given assemblies (typically the open solution's projects).</summary>
    public static Func<FrameInfo, bool> FromAssemblies(IEnumerable<string> assemblyNames)
    {
        var names = new HashSet<string>(assemblyNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        return frame => names.Contains(Normalize(frame.Module));
    }

    /// <summary>
    /// Without a solution to ask, treat the runtime, BCL, UI frameworks and operating system modules as
    /// external and everything else as the user's.
    /// </summary>
    public static Func<FrameInfo, bool> NotFramework { get; } = frame =>
    {
        var module = Normalize(frame.Module);
        if (module.Length == 0)
            return false;
        foreach (var prefix in FrameworkPrefixes)
        {
            if (module.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (module.Length == prefix.Length || !char.IsLetter(module[prefix.Length])))
                return false;
        }
        return true;
    };

    /// <summary>
    /// The predicate to use for one profile: the solution's assemblies when the profile actually contains
    /// any of them, otherwise the framework heuristic. Profiling a process that is not part of the open
    /// solution would otherwise fold every frame into <c>[External Code]</c>.
    /// </summary>
    public static Func<FrameInfo, bool> ForProfile(CpuProfile profile, IEnumerable<string> solutionAssemblies)
    {
        var fromSolution = FromAssemblies(solutionAssemblies);
        return profile.Frames.Any(fromSolution) ? fromSolution : NotFramework;
    }

    static string Normalize(string module)
    {
        if (module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return module[..^4];
        return module;
    }
}
