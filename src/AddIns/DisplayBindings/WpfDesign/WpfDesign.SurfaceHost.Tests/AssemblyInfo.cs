using Xunit;

// A transient batch of 3/19 tests once got a null Render with no logged exception during
// GPU-render investigation, possibly from concurrent ProGpuWpfCompositionTarget.CreateHeadless()
// across independently-spawned child processes under xunit's default parallelism. This was added
// as a defensive measure, but the failure did not reliably reproduce even before this change, so
// its effectiveness is unproven - the root cause may have been incidental system load during rapid
// rebuild/DLL-swap cycles rather than GPU-context concurrency. Left in place since it's harmless
// either way. See doc/technotes/wpf-designer.md's Phase 1 progress notes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
