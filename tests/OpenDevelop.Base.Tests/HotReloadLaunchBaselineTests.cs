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

	/// <summary>
	/// An SDK app launches its apphost, a native executable without metadata; the baseline must be
	/// the managed assembly beside it, or the host fails with "PE image doesn't contain managed metadata".
	/// </summary>
	[Fact]
	public void AnApphostLaunch_SnapshotsTheManagedAssemblyBesideIt()
	{
		var directory = Directory.CreateTempSubdirectory("HotReloadBaseline-");
		try {
			var apphost = Path.Combine(directory.FullName, "My.App");
			File.WriteAllBytes(apphost, new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' });
			var managed = typeof(HotReloadLaunchBaselineTests).Assembly.Location;
			File.Copy(managed, apphost + ".dll");
			File.WriteAllBytes(Path.Combine(directory.FullName, "My.App.pdb"), new byte[] { 9 });
			// An unrelated managed "My.dll" beside it must not be taken for "My.App"'s assembly.
			File.Copy(typeof(Xunit.Assert).Assembly.Location, Path.Combine(directory.FullName, "My.dll"));

			Assert.True(HotReloadLaunchBaseline.TrySnapshot(new HotReloadProjectId("/tmp/My.App.csproj", null), apphost,
				out var request, out var diagnostic), diagnostic);
			Assert.Equal(File.ReadAllBytes(managed), request!.ModuleImage);
			Assert.Equal(new byte[] { 9 }, request.PdbImage);
			Assert.Equal(apphost + ".dll", HotReloadLaunchBaseline.ManagedModuleFor(apphost));
			Assert.Equal(apphost + ".dll", HotReloadLaunchBaseline.ManagedModuleFor(apphost + ".dll"));
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
