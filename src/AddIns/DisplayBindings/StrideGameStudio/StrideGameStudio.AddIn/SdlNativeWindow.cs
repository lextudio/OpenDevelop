// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Extracts the real Cocoa NSWindow handle SDL created for one of Stride's own SDL windows, via
// SDL_GetWindowWMInfo. This is the "let SDL own its window" half of the composition bridge - see
// doc/technotes/stride-game-studio.md "Composition-bridge probes" for why the alternative (a
// foreign NSView handed to SDL_CreateWindowFrom) is not viable.

using System;
using Silk.NET.SDL;
using SdlWindow = Stride.Graphics.SDL.Window;

namespace ICSharpCode.StrideGameStudio
{
	static class SdlNativeWindow
	{
		public static unsafe IntPtr GetCocoaNsWindow(SdlWindow window)
		{
			var sdl = SdlWindow.SDL;
			var wmInfo = new SysWMInfo();
			sdl.GetVersion(ref wmInfo.Version);

			var sdlWin = (Silk.NET.SDL.Window*)window.SdlHandle;
			if (!sdl.GetWindowWMInfo(sdlWin, &wmInfo))
			{
				ICSharpCode.Core.LoggingService.Warn("[StrideSdlViewport] SDL_GetWindowWMInfo failed: " + sdl.GetErrorS());
				return IntPtr.Zero;
			}

			return (IntPtr)wmInfo.Info.Cocoa.Window;
		}
	}
}
