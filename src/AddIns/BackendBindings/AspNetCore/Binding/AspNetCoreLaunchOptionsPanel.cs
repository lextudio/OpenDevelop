using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.Core;
using ICSharpCode.AspNetCore;
using ICSharpCode.SharpDevelop.Gui.OptionPanels;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.AspNetCore.AddIn
{
	/// <summary>Code-only options panel so this small addin does not add another XAML build surface.</summary>
	public sealed class AspNetCoreLaunchOptionsPanel : ProjectOptionPanel
	{
		readonly ComboBox profile = new() { MinWidth = 260 };
		readonly TextBox applicationUrl = new() { MinWidth = 360 };
		readonly TextBox launchUrl = new() { MinWidth = 360 };
		readonly CheckBox launchBrowser = new() { Content = "Launch browser when the server is ready" };
		readonly TextBlock certificateStatus = new() { Margin = new Thickness(0, 5, 0, 5), TextWrapping = TextWrapping.Wrap };
		readonly Button checkCertificate = new() { Content = "Check HTTPS certificate", MinWidth = 170 };
		readonly Button trustCertificate = new() { Content = "Install or trust certificate", MinWidth = 180, Margin = new Thickness(8, 0, 0, 0) };
		AspNetCoreLaunchProfileProvider provider;

		public AspNetCoreLaunchOptionsPanel()
		{
			HeaderVisibility = Visibility.Collapsed;
			var panel = new StackPanel { Margin = new Thickness(16) };
			panel.Children.Add(Label("Launch profile"));
			panel.Children.Add(profile);
			panel.Children.Add(Label("Application URLs (semicolon separated)"));
			panel.Children.Add(applicationUrl);
			panel.Children.Add(Label("Browser launch URL (absolute or relative)"));
			panel.Children.Add(launchUrl);
			panel.Children.Add(launchBrowser);
			panel.Children.Add(Label("HTTPS development certificate"));
			panel.Children.Add(certificateStatus);
			var certificateButtons = new StackPanel { Orientation = Orientation.Horizontal };
			certificateButtons.Children.Add(checkCertificate);
			certificateButtons.Children.Add(trustCertificate);
			panel.Children.Add(certificateButtons);
			panel.Children.Add(new TextBlock {
				Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap,
				Text = "Environment variables and advanced settings remain editable in Properties/launchSettings.json."
			});
			Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
			profile.SelectionChanged += (_, _) => { LoadSelectedProfile(); IsDirty = true; };
			applicationUrl.TextChanged += (_, _) => IsDirty = true;
			launchUrl.TextChanged += (_, _) => IsDirty = true;
			launchBrowser.Checked += (_, _) => IsDirty = true;
			launchBrowser.Unchecked += (_, _) => IsDirty = true;
			checkCertificate.Click += async (_, _) => await CheckCertificateAsync();
			trustCertificate.Click += async (_, _) => await TrustCertificateAsync();
		}

		static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 3) };

		protected override void Load(MSBuildBasedProject project, string configuration, string platform)
		{
			base.Load(project, configuration, platform);
			var projectFile = project.FileName.ToString();
			var defaultNamespace = project.GetEvaluatedProperty("RootNamespace") ?? project.GetEvaluatedProperty("AssemblyName") ?? Path.GetFileNameWithoutExtension(projectFile);
			provider = new AspNetCoreLaunchProfileProvider(Path.GetDirectoryName(projectFile), defaultNamespace);
			provider.LoadLaunchSettings();
			profile.ItemsSource = provider.Profiles.Select(p => p.Name).ToArray();
			var selected = project.GetEvaluatedProperty("AspNetCoreLaunchProfile");
			profile.SelectedItem = provider.Profiles.Any(p => p.Name == selected) ? selected : provider.GetProfile()?.Name;
			LoadSelectedProfile();
			IsDirty = false;
			certificateStatus.Text = SelectedProfileUsesHttps() ? "Not checked." : "The selected profile does not use HTTPS.";
		}

		bool SelectedProfileUsesHttps() => provider != null && profile.SelectedItem is string name &&
			provider.Profiles.FirstOrDefault(p => p.Name == name)?.ApplicationUrl
				.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
				.Any(url => url.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase)) == true;

		async System.Threading.Tasks.Task CheckCertificateAsync()
		{
			if (!SelectedProfileUsesHttps()) {
				certificateStatus.Text = "The selected profile does not use HTTPS.";
				return;
			}
			await RunCertificateOperationAsync(() => AspNetCoreDevCertificate.CheckAsync());
		}

		async System.Threading.Tasks.Task TrustCertificateAsync()
		{
			if (!SelectedProfileUsesHttps()) {
				certificateStatus.Text = "The selected profile does not use HTTPS.";
				return;
			}
			if (!MessageService.AskQuestion("Run 'dotnet dev-certs https --trust'? The operating system may ask for confirmation."))
				return;
			await RunCertificateOperationAsync(() => AspNetCoreDevCertificate.TrustAsync());
		}

		async System.Threading.Tasks.Task RunCertificateOperationAsync(Func<System.Threading.Tasks.Task<AspNetCoreDevCertificateResult>> operation)
		{
			checkCertificate.IsEnabled = trustCertificate.IsEnabled = false;
			certificateStatus.Text = "Checking…";
			try {
				var result = await operation();
				certificateStatus.Text = result.Message;
				if (result.Status == AspNetCoreDevCertificateStatus.Error)
					MessageService.ShowError(result.Message);
			} catch (Exception ex) {
				certificateStatus.Text = "Certificate operation failed.";
				MessageService.ShowError(ex.Message);
			} finally {
				checkCertificate.IsEnabled = trustCertificate.IsEnabled = true;
			}
		}

		void LoadSelectedProfile()
		{
			if (provider == null || profile.SelectedItem is not string name) return;
			var selected = provider.Profiles.FirstOrDefault(p => p.Name == name);
			if (selected == null) return;
			applicationUrl.Text = selected.ApplicationUrl;
			launchUrl.Text = selected.LaunchUrl;
			launchBrowser.IsChecked = selected.LaunchBrowser;
			certificateStatus.Text = SelectedProfileUsesHttps() ? "Not checked." : "The selected profile does not use HTTPS.";
		}

		protected override bool Save(MSBuildBasedProject project, string configuration, string platform)
		{
			if (provider != null && profile.SelectedItem is string name) {
				provider.UpdateProfile(name, applicationUrl.Text, launchUrl.Text, launchBrowser.IsChecked == true);
				provider.SaveLaunchSettings();
				project.SetProperty("AspNetCoreLaunchProfile", name);
			}
			return base.Save(project, configuration, platform);
		}
	}
}
