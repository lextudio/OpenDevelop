using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AspNetCore;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.AspNetCore.AddIn
{
	public sealed class AspNetCorePublishToFolderCommand : AbstractMenuCommand
	{
		public override async void Run()
		{
			var project = ProjectService.CurrentProject;
			if (project == null) return;
			var profile = PublishProfileDialog.Select(project.Directory);
			if (profile == null) return;
			try {
				if (profile.SaveChanges) profile.Profile.Save();
				ICSharpCode.SharpDevelop.Commands.SaveAllFiles.SaveAll();
				var command = AspNetCorePublishCommand.Create(project.FileName.ToString(), profile.Profile);
				using var runner = new ProcessRunner { WorkingDirectory = DirectoryName.Create(command.WorkingDirectory) };
				var exitCode = await runner.RunInOutputPadAsync(TaskService.BuildMessageViewCategory, command.FileName, command.ArgumentList.ToArray());
				if (exitCode == 0) {
					var output = AspNetCorePublishCommand.GetOutputDirectory(project.FileName.ToString(), profile.Profile);
					Directory.CreateDirectory(output);
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(output) { UseShellExecute = true });
				} else {
					MessageService.ShowError("dotnet publish exited with code " + exitCode + ". See Build output for details.");
				}
			} catch (Exception ex) {
				MessageService.ShowError("ASP.NET Core publish failed: " + ex.Message);
			}
		}
	}

	sealed class PublishProfileDialog : Window
	{
		readonly string projectDirectory;
		readonly ComboBox profiles = new() { MinWidth = 390 };
		readonly TextBox output = new() { MinWidth = 350 };
		readonly TextBox configuration = new() { Width = 120, Text = "Release", HorizontalAlignment = HorizontalAlignment.Left };
		readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
		readonly CheckBox saveChanges = new() { Content = "Save changes to the selected .pubxml profile" };
		readonly AspNetCorePublishProfile[] savedProfiles;

		PublishProfileDialog(string projectDirectory)
		{
			this.projectDirectory = projectDirectory;
			savedProfiles = AspNetCorePublishCommand.LoadProfiles(projectDirectory).ToArray();
			Title = "Publish ASP.NET Core to Folder";
			Owner = SD.Workbench.MainWindow;
			WindowStartupLocation = WindowStartupLocation.CenterOwner;
			ResizeMode = ResizeMode.NoResize;
			SizeToContent = SizeToContent.WidthAndHeight;
			ShowInTaskbar = false;
			var grid = new Grid { Margin = new Thickness(16), MinWidth = 560 };
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			for (var i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			Add(grid, new TextBlock { Text = "Publish profile", Margin = LabelMargin() }, 0, 0);
			Add(grid, profiles, 0, 1, 2);
			Add(grid, new TextBlock { Text = "Output folder", Margin = LabelMargin() }, 1, 0);
			Add(grid, output, 1, 1);
			var browse = new Button { Content = "Browse...", Margin = new Thickness(8, 4, 0, 4), MinWidth = 80 };
			Add(grid, browse, 1, 2);
			Add(grid, new TextBlock { Text = "Configuration", Margin = LabelMargin() }, 2, 0);
			Add(grid, configuration, 2, 1);
			details.Margin = new Thickness(0, 10, 0, 6); Add(grid, details, 3, 0, 3);
			saveChanges.Margin = new Thickness(0, 0, 0, 12); Add(grid, saveChanges, 4, 0, 3);
			var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
			var publish = new Button { Content = "Publish", IsDefault = true, MinWidth = 85, Margin = new Thickness(8, 0, 0, 0) };
			var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 85 };
			buttons.Children.Add(cancel); buttons.Children.Add(publish); Add(grid, buttons, 5, 0, 3);
			Content = grid;
			profiles.Items.Add("Custom folder");
			foreach (var profile in savedProfiles) profiles.Items.Add(profile.Name);
			profiles.SelectionChanged += (_, _) => LoadSelection();
			browse.Click += (_, _) => Browse();
			publish.Click += (_, _) => { if (string.IsNullOrWhiteSpace(output.Text)) { MessageService.ShowError("An output folder is required."); return; } DialogResult = true; };
			profiles.SelectedIndex = savedProfiles.Length > 0 ? 1 : 0;
		}

		static Thickness LabelMargin() => new(0, 8, 12, 4);

		static void Add(Grid grid, UIElement element, int row, int column, int columnSpan = 1)
		{
			Grid.SetRow(element, row); Grid.SetColumn(element, column); Grid.SetColumnSpan(element, columnSpan); grid.Children.Add(element);
		}

		void LoadSelection()
		{
			var selected = profiles.SelectedIndex > 0 ? savedProfiles[profiles.SelectedIndex - 1] : null;
			output.Text = selected?.PublishDirectory ?? Path.Combine(projectDirectory, "bin", "Release", "publish");
			configuration.Text = string.IsNullOrWhiteSpace(selected?.Configuration) ? "Release" : selected.Configuration;
			saveChanges.IsEnabled = selected != null;
			if (selected == null) saveChanges.IsChecked = false;
			details.Text = selected == null ? "One-time folder publish; no profile file will be created."
				: $"Target: {(string.IsNullOrEmpty(selected.TargetFramework) ? "project default" : selected.TargetFramework)}; Runtime: {(string.IsNullOrEmpty(selected.RuntimeIdentifier) ? "portable" : selected.RuntimeIdentifier)}; Self-contained: {selected.SelfContained}";
		}

		void Browse()
		{
			var selected = SD.FileService.BrowseForFolder("Choose publish output folder", Path.GetFullPath(output.Text, projectDirectory));
			if (!string.IsNullOrEmpty(selected)) { profiles.SelectedIndex = 0; output.Text = selected; }
		}

		PublishSelection CreateResult()
		{
			var selected = profiles.SelectedIndex > 0 ? savedProfiles[profiles.SelectedIndex - 1] : null;
			var profile = new AspNetCorePublishProfile { FilePath = selected?.FilePath ?? string.Empty, Name = selected?.Name ?? "Folder", PublishDirectory = output.Text, Configuration = configuration.Text,
				TargetFramework = selected?.TargetFramework ?? string.Empty, RuntimeIdentifier = selected?.RuntimeIdentifier ?? string.Empty,
				SelfContained = selected?.SelfContained == true, DeleteExistingFiles = selected?.DeleteExistingFiles == true };
			return new PublishSelection(profile, saveChanges.IsChecked == true && selected != null);
		}

		public static PublishSelection Select(string projectDirectory)
		{
			var dialog = new PublishProfileDialog(projectDirectory);
			return dialog.ShowDialog() == true ? dialog.CreateResult() : null;
		}
	}

	sealed record PublishSelection(AspNetCorePublishProfile Profile, bool SaveChanges);
}
