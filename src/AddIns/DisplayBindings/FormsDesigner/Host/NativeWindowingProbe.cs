using System.Runtime.InteropServices;

namespace ICSharpCode.FormsDesigner.Host;

/// <summary>
/// What the LibreWinForms window backend (Silk.NET over GLFW) needs on Linux, described when it cannot create a window.
/// Silk.NET only reports "GlfwPlatform - not applicable", which hides whether the display is missing, an X11 library
/// is missing or GLFW itself refused; CI runners show that failure and nothing else (issue: designerLoaded=false on
/// the Linux lanes). The text goes into the load error, which the IDE logs and the CI failure diagnostics print.
/// </summary>
static class NativeWindowingProbe
{
	static readonly string[] Libraries = {
		"libX11.so.6", "libX11-xcb.so.1", "libXrandr.so.2", "libXinerama.so.1", "libXcursor.so.1", "libXi.so.6",
		"libXext.so.6", "libXrender.so.1", "libxkbcommon.so.0", "libGL.so.1", "libEGL.so.1", "libwayland-client.so.0",
	};

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GlfwInit();
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GlfwGetError(out IntPtr description);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GlfwGetPlatform();
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void GlfwTerminate();

	public static string Describe()
	{
		try {
			var parts = new List<string>();
			foreach (var name in new[] { "DISPLAY", "WAYLAND_DISPLAY", "XDG_SESSION_TYPE", "XDG_RUNTIME_DIR", "LD_LIBRARY_PATH" })
				parts.Add(name + "=" + (Environment.GetEnvironmentVariable(name) ?? "<unset>"));
			if (!OperatingSystem.IsLinux())
				return string.Join("; ", parts);
			foreach (var library in Libraries) {
				if (NativeLibrary.TryLoad(library, out var handle)) {
					parts.Add(library + "=ok");
					NativeLibrary.Free(handle);
				} else {
					parts.Add(library + "=MISSING");
				}
			}
			parts.Add("glfw: " + DescribeGlfw());
			return string.Join("; ", parts);
		} catch (Exception ex) {
			return "probe failed: " + ex.GetType().Name + ": " + ex.Message;
		}
	}

	static string DescribeGlfw()
	{
		var architecture = RuntimeInformation.ProcessArchitecture switch {
			Architecture.X64 => "linux-x64",
			Architecture.Arm64 => "linux-arm64",
			Architecture.Arm => "linux-arm",
			var other => "linux-" + other.ToString().ToLowerInvariant(),
		};
		var path = Path.Combine(AppContext.BaseDirectory, "runtimes", architecture, "native", "libglfw.so.3");
		IntPtr glfw;
		try {
			glfw = NativeLibrary.Load(path);
		} catch (Exception ex) {
			return "cannot load " + path + ": " + ex.Message;
		}
		var init = Marshal.GetDelegateForFunctionPointer<GlfwInit>(NativeLibrary.GetExport(glfw, "glfwInit"));
		var getError = Marshal.GetDelegateForFunctionPointer<GlfwGetError>(NativeLibrary.GetExport(glfw, "glfwGetError"));
		var terminate = Marshal.GetDelegateForFunctionPointer<GlfwTerminate>(NativeLibrary.GetExport(glfw, "glfwTerminate"));
		var ok = init() != 0;
		var code = getError(out var description);
		var text = description == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(description);
		var platform = "";
		if (ok && NativeLibrary.TryGetExport(glfw, "glfwGetPlatform", out var getPlatform))
			platform = " platform=0x" + Marshal.GetDelegateForFunctionPointer<GlfwGetPlatform>(getPlatform)().ToString("x");
		if (ok)
			terminate();
		return (ok ? "init ok" : "init FAILED") + platform + (code != 0 ? $" error=0x{code:x} {text}" : "") + " (" + path + ")";
	}
}
