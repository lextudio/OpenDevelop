using ICSharpCode.SharpDevelop.LanguageServices;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>The registry relays "the editor closed this file" to the service that owns it, and only to a
/// service that asked for it (<see cref="ILanguageServiceDocumentLifecycle"/>).</summary>
public sealed class LanguageServiceDocumentLifecycleTests
{
	[Fact]
	public void NotifyDocumentClosed_ReachesTheServiceRegisteredForTheExtension()
	{
		var registry = new LanguageServiceRegistry();
		var service = new FakeLanguageService();
		var other = new FakeLanguageService();
		registry.RegisterExtension(".ui", service);
		registry.RegisterExtension(".po", other);

		registry.NotifyDocumentClosed("/project/Main.ui");

		Assert.Equal(new[] { "/project/Main.ui" }, service.ClosedFiles);
		Assert.Empty(other.ClosedFiles);
	}

	[Fact]
	public void NotifyDocumentClosed_IgnoresUnregisteredAndExtensionlessFiles_AndServicesWithoutTheCapability()
	{
		var registry = new LanguageServiceRegistry();
		registry.RegisterExtension(".cs", NoOpLanguageService.Instance);
		registry.NotifyDocumentClosed("/project/Readme");
		registry.NotifyDocumentClosed("/project/other.xyz");
		registry.NotifyDocumentClosed("/project/Program.cs");   // a service without the capability: nothing to call, nothing thrown
		registry.NotifyDocumentClosed("");
	}
}
