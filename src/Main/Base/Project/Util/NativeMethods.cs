// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ICSharpCode.SharpDevelop
{
	/// <summary>
	/// Contains P/Invoke methods for functions in the Windows API.
	/// </summary>
	static class NativeMethods
	{
		static readonly IntPtr FALSE = new IntPtr(0);
		static readonly IntPtr TRUE = new IntPtr(1);
		
		public const int WM_SETREDRAW = 0x00B;
		public const int WM_USER = 0x400;
		
		[DllImport("kernel32", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool CloseHandle(IntPtr hObject);
		
		[DllImport("kernel32.dll")]
		internal static extern IntPtr GetCurrentProcess();
		
		[DllImport("kernel32.dll", BestFitMapping = false, CharSet = CharSet.Ansi)]
		internal static extern bool DuplicateHandle(HandleRef hSourceProcessHandle, SafeHandle hSourceHandle, HandleRef hTargetProcess, out SafeWaitHandle targetHandle, int dwDesiredAccess, bool bInheritHandle, int dwOptions);
		
		internal const int DUPLICATE_SAME_ACCESS = 2;

		// Note: the Win32 SHFileOperation-based DeleteToRecycleBin() (shell32.dll, recycle-bin delete) and
		// the user32.dll SendMessage/SetForegroundWindow P/Invokes have been removed - they are Win32-only
		// with no cross-platform meaning and had no real callers left once the WinForms bridge was stripped.

		#region SetFileTime
		[StructLayout(LayoutKind.Sequential)]
		struct FILETIME
		{
			internal uint ftTimeLow;
			internal uint ftTimeHigh;

			public FILETIME(long fileTime)
			{
				unchecked {
    				this.ftTimeLow = (uint)fileTime;
    				this.ftTimeHigh = (uint)(fileTime >> 32);
				}
			}
		}
		
		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		unsafe static extern bool SetFileTime(SafeFileHandle hFile, FILETIME* creationTime, FILETIME* lastAccessTime, FILETIME* lastWriteTime);
		
		/// <summary>
		/// Update the file times on the given file handle.
		/// </summary>
		public unsafe static void SetFileCreationTime(SafeFileHandle hFile, DateTime creationTime)
		{
		    FILETIME fileCreationTime = new FILETIME(creationTime.ToFileTimeUtc());
			if (!SetFileTime(hFile, &fileCreationTime, null, null)) {
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
		}
		#endregion
		
		#region Get OEM Encoding
		[DllImport("kernel32.dll")]
		static extern int GetOEMCP();
		
		public static Encoding OemEncoding {
			get {
				try {
					return Encoding.GetEncoding(GetOEMCP());
				} catch (ArgumentException) {
					return Encoding.Default;
				} catch (NotSupportedException) {
					return Encoding.Default;
				}
			}
		}
		#endregion
	}
}
