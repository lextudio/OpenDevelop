using ICSharpCode.FormsDesigner.OutOfProcess;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.FormsDesigner.Host.Tests;

public sealed class FormsDesignerHostClientTests
{
	[Fact]
	public async Task ChildHost_HandshakesRejectsStaleVersionsAndFlushesCurrentSnapshot()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
			"../../../../Host/bin/Debug/net10.0-windows/FormsDesigner.Host.dll"));
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		Assert.True(client.IsAlive);
		Assert.NotEqual(Environment.ProcessId, client.ProcessId);

		var first = Snapshot(7, "button1");
		var opened = await client.OpenAsync(first, timeout.Token);
		Assert.True(opened.Accepted);
		Assert.Equal(client.SessionId, opened.SessionId);
		Assert.Equal(client.DocumentId, opened.DocumentId);
		Assert.Equal(7, opened.Version);
		Assert.Equal("System.Windows.Forms.Form", opened.RootType);
		Assert.True(opened.ComponentCount >= 1);
		Assert.NotNull(opened.Render);
		Assert.True(opened.Render.Sequence > 0);
		Assert.True(opened.Render.Dpi >= 1);
		Assert.True(opened.Render.Width > 0 && opened.Render.Height > 0);
		var png = Convert.FromBase64String(opened.Render.PngBase64);
		Assert.True(png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
		Assert.Contains(opened.Components, component => component.Name == "button1"
			&& component.Type == "System.Windows.Forms.Button"
			&& component.Parent == "Form1"
			&& component.Text == "button1"
			&& component.X == 12 && component.Y == 20
			&& component.Width == 90 && component.Height == 30);
		var openedButton = Assert.Single(opened.Components, component => component.Name == "button1");
		Assert.Equal("button1", openedButton.AccessibleName);
		Assert.Equal("Button", openedButton.AccessibleRole);
		Assert.Contains(openedButton.Properties, property => property.Name == "Text" && property.DisplayName == "Text"
			&& property.TypeName == "System.String");
		Assert.Contains(openedButton.Properties, property => property.Name == "Enabled" && property.TypeName == "System.Boolean");
		var hit = await client.HitTestAsync(7, 20, 25, timeout.Token);
		Assert.Equal("button1", hit.ComponentName);
		Assert.Equal("System.Windows.Forms.Button", hit.ComponentType);
		var resizedRoot = await client.SetBoundsAsync(7, "Form1", 0, 0, 420, 260, timeout.Token);
		Assert.Contains(resizedRoot.Components, component => component.Name == "Form1"
			&& component.Width >= 420 && component.Height >= 260);
		Assert.Contains("Size = new System.Drawing.Size(420, 260);",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var scaledRoot = await client.SetPropertyAsync(7, "Form1", "AutoScaleDimensions", "8, 16", timeout.Token);
		Assert.Contains(scaledRoot.Components.Single(component => component.Name == "Form1").Properties,
			property => property.Name == "AutoScaleDimensions" && property.Value == "8, 16");
		Assert.Contains("AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		var edited = await client.SetPropertyAsync(7, "button1", "Text", "edited in child", timeout.Token);
		Assert.True(edited.Accepted);
		Assert.True(edited.Render!.Sequence > opened.Render.Sequence);
		Assert.Contains(edited.Components, component => component.Name == "button1" && component.Text == "edited in child");
		Assert.Contains(edited.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Text" && property.ShouldSerialize);
		var editedFiles = await client.FlushAsync(7, timeout.Token);
		Assert.Contains("edited in child", DesignerText(editedFiles), StringComparison.Ordinal);
		var disabled = await client.SetPropertyAsync(7, "button1", "Enabled", "False", timeout.Token);
		Assert.Contains(disabled.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Enabled" && property.Value.Equals("False", StringComparison.OrdinalIgnoreCase));
		Assert.Contains("button1.Enabled = false;", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var anchored = await client.SetPropertyAsync(7, "button1", "Anchor", "Top, Left", timeout.Token);
		Assert.Contains(anchored.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Anchor" && property.IsEnum);
		Assert.Contains("button1.Anchor = (System.Windows.Forms.AnchorStyles)5;",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var padded = await client.SetPropertyAsync(7, "button1", "Padding", "1, 2, 3, 4", timeout.Token);
		Assert.Contains(padded.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Padding" && property.Value.Contains("1", StringComparison.Ordinal));
		Assert.Contains("button1.Padding = new System.Windows.Forms.Padding(1, 2, 3, 4);",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var resetEnabled = await client.ResetPropertyAsync(7, "button1", "Enabled", timeout.Token);
		Assert.Contains(resetEnabled.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Enabled"
				&& property.Value.Equals("True", StringComparison.OrdinalIgnoreCase)
				&& !property.ShouldSerialize);
		Assert.DoesNotContain("button1.Enabled =", DesignerText(await client.FlushAsync(7, timeout.Token)),
			StringComparison.Ordinal);
		Assert.Contains(openedButton.Events, item => item.Name == "Click" && item.HandlerTypeName == "System.EventHandler");
		var eventBound = await client.SetEventAsync(7, "button1", "Click", "button1_Click", timeout.Token);
		Assert.Contains(eventBound.Components.Single(item => item.Name == "button1").Events,
			item => item.Name == "Click" && item.Handler == "button1_Click");
		var eventFiles = await client.FlushAsync(7, timeout.Token);
		Assert.Contains("button1.Click += button1_Click;", eventFiles.Files.Single(item => item.Kind == "Designer").Text, StringComparison.Ordinal);
		Assert.Contains("private void button1_Click(System.Object sender, System.EventArgs e)", eventFiles.Files.Single(item => item.Kind == "Source").Text, StringComparison.Ordinal);
		var eventCleared = await client.SetEventAsync(7, "button1", "Click", "", timeout.Token);
		Assert.Contains(eventCleared.Components.Single(item => item.Name == "button1").Events,
			item => item.Name == "Click" && item.Handler == "");
		var clearedFiles = await client.FlushAsync(7, timeout.Token);
		Assert.DoesNotContain("button1.Click +=", clearedFiles.Files.Single(item => item.Kind == "Designer").Text, StringComparison.Ordinal);
		Assert.Contains("private void button1_Click", clearedFiles.Files.Single(item => item.Kind == "Source").Text, StringComparison.Ordinal);
		var defaultEvent = await client.ActivateDefaultEventAsync(7, "button1", timeout.Token);
		Assert.Contains(defaultEvent.Components.Single(item => item.Name == "button1").Events,
			item => item.Name == "Click" && item.Handler == "button1_Click");
		Assert.Contains("button1.Click += button1_Click;",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		var added = await client.AddElementAsync(7, "Form1", new DesignerToolboxItemInfo { TypeName = "System.Windows.Forms.Label" }, "label1", 30, 70, timeout.Token);
		Assert.Contains(added.Components, component => component.Name == "label1"
			&& component.Type == "System.Windows.Forms.Label" && component.Parent == "Form1"
			&& component.X == 30 && component.Y == 70);
		var addedFiles = await client.FlushAsync(7, timeout.Token);
		var addedSource = DesignerText(addedFiles);
		Assert.Contains("label1 = new System.Windows.Forms.Label();", addedSource, StringComparison.Ordinal);
		Assert.Contains("Controls.Add(label1);", addedSource, StringComparison.Ordinal);
		Assert.Contains("private System.Windows.Forms.Label label1;", addedSource, StringComparison.Ordinal);
		Assert.Contains("label1.Size = new System.Drawing.Size(", addedSource, StringComparison.Ordinal);
		var labeled = await client.SetPropertyAsync(7, "label1", "Text", "new label", timeout.Token);
		Assert.Contains(labeled.Components, component => component.Name == "label1" && component.Text == "new label");
		Assert.Contains("label1.Text = \"new label\";", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		var moved = await client.SetBoundsAsync(7, "button1", 40, 50, 120, 35, timeout.Token);
		Assert.Contains(moved.Components, component => component.Name == "button1"
			&& component.X == 40 && component.Y == 50 && component.Width == 120 && component.Height == 35);
		var movedSource = DesignerText(await client.FlushAsync(7, timeout.Token));
		Assert.Contains("new System.Drawing.Point(40, 50)", movedSource, StringComparison.Ordinal);
		Assert.Contains("new System.Drawing.Size(120, 35)", movedSource, StringComparison.Ordinal);

		await client.AddElementAsync(7, "Form1", new DesignerToolboxItemInfo { TypeName = "Panel" }, "panel1", 10, 100, timeout.Token);
		var nested = await client.AddElementAsync(7, "panel1", new DesignerToolboxItemInfo { TypeName = "Button" }, "nestedButton", 5, 6, timeout.Token);
		Assert.Contains(nested.Components, component => component.Name == "nestedButton"
			&& component.Parent == "panel1" && component.X == 5 && component.Y == 6
			&& component.SurfaceX == 15 && component.SurfaceY == 106);
		Assert.Contains("panel1.Controls.Add(nestedButton);",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var beforeAdvancedControlRender = nested.Render!.PngBase64;
		var advanced = await client.AddElementAsync(7, "Form1", new DesignerToolboxItemInfo { TypeName = "DataGridView" }, "dataGridView1", 145, 10, timeout.Token);
		Assert.Contains(advanced.Components, component => component.Name == "dataGridView1"
			&& component.Type == "System.Windows.Forms.DataGridView");
		Assert.NotEqual(beforeAdvancedControlRender, advanced.Render!.PngBase64);
		Assert.Contains("System.Windows.Forms.DataGridView dataGridView1", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		await client.AddElementAsync(7, "Form1", new DesignerToolboxItemInfo { TypeName = "Button" }, "renameMe", 5, 5, timeout.Token);
		var renamed = await client.RenameAsync(7, "renameMe", "renamedButton", timeout.Token);
		Assert.Contains(renamed.Components, component => component.Name == "renamedButton");
		Assert.DoesNotContain(renamed.Components, component => component.Name == "renameMe");
		var renamedSource = DesignerText(await client.FlushAsync(7, timeout.Token));
		Assert.Contains("renamedButton = new System.Windows.Forms.Button();", renamedSource, StringComparison.Ordinal);
		Assert.Contains("renamedButton.Name = \"renamedButton\";", renamedSource, StringComparison.Ordinal);
		Assert.DoesNotContain("renameMe", renamedSource, StringComparison.Ordinal);
		var sentBack = await client.SetZOrderAsync(7, "button1", false, timeout.Token);
		Assert.True(sentBack.Accepted);
		Assert.Contains("Controls.SetChildIndex(button1,", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var broughtFront = await client.SetZOrderAsync(7, "button1", true, timeout.Token);
		Assert.True(broughtFront.Accepted);
		Assert.Contains("Controls.SetChildIndex(button1, 0);", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var aligned = await client.ApplyLayoutAsync(7, "align-left", new[] { "button1", "label1" }, 0, 0, timeout.Token);
		Assert.Equal(40, aligned.Components.Single(item => item.Name == "button1").X);
		Assert.Equal(40, aligned.Components.Single(item => item.Name == "label1").X);
		var sameWidth = await client.ApplyLayoutAsync(7, "same-width", new[] { "button1", "label1" }, 0, 0, timeout.Token);
		Assert.Equal(120, sameWidth.Components.Single(item => item.Name == "label1").Width);
		var layoutSource = DesignerText(await client.FlushAsync(7, timeout.Token));
		Assert.Contains("label1.Location = new System.Drawing.Point(40, 70)", layoutSource, StringComparison.Ordinal);
		Assert.Contains("label1.Size = new System.Drawing.Size(120,", layoutSource, StringComparison.Ordinal);
		var groupMoved = await client.ApplyLayoutAsync(7, "move", new[] { "button1", "label1" }, 3, 4, timeout.Token);
		Assert.Contains(groupMoved.Components, item => item.Name == "button1" && item.X == 43 && item.Y == 54);
		Assert.Contains(groupMoved.Components, item => item.Name == "label1" && item.X == 43 && item.Y == 74);
		Assert.Contains("label1.Location = new System.Drawing.Point(43, 74)",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		await client.DeleteElementsAsync(7, new[] { "renamedButton" }, timeout.Token);
		var deleted = await client.DeleteElementsAsync(7, new[] { "label1" }, timeout.Token);
		Assert.DoesNotContain(deleted.Components, component => component.Name == "label1");
		Assert.DoesNotContain("label1", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var stale = await client.UpdateAsync(Snapshot(7, "stale"), timeout.Token);
		Assert.False(stale.Accepted);
		Assert.Contains("Stale", stale.Error, StringComparison.Ordinal);

		var nextSnapshot = Snapshot(8, "button2");
		var nextDesigner = nextSnapshot.Files.Single(item => item.Kind == "Designer");
		nextDesigner.Text = nextDesigner.Text.Replace("this.button1.Size =",
			"this.button1.Padding = new System.Windows.Forms.Padding(4, 3, 2, 1);\n        this.button1.Size =", StringComparison.Ordinal);
		nextDesigner.Text = nextDesigner.Text.Replace("this.button1 = new System.Windows.Forms.Button();",
			"this.button1 = new System.Windows.Forms.Button();\n        this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);\n        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;", StringComparison.Ordinal);
		var current = await client.UpdateAsync(nextSnapshot, timeout.Token);
		Assert.True(current.Accepted);
		Assert.Contains(current.Components, component => component.Name == "button1" && component.Text == "button2");
		Assert.Contains(current.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Padding" && property.Value == "4, 3, 2, 1");
		var loadedScale = Assert.Single(current.Components.Single(component => component.Name == "Form1").Properties,
			property => property.Name == "AutoScaleDimensions");
		Assert.Equal("7, 15", loadedScale.Value);
		var edits = await client.FlushAsync(8, timeout.Token);
		Assert.Equal(client.SessionId, edits.SessionId);
		Assert.Equal(client.DocumentId, edits.DocumentId);
		Assert.Equal(8, edits.BaseVersion);
		Assert.Contains("button2", DesignerText(edits), StringComparison.Ordinal);

		var resourceSnapshot = Snapshot(9, "fallback");
		var resourceDesigner = resourceSnapshot.Files.Single(item => item.Kind == "Designer");
		resourceDesigner.Text = resourceDesigner.Text.Replace(
			"this.button1.Text = \"fallback\";",
			"resources.ApplyResources(this.button1, nameof(button1));\n        this.button1.Image = (System.Drawing.Image)resources.GetObject(\"button1.Image\");",
			StringComparison.Ordinal);
		resourceSnapshot.Files.Add(new DesignerSourceFileSnapshot {
			FileName = "/project/Form1.resx", Kind = "Resource",
			Base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
				$"<root><data name=\"button1.Text\"><value>localized text</value></data>" +
				$"<data name=\"button1.Image\" type=\"System.Drawing.Bitmap, System.Drawing.Common\" mimetype=\"application/x-microsoft.net.object.bytearray.base64\"><value>{Convert.ToBase64String(png)}</value></data></root>"))
		});
		var resourceLoaded = await client.UpdateAsync(resourceSnapshot, timeout.Token);
		Assert.Contains(resourceLoaded.Components, component => component.Name == "button1" && component.Text == "localized text");
		Assert.Contains(resourceLoaded.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Image" && !property.IsNull && property.Value == "[binary]");
		Assert.Contains((await client.FlushAsync(9, timeout.Token)).Files, item => item.Kind == "Resource" && !String.IsNullOrEmpty(item.Base64));

		var fixtureAssembly = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
			"../../../../Host.Tests/CustomControl/bin/Debug/net10.0-windows/FormsDesigner.CustomControlFixture.dll"));
		Assert.True(File.Exists(fixtureAssembly));
		Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "FormsDesigner.CustomControlFixture");
		var customSnapshot = Snapshot(10, "custom");
		customSnapshot.ProjectAssemblyPath = fixtureAssembly;
		var customDesigner = customSnapshot.Files.Single(item => item.Kind == "Designer");
		customDesigner.Text = customDesigner.Text
			.Replace("System.Windows.Forms.Button", "FormsDesigner.CustomControlFixture.FancyButton", StringComparison.Ordinal);
		var custom = await client.UpdateAsync(customSnapshot, timeout.Token);
		Assert.True(custom.Accepted);
		Assert.Contains(custom.Components, component => component.Name == "button1"
			&& component.Type == "FormsDesigner.CustomControlFixture.FancyButton");
		Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "FormsDesigner.CustomControlFixture");

		var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		client.HostExited += (sender, args) => exited.TrySetResult();
		await Assert.ThrowsAsync<TimeoutException>(() => client.DelayAsync(5000, timeout.Token));
		await exited.Task.WaitAsync(timeout.Token);
		Assert.False(client.IsAlive);
	}

	[Fact]
	public async Task ChildHost_BoundsSnapshotsAndSupportsIndependentLifetimes()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
			"../../../../Host/bin/Debug/net10.0-windows/FormsDesigner.Host.dll"));
		var first = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var second = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var firstPid = first.ProcessId;
		try {
			Assert.NotEqual(first.ProcessId, second.ProcessId);
			Assert.NotEqual(first.SessionId, second.SessionId);
			Assert.True((await first.OpenAsync(Snapshot(1, "first"), timeout.Token)).Accepted);
			Assert.True((await second.OpenAsync(Snapshot(1, "second"), timeout.Token)).Accepted);
			var oversized = Snapshot(2, "oversized");
			for (var index = oversized.Files.Count; index <= 256; index++)
				oversized.Files.Add(new DesignerSourceFileSnapshot { FileName = $"/project/{index}.cs", Text = " " });
			await Assert.ThrowsAnyAsync<Exception>(() => first.UpdateAsync(oversized, timeout.Token));
			Assert.True(first.IsAlive);
			first.Dispose();
			await WaitForExitAsync(firstPid, timeout.Token);
			Assert.True(second.IsAlive);
			Assert.Contains((await second.FlushAsync(1, timeout.Token)).Files,
				item => item.Kind == "Designer" && item.Text.Contains("second", StringComparison.Ordinal));
		} finally {
			first.Dispose();
			second.Dispose();
		}
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

	static string DesignerText(DesignerEditSet edits) => edits.Files.Single(item => item.Kind == "Designer").Text;

	[Fact]
	public async Task ChildHost_VbSnapshot_RoundTripsDesignerEdits()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
			"../../../../Host/bin/Debug/net10.0-windows/FormsDesigner.Host.dll"));
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var opened = await client.OpenAsync(VbSnapshot(7, "button1"), timeout.Token);
		Assert.True(opened.Accepted);
		Assert.Equal("System.Windows.Forms.Form", opened.RootType);
		Assert.True(opened.ComponentCount >= 1);
		Assert.NotNull(opened.Render);
		Assert.Contains(opened.Components, component => component.Name == "button1"
			&& component.Type == "System.Windows.Forms.Button"
			&& component.Parent == "Form1"
			&& component.Text == "button1"
			&& component.X == 12 && component.Y == 20
			&& component.Width == 90 && component.Height == 30);
		Assert.Contains(opened.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Text" && property.TypeName == "System.String");
		var openedButton = Assert.Single(opened.Components, component => component.Name == "button1");
		Assert.Contains(openedButton.Events, item => item.Name == "Click" && item.HandlerTypeName == "System.EventHandler");

		// Flush normalizes the VB Me. qualifiers away, like the C# this. pass.
		Assert.Contains("button1.Text = \"button1\"", DesignerText(await client.FlushAsync(7, timeout.Token)),
			StringComparison.Ordinal);

		var edited = await client.SetPropertyAsync(7, "button1", "Text", "edited in child", timeout.Token);
		Assert.True(edited.Accepted);
		Assert.Contains(edited.Components, component => component.Name == "button1" && component.Text == "edited in child");
		var editedFiles = await client.FlushAsync(7, timeout.Token);
		Assert.Contains("button1.Text = \"edited in child\"", DesignerText(editedFiles), StringComparison.Ordinal);
		Assert.DoesNotContain("Me.", DesignerText(editedFiles), StringComparison.Ordinal);
		var disabled = await client.SetPropertyAsync(7, "button1", "Enabled", "False", timeout.Token);
		Assert.Contains(disabled.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Enabled" && property.Value.Equals("False", StringComparison.OrdinalIgnoreCase));
		Assert.Contains("button1.Enabled = False", DesignerText(await client.FlushAsync(7, timeout.Token)),
			StringComparison.Ordinal);

		var moved = await client.SetBoundsAsync(7, "button1", 40, 50, 120, 35, timeout.Token);
		Assert.Contains(moved.Components, component => component.Name == "button1"
			&& component.X == 40 && component.Y == 50 && component.Width == 120 && component.Height == 35);
		var movedSource = DesignerText(await client.FlushAsync(7, timeout.Token));
		Assert.Contains("button1.Location = New System.Drawing.Point(40, 50)", movedSource, StringComparison.Ordinal);
		Assert.Contains("button1.Size = New System.Drawing.Size(120, 35)", movedSource, StringComparison.Ordinal);

		var eventBound = await client.SetEventAsync(7, "button1", "Click", "button1_Click", timeout.Token);
		Assert.Contains(eventBound.Components.Single(item => item.Name == "button1").Events,
			item => item.Name == "Click" && item.Handler == "button1_Click");
		var eventFiles = await client.FlushAsync(7, timeout.Token);
		Assert.Contains("AddHandler button1.Click, AddressOf button1_Click",
			eventFiles.Files.Single(item => item.Kind == "Designer").Text, StringComparison.Ordinal);
		Assert.Contains("Private Sub button1_Click(sender As System.Object, e As System.EventArgs)",
			eventFiles.Files.Single(item => item.Kind == "Source").Text, StringComparison.Ordinal);
		var eventCleared = await client.SetEventAsync(7, "button1", "Click", "", timeout.Token);
		Assert.Contains(eventCleared.Components.Single(item => item.Name == "button1").Events,
			item => item.Name == "Click" && item.Handler == "");
		Assert.DoesNotContain("AddHandler button1.Click",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		var added = await client.AddElementAsync(7, "Form1", new DesignerToolboxItemInfo { TypeName = "System.Windows.Forms.Label" }, "label1", 30, 70, timeout.Token);
		Assert.Contains(added.Components, component => component.Name == "label1"
			&& component.Type == "System.Windows.Forms.Label" && component.Parent == "Form1"
			&& component.X == 30 && component.Y == 70);
		var addedSource = DesignerText(await client.FlushAsync(7, timeout.Token));
		Assert.Contains("label1 = New System.Windows.Forms.Label()", addedSource, StringComparison.Ordinal);
		Assert.Contains("Controls.Add(label1)", addedSource, StringComparison.Ordinal);
		Assert.Contains("Friend WithEvents label1 As System.Windows.Forms.Label", addedSource, StringComparison.Ordinal);
		Assert.Contains("label1.Size = New System.Drawing.Size(", addedSource, StringComparison.Ordinal);
		var labeled = await client.SetPropertyAsync(7, "label1", "Text", "new label", timeout.Token);
		Assert.Contains(labeled.Components, component => component.Name == "label1" && component.Text == "new label");
		Assert.Contains("label1.Text = \"new label\"", DesignerText(await client.FlushAsync(7, timeout.Token)),
			StringComparison.Ordinal);

		var deleted = await client.DeleteElementsAsync(7, new[] { "label1" }, timeout.Token);
		Assert.True(deleted.Accepted);
		Assert.DoesNotContain(deleted.Components, component => component.Name == "label1");
		Assert.DoesNotContain("label1", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		var stale = await client.UpdateAsync(VbSnapshot(7, "stale"), timeout.Token);
		Assert.False(stale.Accepted);
		Assert.Contains("Stale", stale.Error, StringComparison.Ordinal);
	}

	static DesignerDocumentSnapshot VbSnapshot(long version, string text) => new() {
		Version = version,
		PrimaryFileName = "/project/Form1.vb",
		DesignerFileName = "/project/Form1.Designer.vb",
		Language = "VisualBasic",
		Files = {
			new DesignerSourceFileSnapshot {
				FileName = "/project/Form1.vb",
				Kind = "Source",
				Text = "Imports System.Windows.Forms\nPublic Class Form1\n    Inherits Form\nEnd Class"
			},
			new DesignerSourceFileSnapshot {
				FileName = "/project/Form1.Designer.vb",
				Kind = "Designer",
				Text = $$"""
					Imports System.Windows.Forms

					Partial Class Form1
					    Inherits Form

					    Private Sub InitializeComponent()
					        Me.button1 = New System.Windows.Forms.Button()
					        Me.button1.Text = "{{text}}"
					        Me.button1.Location = New System.Drawing.Point(12, 20)
					        Me.button1.Size = New System.Drawing.Size(90, 30)
					        Me.Controls.Add(Me.button1)
					    End Sub

					    Friend WithEvents button1 As System.Windows.Forms.Button
					End Class
					"""
			}
		}
	};

	static DesignerDocumentSnapshot Snapshot(long version, string text) => new() {
		Version = version,
		PrimaryFileName = "/project/Form1.cs",
		DesignerFileName = "/project/Form1.Designer.cs",
		Files = {
			new DesignerSourceFileSnapshot {
				FileName = "/project/Form1.cs",
				Kind = "Source",
				Text = "namespace Sample; partial class Form1 { }"
			},
			new DesignerSourceFileSnapshot {
				FileName = "/project/Form1.Designer.cs",
				Kind = "Designer",
				Text = $$"""
					namespace Sample;
					partial class Form1
					{
					    private void InitializeComponent()
					    {
					        this.button1 = new System.Windows.Forms.Button();
					        this.button1.Text = "{{text}}";
					        this.button1.Location = new System.Drawing.Point(12, 20);
					        this.button1.Size = new System.Drawing.Size(90, 30);
					        this.Controls.Add(this.button1);
					    }
					    private System.Windows.Forms.Button button1;
					}
					"""
			}
		}
	};
}
