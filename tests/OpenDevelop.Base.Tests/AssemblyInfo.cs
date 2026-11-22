using Xunit;

// SD.InitializeForUnitTests replaces one process-wide service container. Several test classes
// correctly declare ProcessWideStateCollection, but UIFact runs its STA work on a separate
// scheduler and can overlap a collection teardown with another class's service lookup. Keep this
// assembly serial until SD no longer uses static process-wide test state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
