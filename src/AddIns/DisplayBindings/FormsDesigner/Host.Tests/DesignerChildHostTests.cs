using System.Net;
using System.Net.Sockets;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.FormsDesigner.Host.Tests;

public sealed class DesignerChildHostTests
{
	[Fact]
	public async Task Run_ExitsWhenParentTransportDisconnectsWithoutShutdownRpc()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		var service = new NeverExplicitlyShutdownService();

		var child = Task.Run(() => DesignerChildHost.Run(
			new[] { "--port", port.ToString(), "--token", "test-token" },
			"DesignerChildHostTests", _ => service));
		using var parentConnection = await listener.AcceptTcpClientAsync(timeout.Token);

		// Model the IDE being killed: close its transport without invoking the child's shutdown
		// method. The bootstrap must observe JsonRpc.Completion and return on its own.
		parentConnection.Dispose();

		Assert.Equal(0, await child.WaitAsync(timeout.Token));
		Assert.True(service.WaitCompleted);
	}

	sealed class NeverExplicitlyShutdownService : IDesignerChildService
	{
		readonly ManualResetEventSlim shutdown = new(false);
		public bool WaitCompleted { get; private set; }

		public void WaitForShutdown()
		{
			shutdown.Wait();
			WaitCompleted = true;
		}

		public void OnParentDisconnected() => shutdown.Set();
	}
}
