using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WinUIXamlDesigner.UnoDesignHost;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost.Tests;

public sealed class UnoDesignHostRpcTests
{
	const string Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	const string XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

	static string Fixture(string text) =>
		$"""
		<Grid xmlns="{Ns}" xmlns:x="{XNs}" x:Name="root">
		    <TextBlock x:Name="greeting" Text="{text}"/>
		</Grid>
		""";

	/// <summary>The child host binary under test. Defaults to the Uno host; setting
	/// OPENDEVELOP_WINUIDESIGNER_HOST_DLL points this same suite at the Microsoft WinUI 3 host,
	/// which source-links the very same DesignHost/DesignRpc implementation. The DDP contract is
	/// what is being verified and it is identical for both, so the tests are shared rather than
	/// duplicated - only the child binary changes.</summary>
	static string HostDll() =>
		Environment.GetEnvironmentVariable("OPENDEVELOP_WINUIDESIGNER_HOST_DLL") is { Length: > 0 } overridden
			? Path.GetFullPath(overridden)
		#if MICROSOFT_WINUI_DESIGNER_HOST
			: MicrosoftHostDll();
		#else
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
				"../../../../WinUIXamlDesigner.UnoHost/bin/Debug/net10.0-desktop/WinUIXamlDesigner.UnoHost.dll"));
		#endif

	#if MICROSOFT_WINUI_DESIGNER_HOST
	static string MicrosoftHostDll()
	{
		// Exercise the same deployed child the IDE probes.  Besides matching production, this
		// avoids a Windows App SDK bootstrap failure from the much longer source/bin path.
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory != null && !string.Equals(directory.Name, "WinUIXamlDesigner", StringComparison.OrdinalIgnoreCase))
			directory = directory.Parent;
		if (directory == null)
			throw new DirectoryNotFoundException("Could not locate the WinUIXamlDesigner source directory.");
		var repository = directory.Parent?.Parent?.Parent?.Parent;
		var deployed = repository == null ? null : Path.Combine(repository.FullName, "AddIns", "DisplayBindings", "WinUIXamlDesigner", "MicrosoftHost", "WinUIXamlDesigner.MicrosoftHost.dll");
		if (deployed != null && File.Exists(deployed))
			return deployed;

		// Fallback is useful for a developer who built the host but has not deployed it yet.
		return Directory.EnumerateFiles(Path.Combine(directory.FullName, "WinUIXamlDesigner.MicrosoftHost", "bin"),
			"WinUIXamlDesigner.MicrosoftHost.dll", SearchOption.AllDirectories).FirstOrDefault()
			?? throw new FileNotFoundException("Build WinUIXamlDesigner.MicrosoftHost before running its test suite.");
	}
	#endif

	static Task<UnoDesignClient> StartAsync(CancellationToken cancellationToken)
		=> UnoDesignClient.StartAsync(
			Environment.GetEnvironmentVariable("OD_GEOMETRY_RUNTIME_CONFIG") ?? "",
			Environment.GetEnvironmentVariable("OD_GEOMETRY_DEPS_FILE") ?? "",
			cancellationToken,
			HostDll(),
			Environment.GetEnvironmentVariable("OD_GEOMETRY_APP_BIN"));

	/// <summary>Wraps fixture XAML as a single-file document snapshot (the DDP document shape);
	/// the surface size/DPI is presentation state and goes through SetViewport instead.</summary>
	static DesignerDocumentSnapshot Document(UnoDesignClient client, string xaml, long version = 1)
	{
		client.SetViewport(320, 240, 1.0);
		return new DesignerDocumentSnapshot {
			SessionId = client.SessionId,
			DocumentId = client.DocumentId,
			Version = version,
			PrimaryFileName = "MainPage.xaml",
			Language = "",
			Files = { new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = xaml } }
		};
	}

	[Theory]
	[InlineData(0, 0, 320, 1.0)]
	[InlineData(23, 17, 320, 1.0)]
	[InlineData(23, 17, 1280, 1.0)]
	[InlineData(23, 17, 320, 2.0)]
	[InlineData(0, 0, 1280, 1.0, true)]
	[InlineData(0, 0, 1280, 2.0, true)]
	public async Task ChildHost_GeometryMatchesRasterPixels(int translateX, int translateY, int designWidth, double dpi, bool settingsCard = false, string? galleryTarget = null)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await StartAsync(timeout.Token);
		var xaml = $"""
		<Grid xmlns="{Ns}" xmlns:x="{XNs}" Background="White" Width="{designWidth}" Height="240">
		    <Canvas>
		        <Border x:Name="marker" Canvas.Left="110" Canvas.Top="70" Width="60" Height="40" Background="#FFFF00FF">
		            <Border.RenderTransform><TranslateTransform X="{translateX}" Y="{translateY}"/></Border.RenderTransform>
		        </Border>
		    </Canvas>
		</Grid>
		""";
		if (settingsCard) xaml = $"""
		<Grid xmlns="{Ns}" xmlns:x="{XNs}" xmlns:toolkit="using:CommunityToolkit.WinUI.Controls" Background="White" Width="{designWidth}" Height="753">
		    <ScrollView Margin="0,62,0,0">
		        <Border Padding="36,0">
		            <StackPanel MaxWidth="1064" Spacing="4">
		                <Border Height="274"/>
		                <toolkit:SettingsCard Header="Manage samples" Description="Clear your recent or favorite samples">
		                    <StackPanel Orientation="Horizontal" Spacing="8">
		                        <Button MinWidth="120" Content="Clear recents"/>
		                        <Button x:Name="marker" MinWidth="120" Content="Remove favorites" Background="#FFFF00FF" BorderThickness="0" CornerRadius="0"/>
		                    </StackPanel>
		                </toolkit:SettingsCard>
		            </StackPanel>
		        </Border>
		    </ScrollView>
		</Grid>
		""";
		if (galleryTarget != null) {
			var path = Environment.GetEnvironmentVariable("OD_GEOMETRY_GALLERY_PAGE");
			Assert.True(File.Exists(path), "Set OD_GEOMETRY_GALLERY_PAGE to SettingsPage.xaml.");
			var xml = System.Xml.Linq.XDocument.Load(path!);
			if (Environment.GetEnvironmentVariable("OD_GEOMETRY_NO_TRANSITIONS") == "1")
				foreach (var transition in xml.Descendants().Where(e => e.Name.LocalName.EndsWith(".ChildrenTransitions", StringComparison.Ordinal)).ToArray()) transition.Remove();
			var target = xml.Descendants().Single(e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Name", XNs)) == galleryTarget);
			target.SetAttributeValue("Background", "#FFFF00FF");
			target.SetAttributeValue("BorderBrush", "#FFFF00FF");
			target.SetAttributeValue("CornerRadius", "0");
			xaml = xml.ToString();
		}
		var document = Document(client, xaml);
		client.SetViewport(designWidth, 240, dpi);
		var state = await client.OpenAsync(document, timeout.Token);
		Assert.True(state.Accepted, state.Error);
		Assert.NotNull(state.Render);
		var frame = state.Render!;
		var pixels = DesignerFrameCodec.DecodeBgra32(frame);
		var left = frame.Width; var top = frame.Height; var right = -1; var bottom = -1;
		for (var y = 0; y < frame.Height; y++)
			for (var x = 0; x < frame.Width; x++) {
				var i = (y * frame.Width + x) * 4;
				if (pixels[i] > 240 && pixels[i + 1] < 15 && pixels[i + 2] > 240 && pixels[i + 3] > 240) {
					left = Math.Min(left, x); top = Math.Min(top, y);
					right = Math.Max(right, x); bottom = Math.Max(bottom, y);
				}
			}
		Assert.True(right >= left, "Rendered frame contains no magenta marker.");
		var node = FindByName(state.Tree!, galleryTarget ?? "marker")!;
		var error = Math.Max(Math.Max(Math.Abs(node.X * dpi - left), Math.Abs(node.Y * dpi - top)),
			Math.Max(Math.Abs((node.X + node.Width) * dpi - right - 1), Math.Abs((node.Y + node.Height) * dpi - bottom - 1)));
		Assert.True(error <= 1, $"Frame={frame.Width}x{frame.Height}; raster=({left},{top},{right + 1},{bottom + 1}); tree=({node.X},{node.Y},{node.X + node.Width},{node.Y + node.Height}); max edge error={error}");
		var hit = await client.HitTestAsync(1, (left + right + 1) / (2 * dpi), (top + bottom + 1) / (2 * dpi), timeout.Token);
		Assert.Contains(galleryTarget ?? "marker", hit.Chain);
	}

	[Fact(Explicit = true)]
	public async Task GallerySettings_ButtonsMatchRasterPixels()
	{
		foreach (var target in new[] { "ClearRecentBtn", "UnfavoriteBtn" })
			foreach (var dpi in new[] { 1.0, 2.0 })
				await ChildHost_GeometryMatchesRasterPixels(0, 0, 1280, dpi, galleryTarget: target);
	}

	[Fact]
	public async Task ChildHost_HandshakesAndOpensUpdatesFlushesASession()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await StartAsync(timeout.Token);
		Assert.True(client.IsAlive);
		Assert.NotEqual(Environment.ProcessId, client.ProcessId);
		Assert.IsAssignableFrom<IDesignHostEventBinding>(client);
		Assert.IsAssignableFrom<IDesignHostBounds>(client);
		Assert.IsAssignableFrom<IDesignHostHitTesting>(client);

		var opened = await client.OpenAsync(Document(client, Fixture("Hello")), timeout.Token);
		Assert.True(opened.Accepted);
		Assert.NotNull(opened.Tree);
		Assert.NotNull(opened.Render);
		Assert.False(string.IsNullOrEmpty(opened.Render!.Data));
		var hit = await client.HitTestAsync(1, 4, 4, timeout.Token);
		Assert.NotNull(hit);

		var updated = await client.UpdateAsync(Document(client, Fixture("Updated"), 2), timeout.Token);
		Assert.True(updated.Accepted);
		Assert.NotNull(updated.Tree);
		Assert.NotNull(updated.Render);
		// The rendered frame carries no reliable monotonic sequence on this backend (Sequence
		// is not populated by the Uno child) - a changed document produces a different
		// compressed frame, which is the useful proxy that a fresh render actually happened.
		Assert.NotEqual(opened.Render.Data, updated.Render!.Data);

		var flushed = await client.FlushAsync(2, timeout.Token);
		var document = Assert.Single(flushed.Files);
		Assert.Contains("Updated", document.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_SupportsElementMutationRpcs()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await StartAsync(timeout.Token);
		var opened = await client.OpenAsync(Document(client, Fixture("Hello")), timeout.Token);
		Assert.True(opened.Accepted);

		var edited = await client.SetPropertyAsync(1, "greeting", "Text", "Edited", timeout.Token);
		Assert.True(edited.Accepted);
		Assert.NotNull(edited.Render);
		Assert.NotEqual(opened.Render!.Data, edited.Render!.Data);

		var badProperty = await client.SetPropertyAsync(1, "doesNotExist", "Text", "x", timeout.Token);
		Assert.False(badProperty.Accepted);
		Assert.False(string.IsNullOrEmpty(badProperty.Error));

		var resized = await client.SetBoundsAsync(1, "greeting", 0, 0, 150, 40, timeout.Token);
		Assert.True(resized.Accepted);

		var eventBound = await client.SetEventAsync(1, "greeting", "PointerPressed", "greeting_PointerPressed", timeout.Token);
		Assert.True(eventBound.Accepted);

		var badEvent = await client.SetEventAsync(1, "greeting", "NotARealEvent", "handler", timeout.Token);
		Assert.False(badEvent.Accepted);

		var itemXaml = $"""<Button xmlns="{Ns}" xmlns:x="{XNs}" x:Name="tempButton" Content="Temp"/>""";
		var added = await client.AddElementAsync(1, "root", new DesignerToolboxItemInfo { Template = itemXaml }, "tempButton", 10, 10, timeout.Token);
		Assert.True(added.Accepted);

		var deleted = await client.DeleteElementsAsync(1, new[] { "tempButton" }, timeout.Token);
		Assert.True(deleted.Accepted);

		var deleteMissing = await client.DeleteElementsAsync(1, new[] { "tempButton" }, timeout.Token);
		Assert.False(deleteMissing.Accepted);
		Assert.False(string.IsNullOrEmpty(deleteMissing.Error));

		var renamed = await client.RenameAsync(1, "greeting", "greeting2", timeout.Token);
		Assert.True(renamed.Accepted);

		var staleNameLookup = await client.SetPropertyAsync(1, "greeting", "Text", "y", timeout.Token);
		Assert.False(staleNameLookup.Accepted);

		var newNameLookup = await client.SetPropertyAsync(1, "greeting2", "Text", "y", timeout.Token);
		Assert.True(newNameLookup.Accepted);
	}

	/// <summary>
	/// Tree nodes must report whether they are actually ON SCREEN, because the AddIn positions
	/// overlays (tab-order badges today) from each node's X/Y. A hidden element still reports the
	/// coordinates it WOULD occupy, so an overlay drawn for one lands on top of whatever IS
	/// showing there - and for a tab control, every tab's content occupies the same rect. The
	/// WinForms surface shipped exactly that bug in its always-on outlines and name tags, where it
	/// read as "the designer renders the wrong tab" and cost hours before anyone checked which
	/// LAYER the wrong pixels came from. See DesignerElementNode.IsVisible.
	///
	/// WinUI has no WPF-style UIElement.IsVisible, so the host folds Visibility up the visual tree
	/// itself; a collapsed element stays IN that tree (collapsing is not removal), which is exactly
	/// why its children would otherwise be reported as on screen.
	/// </summary>
	[Fact]
	public async Task Tree_ReportsWhetherEachElementIsActuallyOnScreen()
	{
		var xaml = $"""
			<Grid xmlns="{Ns}" xmlns:x="{XNs}" x:Name="root">
			    <StackPanel x:Name="shownPanel">
			        <TextBlock x:Name="shownChild" Text="visible"/>
			    </StackPanel>
			    <StackPanel x:Name="collapsedPanel" Visibility="Collapsed">
			        <TextBlock x:Name="hiddenChild" Text="hidden"/>
			    </StackPanel>
			</Grid>
			""";

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await StartAsync(timeout.Token);
		var opened = await client.OpenAsync(Document(client, xaml), timeout.Token);
		Assert.True(opened.Accepted);
		Assert.NotNull(opened.Tree);

		Assert.True(FindByName(opened.Tree!, "shownPanel")!.IsVisible);
		Assert.True(FindByName(opened.Tree!, "shownChild")!.IsVisible);

		// The collapsed container...
		Assert.False(FindByName(opened.Tree!, "collapsedPanel")!.IsVisible);
		// ...and its child, whose OWN Visibility is Visible - the case that only an ancestor walk
		// catches, and the one that produced phantom overlays.
		Assert.False(FindByName(opened.Tree!, "hiddenChild")!.IsVisible);
	}

	static DesignerElementNode? FindByName(DesignerElementNode node, string name)
	{
		if (node.Name == name)
			return node;
		foreach (var child in node.Children)
		{
			if (FindByName(child, name) is { } found)
				return found;
		}
		return null;
	}

	[Fact]
	public async Task StaleMutations_AreRejectedAndCannotOverwriteNewerSource()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var client = await StartAsync(timeout.Token);
		Assert.True((await client.OpenAsync(Document(client, Fixture("Hello")), timeout.Token)).Accepted);

		var updated = await client.UpdateAsync(Document(client, Fixture("Newer"), 2), timeout.Token);
		Assert.True(updated.Accepted, updated.Error);
		var staleUpdate = await client.UpdateAsync(Document(client, Fixture("Older"), 1), timeout.Token);
		Assert.False(staleUpdate.Accepted);
		Assert.Contains("Stale base version", staleUpdate.Error, StringComparison.Ordinal);

		// DDP's baseVersion is source authority, not merely a render hint. A delayed edit
		// from version 1 must be rejected after session/update has accepted version 2.
		foreach (var (name, stale) in new (string, DesignerSessionState)[] {
			("set-property", await client.SetPropertyAsync(1, "greeting", "Text", "Clobbered", timeout.Token)),
			("set-bounds", await client.SetBoundsAsync(1, "greeting", 0, 0, 11, 22, timeout.Token)),
			("delete-elements", await client.DeleteElementsAsync(1, new[] { "greeting" }, timeout.Token)),
			("rename", await client.RenameAsync(1, "greeting", "clobbered", timeout.Token)),
		})
		{
			Assert.False(stale.Accepted, $"design/{name} accepted a stale base version");
			Assert.Contains("Stale base version", stale.Error, StringComparison.Ordinal);
		}

		var flushed = await client.FlushAsync(2, timeout.Token);
		var text = Assert.Single(flushed.Files).Text;
		Assert.Contains("Newer", text, StringComparison.Ordinal);
		Assert.DoesNotContain("Clobbered", text, StringComparison.Ordinal);
		Assert.DoesNotContain("clobbered", text, StringComparison.Ordinal);
		Assert.Contains("x:Name=\"greeting\"", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ChildHost_SupportsIndependentLifetimes()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		var first = await StartAsync(timeout.Token);
		var second = await StartAsync(timeout.Token);
		try {
			Assert.NotEqual(first.ProcessId, second.ProcessId);
			Assert.NotEqual(first.SessionId, second.SessionId);

			Assert.True((await first.OpenAsync(Document(first, Fixture("First")), timeout.Token)).Accepted);
			Assert.True((await second.OpenAsync(Document(second, Fixture("Second")), timeout.Token)).Accepted);

			var firstPid = first.ProcessId;
			first.Dispose();
			await WaitForExitAsync(firstPid, timeout.Token);

			Assert.True(second.IsAlive);
			var flushed = await second.FlushAsync(1, timeout.Token);
			Assert.Contains(flushed.Files, item => item.Text.Contains("Second", StringComparison.Ordinal));
		} finally {
			first.Dispose();
			second.Dispose();
		}
	}

	[Fact]
	public async Task SharedHost_UsesOneUnoApplicationAndKeepsDocumentsIsolated()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		var first = await UnoDesignClient.AcquireSharedAsync("", "", timeout.Token, HostDll());
		var second = await UnoDesignClient.AcquireSharedAsync("", "", timeout.Token, HostDll());
		try {
			Assert.Equal(first.ProcessId, second.ProcessId);
			Assert.Equal(first.SessionId, second.SessionId);
			Assert.NotEqual(first.DocumentId, second.DocumentId);
			var firstOpened = await first.OpenAsync(Document(first, Fixture("First")), timeout.Token);
			Assert.True(firstOpened.Accepted);
			Assert.True((await second.OpenAsync(Document(second, Fixture("Second")), timeout.Token)).Accepted);
			var edited = await first.SetPropertyAsync(1, "greeting", "Text", "First edited", timeout.Token);
			Assert.True(edited.Accepted);
			Assert.NotEqual(firstOpened.Render!.Data, edited.Render!.Data);
			Assert.Contains("First", (await first.FlushAsync(1, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
			var sibling = (await second.FlushAsync(1, timeout.Token)).Files.Single().Text;
			Assert.Contains("Second", sibling, StringComparison.Ordinal);
			Assert.DoesNotContain("First edited", sibling, StringComparison.Ordinal);
			first.Dispose();
			Assert.True(second.IsAlive);
			Assert.Contains("Second", (await second.FlushAsync(1, timeout.Token)).Files.Single().Text, StringComparison.Ordinal);
		} finally {
			first.Dispose();
			second.Dispose();
		}
	}

	[Fact]
	public async Task SharedHost_RecoversEveryOpenDocumentAfterTheChildExits()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var first = await UnoDesignClient.AcquireSharedAsync("", "", timeout.Token, HostDll());
		using var second = await UnoDesignClient.AcquireSharedAsync("", "", timeout.Token, HostDll());
		Assert.True((await first.OpenAsync(Document(first, Fixture("First")), timeout.Token)).Accepted);
		Assert.True((await second.OpenAsync(Document(second, Fixture("Second")), timeout.Token)).Accepted);
		var failedProcessId = first.ProcessId;

		first.TerminateHost();
		await WaitUntilAsync(() => first.RecoveryCount > 0 && second.RecoveryCount > 0
			&& first.IsAlive && second.IsAlive && first.ProcessId != failedProcessId, timeout.Token);

		Assert.Equal(first.ProcessId, second.ProcessId);
		Assert.NotEqual(failedProcessId, first.ProcessId);
		Assert.Contains("First", Assert.Single((await first.FlushAsync(1, timeout.Token)).Files).Text, StringComparison.Ordinal);
		var sibling = Assert.Single((await second.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Contains("Second", sibling, StringComparison.Ordinal);
		Assert.DoesNotContain("First", sibling, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SharedHost_RecoveryKeepsTheLastAcceptedSnapshotAfterARejectedUpdate()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var first = await UnoDesignClient.AcquireSharedAsync("", "", timeout.Token, HostDll());
		using var second = await UnoDesignClient.AcquireSharedAsync("", "", timeout.Token, HostDll());
		Assert.True((await first.OpenAsync(Document(first, Fixture("Initial")), timeout.Token)).Accepted);
		Assert.True((await first.UpdateAsync(Document(first, Fixture("Newer"), 2), timeout.Token)).Accepted);
		var stale = await first.UpdateAsync(Document(first, Fixture("Older"), 1), timeout.Token);
		Assert.False(stale.Accepted);
		Assert.True((await second.OpenAsync(Document(second, Fixture("Sibling")), timeout.Token)).Accepted);
		var failedProcessId = first.ProcessId;

		first.TerminateHost();
		await WaitUntilAsync(() => first.RecoveryCount > 0 && second.RecoveryCount > 0
			&& first.IsAlive && second.IsAlive && first.ProcessId != failedProcessId, timeout.Token);

		// session/open establishes a fresh child-side version sequence; the recovered source,
		// rather than the new session's initial version number, is the authority under test.
		var recovered = Assert.Single((await first.FlushAsync(1, timeout.Token)).Files).Text;
		Assert.Contains("Newer", recovered, StringComparison.Ordinal);
		Assert.DoesNotContain("Older", recovered, StringComparison.Ordinal);
		Assert.Contains("Sibling", Assert.Single((await second.FlushAsync(1, timeout.Token)).Files).Text,
			StringComparison.Ordinal);
	}

	static async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
	{
		while (true) {
			try {
				if (System.Diagnostics.Process.GetProcessById(processId).HasExited) return;
			} catch (ArgumentException) { return; }
			await Task.Delay(25, cancellationToken);
		}
	}

	static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
	{
		using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		wait.CancelAfter(TimeSpan.FromSeconds(30));
		while (!condition())
			await Task.Delay(25, wait.Token);
	}
}
