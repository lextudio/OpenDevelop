using System.Runtime.InteropServices;

namespace OpenDevelop.Terminal
{
	/// <summary>Facts about a running process that the operating system knows and the shell does not have to announce.</summary>
	static class TerminalProcessInfo
	{
		[DllImport("libproc.dylib")]
		static extern int proc_pidinfo(int pid, int flavor, ulong arg, IntPtr buffer, int bufferSize);

		const int ProcPidVnodePathInfo = 9;
		// struct proc_vnodepathinfo { vnode_info_path cdir; vnode_info_path rdir; }, each a 152-byte vnode_info plus a 1024-byte path
		const int VnodePathInfoSize = 2 * (152 + 1024);
		const int CurrentDirectoryPathOffset = 152;

		/// <summary>The process's current directory, or null when it cannot be read (the process is gone, or the platform has no way: Windows).</summary>
		public static string GetWorkingDirectory(int pid)
		{
			try {
				if (OperatingSystem.IsMacOS()) {
					var buffer = Marshal.AllocHGlobal(VnodePathInfoSize);
					try {
						if (proc_pidinfo(pid, ProcPidVnodePathInfo, 0, buffer, VnodePathInfoSize) != VnodePathInfoSize)
							return null;
						var path = Marshal.PtrToStringUTF8(buffer + CurrentDirectoryPathOffset);
						return string.IsNullOrEmpty(path) ? null : path;
					} finally {
						Marshal.FreeHGlobal(buffer);
					}
				}
				if (OperatingSystem.IsLinux())
					return new FileInfo($"/proc/{pid}/cwd").LinkTarget;
			} catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException or UnauthorizedAccessException) {
			}
			return null;
		}
	}
}
