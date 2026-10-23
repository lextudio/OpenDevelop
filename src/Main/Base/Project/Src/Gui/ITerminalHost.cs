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
}
