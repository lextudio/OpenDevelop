using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WinUIXamlDesigner.UnoDesignHost;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

/// <summary>
/// Drives the IDE-side client (the same <c>UnoDesignClient</c> the runtime host uses, here its standalone build) against
/// the real Surface child, launched the way the Surface runtime host launches it: no runtimeconfig/deps of an app, and
/// the child's own options passed through <c>extraArguments</c>. Needs OD_SURFACE_HOST_DIR (a matched host's pristine
/// directory, a provisioner cache entry's <c>host</c> folder).
/// </summary>
public class IdeClientTests
{
	static CancellationToken Ct => TestContext.Current.CancellationToken;

	[Fact]
	public async Task Client_launches_the_surface_child_with_extra_arguments_and_renders()
	{
		string? hostDir = Environment.GetEnvironmentVariable("OD_SURFACE_HOST_DIR");
		if (string.IsNullOrEmpty(hostDir) || !OperatingSystem.IsWindows())
			Assert.Skip("OD_SURFACE_HOST_DIR is not set (or not Windows).");
		string runRoot = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "odsurf-ide-" + Guid.NewGuid().ToString("N")[..6]);
		Directory.CreateDirectory(runRoot);
		try {
			string extra = $"--host-dir \"{hostDir}\" --run-root \"{runRoot}\"";
			using var client = await UnoDesignClient.AcquireSharedAsync(
				"", "", Ct, SurfaceChildProcessTests.ChildDll(), null, null, null, extra);

			var capabilities = await client.GetCapabilitiesAsync(Ct);
			Assert.Equal("WinUI.Surface", capabilities.Runtime);

			var snapshot = new DesignerDocumentSnapshot { SessionId = client.SessionId, DocumentId = client.DocumentId, Version = 1, PrimaryFileName = "MainPage.xaml" };
			snapshot.Files.Add(new DesignerSourceFileSnapshot {
				FileName = "MainPage.xaml", Kind = "Source",
				Text = "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Button Content=\"ide\"/></Page>",
			});
			var opened = await client.OpenAsync(snapshot, new DesignerViewport { Width = 480, Height = 320, Dpi = 1.0 }, Ct);

			Assert.True(opened.Accepted, opened.Error);
			Assert.Equal("Page", opened.RootType);
			Assert.StartsWith("iVBOR", opened.Render!.PngBase64);
			Assert.Single(Directory.GetDirectories(runRoot));   // the child made this document's private run copy
		} finally {
			TestFiles.TryDelete(runRoot);
		}
	}

	/// <summary>
	/// A canvas click as the IDE sends it: <c>IDesignHostHitTesting.HitTestAsync</c> on the real client, through the child, answered
	/// by the real surface (which only selects in its native-window mode; the child enters and leaves it around the pick).
	/// </summary>
	[Fact]
	public async Task Client_hit_tests_a_named_element_through_the_surface_child_and_keeps_rendering()
	{
		string? hostDir = Environment.GetEnvironmentVariable("OD_SURFACE_HOST_DIR");
		if (string.IsNullOrEmpty(hostDir) || !OperatingSystem.IsWindows())
			Assert.Skip("OD_SURFACE_HOST_DIR is not set (or not Windows).");
		string runRoot = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "odsurf-ide-" + Guid.NewGuid().ToString("N")[..6]);
		Directory.CreateDirectory(runRoot);
		try {
			using var client = await UnoDesignClient.AcquireSharedAsync(
				"", "", Ct, SurfaceChildProcessTests.ChildDll(), null, null, null, $"--host-dir \"{hostDir}\" --run-root \"{runRoot}\"");
			const string xaml = "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">"
				+ "<StackPanel><Button x:Name=\"Go\" Content=\"Click me\"/><TextBlock Text=\"unnamed text\"/></StackPanel></Page>";
			DesignerDocumentSnapshot Snapshot(long version)
			{
				var snapshot = new DesignerDocumentSnapshot { SessionId = client.SessionId, DocumentId = client.DocumentId, Version = version, PrimaryFileName = "MainPage.xaml" };
				snapshot.Files.Add(new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = xaml });
				return snapshot;
			}
			var viewport = new DesignerViewport { Width = 480, Height = 320, Dpi = 1.0 };
			Assert.True((await client.OpenAsync(Snapshot(1), viewport, Ct)).Accepted);

			var onButton = await client.HitTestAsync(1, 20, 15, Ct);
			var onUnnamed = await client.HitTestAsync(1, 20, 45, Ct);
			var onEmpty = await client.HitTestAsync(1, 450, 300, Ct);

			Assert.True(onButton.Hit);
			Assert.Equal(("Go", "Button"), (onButton.ComponentName, onButton.ComponentType));
			Assert.False(onUnnamed.Hit);   // an unnamed element is not offered: the surface's path is not the shell's
			Assert.False(onEmpty.Hit);
			// Leaving native mode after each pick means a render right after a click is still a frame.
			var updated = await client.UpdateAsync(Snapshot(2), viewport, Ct);
			Assert.True(updated.Accepted, updated.Error);
			Assert.StartsWith("iVBOR", updated.Render!.PngBase64);
		} finally {
			TestFiles.TryDelete(runRoot);
		}
	}
}
