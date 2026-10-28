using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// The integrated Terminal pad, as seen by the shell (doc/technotes/terminal.md). The Terminal add-in
/// registers its implementation with <c>SD.Services.AddService(typeof(ITerminalHost), ...)</c>; callers
/// such as the Project Browser's "Open in Terminal" use it when present and fall back to an external
/// terminal application otherwise.
/// </summary>
public interface ITerminalHost
{
	/// <summary>Shows the Terminal pad and starts a new terminal in <paramref name="workingDirectory"/>.</summary>
	Task OpenTerminalAsync(string workingDirectory);

	/// <summary>Shows the Terminal pad and starts a terminal with an explicit executable and startup contract.</summary>
	Task OpenTerminalAsync(TerminalLaunchRequest request);
}

/// <summary>
/// A deliberate, host-neutral request for the integrated terminal. Add-ins can use this to provide
/// an IDE-aware shell session without taking a dependency on Terminal's PTY or rendering types.
/// </summary>
public sealed class TerminalLaunchRequest
{
	public string Executable { get; init; }
	public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
	public string WorkingDirectory { get; init; }
	public string Title { get; init; }
	/// <summary>Stable identity for reusing an IDE-owned terminal; distinct from its user-visible title.</summary>
	public string SessionKey { get; init; }
	public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}
