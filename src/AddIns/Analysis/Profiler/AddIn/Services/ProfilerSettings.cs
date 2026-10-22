using ICSharpCode.Core;

namespace OpenDevelop.Profiler.AddIn.Services;

/// <summary>The few settings the Profiler has, kept in the IDE's property store.</summary>
internal static class ProfilerSettings
{
	public const int DefaultRetainedSessions = 30;
	public const int DefaultLargeCaptureMegabytes = 200;

	/// <summary>How many captures are kept; starting a capture removes the oldest finished ones beyond this.</summary>
	public static int RetainedSessions
	{
		get => Math.Max(1, PropertyService.Get("Profiler.RetainedSessions", DefaultRetainedSessions));
		set => PropertyService.Set("Profiler.RetainedSessions", Math.Max(1, value));
	}

	/// <summary>A capture of at least this many megabytes is confirmed before it is opened.</summary>
	public static int LargeCaptureMegabytes
	{
		get => Math.Max(1, PropertyService.Get("Profiler.LargeCaptureMegabytes", DefaultLargeCaptureMegabytes));
		set => PropertyService.Set("Profiler.LargeCaptureMegabytes", Math.Max(1, value));
	}

	public static long LargeCaptureBytes => LargeCaptureMegabytes * 1024L * 1024L;

	/// <summary>
	/// After a heap snapshot, also read the process's memory for duplicate strings and sparse arrays. It pauses the process again and
	/// writes a memory dump (as large as the process) for the length of the scan, so it is off unless asked for.
	/// </summary>
	public static bool ScanHeapContents
	{
		get => PropertyService.Get("Profiler.ScanHeapContents", false);
		set => PropertyService.Set("Profiler.ScanHeapContents", value);
	}

	/// <summary>Where captures are stored; empty means <c>.od/profiling</c> in the solution folder.</summary>
	public static string StoreFolder
	{
		get => PropertyService.Get("Profiler.StoreFolder", string.Empty);
		set => PropertyService.Set("Profiler.StoreFolder", value.Trim());
	}
}
