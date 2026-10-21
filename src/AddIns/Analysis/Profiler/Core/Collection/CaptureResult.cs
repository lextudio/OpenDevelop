namespace OpenDevelop.Profiler.Collection;

/// <summary>The outcome of a finished capture. The raw file is already complete when this is returned.</summary>
/// <param name="Path">The <c>.nettrace</c> file that was written.</param>
/// <param name="TargetExited">The target ended while the capture was running, which closed the stream.</param>
/// <param name="Elapsed">How long the capture ran.</param>
public sealed record CaptureResult(string Path, bool TargetExited, TimeSpan Elapsed);
