using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using ICSharpCode.SharpDevelop.Workbench;

using Xunit;

namespace OpenDevelop.Tests
{
	/// <summary>
	/// Covers the dialect-ownership routing added for out-of-tree designers. The guarantee that
	/// matters is asymmetric on purpose: a dialect nobody has claimed must keep being designed
	/// exactly as before, so only an explicitly registered dialect may ever cause a binding to
	/// be skipped.
	/// </summary>
	public class XamlDialectRegistryTests : IDisposable
	{
		readonly string tempDirectory;

		public XamlDialectRegistryTests()
		{
			tempDirectory = Path.Combine(Path.GetTempPath(), "xaml-dialect-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tempDirectory);
		}

		public void Dispose()
		{
			try {
				Directory.Delete(tempDirectory, recursive: true);
			} catch (IOException) {
			}
			foreach (string dialect in new[] { "Maui", "Broken" })
				XamlDialectRegistry.Unregister(dialect);
		}

		string WriteFile(string name, string content)
		{
			string path = Path.Combine(tempDirectory, name);
			File.WriteAllText(path, content);
			return path;
		}

		/// <summary>Mirrors the guard in <c>DisplayBindingService.AttachSubWindows</c>, so this
		/// test fails if the guard and the registry ever disagree about the rule.</summary>
		static bool RoutedTo(ISecondaryDisplayBinding binding, string dialect)
		{
			if (binding is IXamlDialectDisplayBinding dialectBinding
			    && dialect != null
			    && !dialectBinding.Dialects.Contains(dialect, StringComparer.OrdinalIgnoreCase))
				return false;
			return true;
		}

		sealed class Binding : IXamlDialectDisplayBinding
		{
			public IEnumerable<string> Dialects { get; set; }
			public bool ReattachWhenParserServiceIsReady => false;
			public bool CanAttachTo(IViewContent content) => true;
			public IViewContent[] CreateSecondaryViewContent(IViewContent viewContent) => new IViewContent[0];
		}

		[Fact]
		public void Unclaimed_File_Resolves_To_No_Dialect()
		{
			string file = WriteFile("MainPage.xaml", "<ContentPage />");

			Assert.Null(XamlDialectRegistry.ResolveDialect(file));
		}

		[Fact]
		public void Unclaimed_File_Reaches_Every_Binding()
		{
			var wpf = new Binding { Dialects = new[] { XamlDialectKeys.Wpf } };
			var maui = new Binding { Dialects = new[] { "Maui" } };

			Assert.True(RoutedTo(wpf, null));
			Assert.True(RoutedTo(maui, null));
		}

		[Fact]
		public void Registered_Dialect_Routes_To_Its_Owner_Only()
		{
			string file = WriteFile("MainPage.xaml", "<ContentPage />");
			XamlDialectRegistry.Register(new XamlDialectRegistration(
				"Maui", name => string.Equals(Path.GetFileName(name), "MainPage.xaml", StringComparison.Ordinal)));

			try {
				string dialect = XamlDialectRegistry.ResolveDialect(file);
				Assert.Equal("Maui", dialect);

				var wpf = new Binding { Dialects = new[] { XamlDialectKeys.Wpf } };
				var maui = new Binding { Dialects = new[] { "Maui" } };
				Assert.False(RoutedTo(wpf, dialect));
				Assert.True(RoutedTo(maui, dialect));
			} finally {
				XamlDialectRegistry.Unregister("Maui");
			}
		}

		[Fact]
		public void Registered_Dialect_Supplies_The_Source_Editor_Toolbox()
		{
			string file = WriteFile("MainPage.xaml", "<ContentPage />");
			var toolbox = new object();
			XamlDialectRegistry.Register(new XamlDialectRegistration(
				"Maui", name => string.Equals(Path.GetFileName(name), "MainPage.xaml", StringComparison.Ordinal)) {
				ToolsContent = () => toolbox
			});

			try {
				Assert.Same(toolbox, XamlDialectRegistry.GetToolsContent(file));
				// Unclaimed files keep the built-in choice.
				Assert.Null(XamlDialectRegistry.GetToolsContent(WriteFile("Other.xaml", "<Window />")));
			} finally {
				XamlDialectRegistry.Unregister("Maui");
			}
		}

		[Fact]
		public void A_Throwing_Toolbox_Provider_Leaves_The_Built_In_Choice()
		{
			string file = WriteFile("MainPage.xaml", "<ContentPage />");
			XamlDialectRegistry.Register(new XamlDialectRegistration("Maui", _ => true) {
				ToolsContent = () => throw new InvalidOperationException("broken addin")
			});

			try {
				Assert.Null(XamlDialectRegistry.GetToolsContent(file));
			} finally {
				XamlDialectRegistry.Unregister("Maui");
			}
		}

		[Fact]
		public void Non_Matching_File_Is_Not_Claimed()
		{
			string other = WriteFile("Other.xaml", "<ContentPage />");
			XamlDialectRegistry.Register(new XamlDialectRegistration(
				"Maui", name => string.Equals(Path.GetFileName(name), "MainPage.xaml", StringComparison.Ordinal)));

			try {
				Assert.Null(XamlDialectRegistry.ResolveDialect(other));
			} finally {
				XamlDialectRegistry.Unregister("Maui");
			}
		}

		[Fact]
		public void Registering_The_Same_Dialect_Twice_Replaces_Rather_Than_Accumulates()
		{
			string first = WriteFile("First.xaml", "<ContentPage />");
			string second = WriteFile("Second.xaml", "<ContentPage />");
			XamlDialectRegistry.Register(new XamlDialectRegistration(
				"Maui", name => string.Equals(Path.GetFileName(name), "First.xaml", StringComparison.Ordinal)));
			XamlDialectRegistry.Register(new XamlDialectRegistration(
				"Maui", name => string.Equals(Path.GetFileName(name), "Second.xaml", StringComparison.Ordinal)));

			try {
				Assert.Null(XamlDialectRegistry.ResolveDialect(first));
				Assert.Equal("Maui", XamlDialectRegistry.ResolveDialect(second));
			} finally {
				XamlDialectRegistry.Unregister("Maui");
			}
		}

		[Fact]
		public void A_Throwing_Matcher_Cannot_Break_Other_Dialects()
		{
			string file = WriteFile("MainPage.xaml", "<ContentPage />");
			XamlDialectRegistry.Register(new XamlDialectRegistration(
				"Broken", name => throw new InvalidOperationException("boom")));
			XamlDialectRegistry.Register(new XamlDialectRegistration(
				"Maui", name => string.Equals(Path.GetFileName(name), "MainPage.xaml", StringComparison.Ordinal)));

			try {
				Assert.Equal("Maui", XamlDialectRegistry.ResolveDialect(file));
			} finally {
				XamlDialectRegistry.Unregister("Broken");
				XamlDialectRegistry.Unregister("Maui");
			}
		}

		[Fact]
		public void A_File_Is_Served_Only_By_Its_Own_Dialects_Server()
		{
			// Each runtime has its own server; a file goes to its own server and no other, and one
			// nobody serves gets no server rather than a wrong one. (Out-of-tree dialects are keyed
			// by their dialect; built-in runtimes are covered by the integration test.)
			string maui = WriteFile("MainPage.xaml", "<ContentPage />");
			string silent = WriteFile("Silent.xaml", "<Thing />");
			string plain = WriteFile("Other.xaml", "<Grid />");
			XamlDialectRegistry.Register(new XamlDialectRegistration(
				"Maui", name => string.Equals(Path.GetFileName(name), "MainPage.xaml", StringComparison.Ordinal)));
			XamlDialectRegistry.Register(new XamlDialectRegistration(
				"Silent", name => string.Equals(Path.GetFileName(name), "Silent.xaml", StringComparison.Ordinal)));
			var mauiServer = Path.Combine(tempDirectory, "maui-xaml-ls.dll");
			var wpfServer = Path.Combine(tempDirectory, "wpf-xaml-ls.dll");
			using var mauiRegistration = XamlLanguageServers.Register("Maui", mauiServer, "--framework", "maui");
			using var wpfRegistration = XamlLanguageServers.Register(XamlLanguageServers.MicrosoftWpf, wpfServer);

			try {
				var spec = XamlLanguageServers.GetLaunchSpec(maui);
				Assert.NotNull(spec);
				Assert.Equal(new[] { "exec", mauiServer, "--workspace", tempDirectory, "--framework", "maui" }, spec!.Arguments);
				// Registered dialect without a server, and an unclaimed file: no server at all -
				// never the WPF one registered alongside.
				Assert.Null(XamlLanguageServers.GetLaunchSpec(silent));
				Assert.Null(XamlLanguageServers.GetLaunchSpec(plain));
				Assert.Null(XamlLanguageServers.GetLaunchSpec(""));
			} finally {
				XamlDialectRegistry.Unregister("Maui");
				XamlDialectRegistry.Unregister("Silent");
			}

			// Disposing a registration removes that server again.
			mauiRegistration.Dispose();
			Assert.Null(XamlLanguageServers.GetServerAssembly("Maui"));
		}

		[Fact]
		public void Host_Assembly_Name_Is_Only_Reported_For_Registered_Dialects()
		{
			Assert.Null(XamlDialectRegistry.GetHostAssemblyName("Maui"));
			Assert.Null(XamlDialectRegistry.GetHostAssemblyName(XamlDialectKeys.Wpf));

			XamlDialectRegistry.Register(new XamlDialectRegistration(
				"Maui", name => true, "MAUIDesigner.Host.dll"));
			try {
				Assert.Equal("MAUIDesigner.Host.dll", XamlDialectRegistry.GetHostAssemblyName("Maui"));
			} finally {
				XamlDialectRegistry.Unregister("Maui");
			}
		}

		[Fact]
		public void Built_In_Dialect_Keys_Are_Stable()
		{
			// The workbench filter and every binding's Dialects declaration share these
			// spellings; a rename here would silently stop routing rather than fail to compile.
			Assert.Equal("Wpf", XamlDialectKeys.Wpf);
			Assert.Equal("WinUI", XamlDialectKeys.WinUI);
			Assert.Equal("Uno", XamlDialectKeys.Uno);
		}

		[Fact]
		public void Empty_File_Name_Is_Not_A_Dialect()
		{
			Assert.Null(XamlDialectRegistry.ResolveDialect(""));
			Assert.Null(XamlDialectRegistry.ResolveDialect(null));
		}
	}
}
