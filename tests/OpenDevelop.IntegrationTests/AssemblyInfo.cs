using Xunit;

// One assembly fixture owns the sole OpenDevelop process. Named collections expose each test
// class as an orderable workflow; FixtureTestCollectionOrderer fixes their cross-class order and
// FixtureTestCaseOrderer fixes the fixture/scenario order inside mixed workflow classes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: AssemblyFixture(typeof(OpenDevelop.IntegrationTests.OpenDevelopAppFixture))]
[assembly: TestCaseOrderer(typeof(OpenDevelop.IntegrationTests.FixtureTestCaseOrderer))]
[assembly: TestCollectionOrderer(typeof(OpenDevelop.IntegrationTests.FixtureTestCollectionOrderer))]
