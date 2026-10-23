using System.Runtime.InteropServices;
using System.Text;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Selects the macOS keyboard input source (layout or input method) through the Text Input Sources
/// API. Real key events are composed by the active input method before an application sees them, so a
/// test that types must run on a plain layout: with a Pinyin source active, <c>echo </c> arrives as two
/// Chinese characters. The previous source is restored on dispose.
/// </summary>
sealed class MacInputSource : IDisposable
{
	public const string Abc = "com.apple.keylayout.ABC";
	public const string Us = "com.apple.keylayout.US";

	const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

	[DllImport(CoreFoundation)] static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);
	[DllImport(CoreFoundation)] static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint size, uint encoding);
	[DllImport(CoreFoundation)] static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
	[DllImport(CoreFoundation)] static extern nint CFArrayGetCount(IntPtr array);
	[DllImport(CoreFoundation)] static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
	[DllImport(CoreFoundation)] static extern void CFRelease(IntPtr value);
	[DllImport(Carbon)] static extern IntPtr TISCopyCurrentKeyboardInputSource();
	[DllImport(Carbon)] static extern IntPtr TISCreateInputSourceList(IntPtr properties, bool includeAllInstalled);
	[DllImport(Carbon)] static extern IntPtr TISGetInputSourceProperty(IntPtr source, IntPtr key);
	[DllImport(Carbon)] static extern int TISSelectInputSource(IntPtr source);

	const uint Utf8 = 0x08000100;

	readonly string? previous;

	MacInputSource(string? previous) => this.previous = previous;

	/// <summary>
	/// Selects <paramref name="id"/> and waits until it is the current source. Null when not on macOS, when the source is not enabled
	/// on this machine, or when the system did not switch to it (the previous source is then left as it was).
	/// </summary>
	public static MacInputSource? Select(string id)
	{
		if (!OperatingSystem.IsMacOS())
			return null;
		var before = Current();
		if (before == id)
			return new MacInputSource(null); // already active: nothing to restore
		if (!Set(id))
			return null;
		for (var attempt = 0; attempt < 40; attempt++)
		{
			if (Current() == id)
				return new MacInputSource(before);
			Thread.Sleep(50);
		}
		if (before != null)
			Set(before);
		return null;
	}

	public static string? Current()
	{
		var source = TISCopyCurrentKeyboardInputSource();
		if (source == IntPtr.Zero)
			return null;
		try { return SourceId(source); }
		finally { CFRelease(source); }
	}

	static bool Set(string id)
	{
		var key = SourceIdKey();
		var value = CFStringCreateWithCString(IntPtr.Zero, id, Utf8);
		if (value == IntPtr.Zero)
			return false;
		var filter = CFDictionaryCreate(IntPtr.Zero, new[] { key }, new[] { value }, 1, IntPtr.Zero, IntPtr.Zero);
		var list = filter == IntPtr.Zero ? IntPtr.Zero : TISCreateInputSourceList(filter, false);
		try
		{
			if (list == IntPtr.Zero || CFArrayGetCount(list) == 0)
				return false;
			return TISSelectInputSource(CFArrayGetValueAtIndex(list, 0)) == 0;
		}
		finally
		{
			if (list != IntPtr.Zero) CFRelease(list);
			if (filter != IntPtr.Zero) CFRelease(filter);
			CFRelease(value);
		}
	}

	static IntPtr SourceIdKey()
	{
		// kTISPropertyInputSourceID is an exported data symbol holding the CFStringRef.
		var handle = NativeLibrary.Load(Carbon);
		return Marshal.ReadIntPtr(NativeLibrary.GetExport(handle, "kTISPropertyInputSourceID"));
	}

	static string? SourceId(IntPtr source)
	{
		var value = TISGetInputSourceProperty(source, SourceIdKey());
		if (value == IntPtr.Zero)
			return null;
		var buffer = new byte[256];
		return CFStringGetCString(value, buffer, buffer.Length, Utf8) ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0)) : null;
	}

	public void Dispose()
	{
		if (previous != null)
			Set(previous);
	}
}
