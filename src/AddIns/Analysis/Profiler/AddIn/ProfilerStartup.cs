using ICSharpCode.Core;

namespace OpenDevelop.Profiler.AddIn;

/// <summary>
/// Loads the add-in's assembly when the IDE starts. The DevFlow agent registers an add-in's integration-test
/// probes (<c>od.profiler.*</c>, see <see cref="ProfilerDevFlowActions"/>) only once its assembly is loaded, and
/// every other entry point here (menu commands, the <c>.nettrace</c> display binding) is created on first use,
/// so without this the probes would exist only after someone had happened to open a capture.
/// </summary>
public sealed class ProfilerStartup : AbstractCommand
{
    public override void Run()
    {
        // Nothing to do: creating this class is what loads the assembly.
    }
}
