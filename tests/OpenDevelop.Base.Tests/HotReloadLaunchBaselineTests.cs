using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ICSharpCode.SharpDevelop.Project.HotReload;
using ProtocolHotReloadDeltaStatus = ICSharpCode.SharpDevelop.LanguageServices.Protocol.HotReloadDeltaStatus;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class HotReloadLaunchBaselineTests
{
	[Fact]
	public async Task CopiesTheLaunchAssemblyAndPortablePdbToTheHost()
	{
		var directory = Directory.CreateTempSubdirectory("HotReloadBaseline-");
		try {
			var assembly = Path.Combine(directory.FullName, "App.dll");
			var pdb = Path.ChangeExtension(assembly, ".pdb");
			await File.WriteAllBytesAsync(assembly, new byte[] { 1, 2, 3 });
			await File.WriteAllBytesAsync(pdb, new byte[] { 4, 5, 6 });
			var transport = new CapturingTransport();
			using var protocol = new RemoteRoslynLanguageProtocol(transport);

			var result = await HotReloadLaunchBaseline.CaptureAsync(protocol,
				new HotReloadProjectId("/tmp/App.csproj", "net11.0"), assembly, TestContext.Current.CancellationToken);

			Assert.Equal(ProtocolHotReloadDeltaStatus.Applied, result.Status);
			Assert.Equal(new byte[] { 1, 2, 3 }, transport.Request!.ModuleImage);
			Assert.Equal(new byte[] { 4, 5, 6 }, transport.Request.PdbImage);
		} finally { directory.Delete(true); }
	}

	[Fact]
	public async Task RejectsALaunchWithoutPortablePdb()
	{
		var directory = Directory.CreateTempSubdirectory("HotReloadBaseline-");
		try {
			var assembly = Path.Combine(directory.FullName, "App.dll");
			await File.WriteAllBytesAsync(assembly, new byte[] { 1 });
			using var protocol = new RemoteRoslynLanguageProtocol(new CapturingTransport());
			var result = await HotReloadLaunchBaseline.CaptureAsync(protocol,
				new HotReloadProjectId("/tmp/App.csproj", null), assembly, TestContext.Current.CancellationToken);
			Assert.Equal(ProtocolHotReloadDeltaStatus.Failed, result.Status);
			Assert.Contains("PDB", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
		} finally { directory.Delete(true); }
	}

	sealed class CapturingTransport : IRoslynProtocolTransport
	{
		public HotReloadBaselineRequest? Request { get; private set; }

		public Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken token)
		{
			Assert.Equal(RoslynProtocolMethods.HotReloadCaptureBaseline, method);
			Request = (HotReloadBaselineRequest)arguments.GetType()
				.GetProperty("hotReloadBaselineRequest")!.GetValue(arguments)!;
			return Task.FromResult((T)(object)new HotReloadDeltaResponse(ProtocolHotReloadDeltaStatus.Applied, null, "captured"));
		}
	}
}
