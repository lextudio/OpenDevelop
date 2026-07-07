using Xunit;

// All test classes here share the single "OpenDevelop app" collection (one shared
// OpenDevelopAppFixture: one SharpDevelop process, one DevFlow port), which already makes
// xunit run them sequentially relative to each other. This attribute makes that requirement
// explicit at the assembly level too, so a future test class added *without* joining that
// collection can't accidentally run in parallel against the same live app/port.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
