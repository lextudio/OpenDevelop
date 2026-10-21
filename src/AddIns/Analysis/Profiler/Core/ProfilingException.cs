namespace OpenDevelop.Profiler;

/// <summary>A capture could not start or finish. The message is written for the user.</summary>
public sealed class ProfilingException : Exception
{
    public ProfilingException(string message) : base(message)
    {
    }

    public ProfilingException(string message, Exception inner) : base(message, inner)
    {
    }
}
