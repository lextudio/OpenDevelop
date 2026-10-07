using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinUIHotReload;

/// <summary>One live XAML element as WinUI's XAML Diagnostics reports it.</summary>
internal sealed record LiveElement(ulong Handle, string Type, string Name, string File, uint Line, uint Column);

/// <summary>
/// Flat-export bindings to WinUIHotReload.Tap. The TAP owns everything COM - the
/// IXamlDiagnostics site, the tree callback, UI-thread marshalling - so this side only moves
/// strings and handles.
/// </summary>
internal sealed unsafe class TapInterop
{
	readonly delegate* unmanaged[Stdcall]<int> isReady;
	readonly delegate* unmanaged[Stdcall]<delegate* unmanaged[Stdcall]<ulong, char*, char*, char*, uint, uint, nint, void>, nint, int> getElements;
	readonly delegate* unmanaged[Stdcall]<ulong, char*, char*, char*, int> setProperty;
	readonly delegate* unmanaged[Stdcall]<ulong, char*, nint*, int> getStringProperty;
	readonly delegate* unmanaged[Stdcall]<delegate* unmanaged[Stdcall]<nint, int>, nint, int> runOnUiThread;
	readonly delegate* unmanaged[Stdcall]<ulong, nint*, int> getObject;

	public TapInterop(nint module)
	{
		isReady = (delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(module, "WinUIHR_IsReady");
		getElements = (delegate* unmanaged[Stdcall]<delegate* unmanaged[Stdcall]<ulong, char*, char*, char*, uint, uint, nint, void>, nint, int>)
			NativeLibrary.GetExport(module, "WinUIHR_GetElements");
		setProperty = (delegate* unmanaged[Stdcall]<ulong, char*, char*, char*, int>)NativeLibrary.GetExport(module, "WinUIHR_SetProperty");
		getStringProperty = (delegate* unmanaged[Stdcall]<ulong, char*, nint*, int>)NativeLibrary.GetExport(module, "WinUIHR_GetStringProperty");
		runOnUiThread = (delegate* unmanaged[Stdcall]<delegate* unmanaged[Stdcall]<nint, int>, nint, int>)NativeLibrary.GetExport(module, "WinUIHR_RunOnUiThread");
		getObject = (delegate* unmanaged[Stdcall]<ulong, nint*, int>)NativeLibrary.GetExport(module, "WinUIHR_GetObject");
	}

	/// <summary>
	/// Runs <paramref name="work"/> on the application's UI thread and waits for it. An exception
	/// thrown there is rethrown here, so callers see WinUI's own error (a XamlParseException, say).
	/// Never call from the UI thread.
	/// </summary>
	public T RunOnUiThread<T>(Func<T> work)
	{
		var box = new UiWork(() => work());
		var handle = GCHandle.Alloc(box);
		try {
			var hr = runOnUiThread(&InvokeUiWork, GCHandle.ToIntPtr(handle));
			if (box.Error != null)
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(box.Error).Throw();
			Marshal.ThrowExceptionForHR(hr);
			return (T)box.Result!;
		} finally {
			handle.Free();
		}
	}

	sealed class UiWork
	{
		public UiWork(Func<object?> work) => Work = work;
		public Func<object?> Work { get; }
		public object? Result;
		public Exception? Error;
	}

	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
	static int InvokeUiWork(nint context)
	{
		var work = (UiWork)GCHandle.FromIntPtr(context).Target!;
		try {
			work.Result = work.Work();
			return 0;
		} catch (Exception ex) {
			// Never let an exception unwind into native code; it is rethrown on the calling side.
			work.Error = ex;
			return unchecked((int)0x80004005);
		}
	}

	/// <summary>The live object behind a handle, projected by the application's own CsWinRT.</summary>
	public object GetObject(ulong handle)
	{
		nint inspectable = 0;
		Marshal.ThrowExceptionForHR(getObject(handle, &inspectable));
		try {
			return WinRTBridge.FromAbi(inspectable);
		} finally {
			Marshal.Release(inspectable);
		}
	}

	/// <summary>True once WinUI has handed the TAP its site and the live tree has been replayed.</summary>
	public bool IsReady => isReady() == 0;

	public List<LiveElement> GetElements()
	{
		var elements = new List<LiveElement>();
		var handle = GCHandle.Alloc(elements);
		try {
			Marshal.ThrowExceptionForHR(getElements(&OnElement, GCHandle.ToIntPtr(handle)));
		} finally {
			handle.Free();
		}
		return elements;
	}

	[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
	static void OnElement(ulong handle, char* type, char* name, char* file, uint line, uint column, nint context)
	{
		var elements = (List<LiveElement>)GCHandle.FromIntPtr(context).Target!;
		elements.Add(new LiveElement(handle, new string(type), new string(name), new string(file), line, column));
	}

	/// <summary>Sets a property from its XAML string form; returns the HRESULT.</summary>
	public int SetProperty(ulong handle, string propertyFullName, string winRtValueType, string value)
	{
		fixed (char* p = propertyFullName)
		fixed (char* t = winRtValueType)
		fixed (char* v = value)
			return setProperty(handle, p, t, v);
	}

	public string? GetStringProperty(ulong handle, string propertyFullName, out int hr)
	{
		nint bstr = 0;
		fixed (char* p = propertyFullName)
			hr = getStringProperty(handle, p, &bstr);
		if (bstr == 0)
			return hr >= 0 ? "" : null;
		try {
			return Marshal.PtrToStringBSTR(bstr);
		} finally {
			Marshal.FreeBSTR(bstr);
		}
	}
}
