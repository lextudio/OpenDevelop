using Debugger.AddIn.Service.Dap;
using ICSharpCode.SharpDevelop.Services;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Issue #27: an exception thrown inside framework code stops with a top frame that has no source, so
/// "Break" showed nothing. The frame shown must be the first one with source: the user's own line.
/// </summary>
public class ExceptionStopFrameTests
{
	static DapStackFrameInfo Frame(int id, string path, int token = 0) => new DapStackFrameInfo { Id = id, FilePath = path, Line = path == null ? 0 : 10, MethodToken = token };

	[Fact]
	public void FrameworkTopFrames_SelectTheFirstFrameWithSource()
	{
		var frames = new[] { Frame(1, null), Frame(2, null), Frame(3, "/src/Program.cs"), Frame(4, "/src/Other.cs") };
		Assert.Equal(3, WindowsDebugger.SelectExceptionStopFrame(frames)!.Id);
	}

	[Fact]
	public void TopFrameWithSource_IsKept()
	{
		var frames = new[] { Frame(1, "/src/Program.cs"), Frame(2, null) };
		Assert.Equal(1, WindowsDebugger.SelectExceptionStopFrame(frames)!.Id);
	}

	[Fact]
	public void NoFrameWithSource_SelectsTheFirstDecompilableFrame()
	{
		// A native transition frame has no method token; the frame below it can be decompiled.
		var frames = new[] { Frame(1, null), Frame(2, null, token: 100669992), Frame(3, null, token: 100668726) };
		Assert.Equal(2, WindowsDebugger.SelectExceptionStopFrame(frames)!.Id);
	}

	[Fact]
	public void NothingToShow_FallsBackToTheTopFrame()
	{
		var frames = new[] { Frame(1, null), Frame(2, "") };
		Assert.Equal(1, WindowsDebugger.SelectExceptionStopFrame(frames)!.Id);
	}

	[Fact]
	public void EmptyOrMissingStack_HasNoFrame()
	{
		Assert.Null(WindowsDebugger.SelectExceptionStopFrame(System.Array.Empty<DapStackFrameInfo>()));
		Assert.Null(WindowsDebugger.SelectExceptionStopFrame(null));
	}

	[Fact]
	public void VirtualDecompiledPath_IsNotASourceFile()
	{
		// The adapter keeps a decompiled frame's "decompiled/..." path after resolving it.
		var decompiled = Frame(2, "decompiled/System.Private.CoreLib/abc/System.Threading/Monitor.cs", token: 100669992);
		Assert.False(WindowsDebugger.HasSourceFile(decompiled));
		Assert.True(WindowsDebugger.HasSourceFile(Frame(3, "/src/Program.cs")));
		Assert.False(WindowsDebugger.HasSourceFile(null));
	}

	[Fact]
	public void VirtualDecompiledPath_IsSkippedForARealSourceFrameBelow()
	{
		var frames = new[] { Frame(1, null), Frame(2, "decompiled/System.Private.CoreLib/abc/Int32.cs", token: 7), Frame(3, "/src/Program.cs") };
		Assert.Equal(3, WindowsDebugger.SelectExceptionStopFrame(frames)!.Id);
	}

	[Fact]
	public void VirtualDecompiledPath_IsTheDecompilableFrameWhenNothingHasSource()
	{
		var frames = new[] { Frame(1, null), Frame(2, "decompiled/System.Private.CoreLib/abc/Monitor.cs", token: 7) };
		Assert.Equal(2, WindowsDebugger.SelectExceptionStopFrame(frames)!.Id);
	}

	[Fact]
	public void ExceptionStop_WithAVirtualPathFrame_IsShownDecompiled_EvenWithoutExceptionDetails()
	{
		// The decision must not depend on the exception details: the adapter can answer
		// exceptionInfo with an error, and the virtual path must never reach FileService.OpenFile.
		var virtualFrame = Frame(2, "decompiled/System.Private.CoreLib/abc/Monitor.cs", token: 7);
		Assert.True(WindowsDebugger.ShouldShowFrameDecompiled(isExceptionStop: true, virtualFrame));
		Assert.True(WindowsDebugger.ShouldShowFrameDecompiled(isExceptionStop: true, Frame(1, null)));
	}

	[Fact]
	public void ShouldShowFrameDecompiled_IsFalseForRealSourceOtherStopsAndNoFrame()
	{
		Assert.False(WindowsDebugger.ShouldShowFrameDecompiled(isExceptionStop: true, Frame(3, "/src/Program.cs")));
		Assert.False(WindowsDebugger.ShouldShowFrameDecompiled(isExceptionStop: false, Frame(1, null)));
		Assert.False(WindowsDebugger.ShouldShowFrameDecompiled(isExceptionStop: true, null));
	}
}
