using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ICSharpCode.SharpDevelop.Gui
{
	public partial class AboutDialog : Window
	{
		readonly string[] quotes =
		{
			"\"The most successful method of programming is to begin a program as simply as possible, test it, and then add to the program until it performs the required job.\"\n    -- PDP8 handbook, Pg 9-64",
			"\"The primary purpose of the DATA statement is to give names to constants; instead of referring to pi as 3.141592653589793 at every appearance, the variable PI can be given that value with a DATA statement and used instead of the longer form of the constant. This also simplifies modifying the program, should the value of pi change.\"\n    -- FORTRAN manual for Xerox computers",
			"\"No proper program contains an indication which as an operator-applied occurrence identifies an operator-defining occurrence which as an indication-applied occurrence identifies an indication-defining occurrence different from the one identified by the given indication as an indication-applied occurrence.\"\n   -- ALGOL 68 Report",
			"\"The '#pragma' command is specified in the ANSI standard to have an arbitrary implementation-defined effect. In the GNU C preprocessor, `#pragma' first attempts to run the game rogue; if that fails, it tries to run the game hack; if that fails, it tries to run GNU Emacs displaying the Tower of Hanoi; if that fails, it reports a fatal error. In any case, preprocessing does not continue.\"\n   --From an old GNU C Preprocessor document",
			"\"There are two ways of constructing a software design: one way is to make it so simple that there are obviously no deficiencies; the other is to make it so complicated that there are no obvious deficiencies.\"\n    -- C.A.R. Hoare",
			"\"On two occasions, I have been asked [by members of Parliament], 'Pray, Mr. Babbage, if you put into the machine wrong figures, will the right answers come out?' I am not able to rightly apprehend the kind of confusion of ideas that could provoke such a question.\"\n   -- Charles Babbage (1791-1871)"
		};

		readonly Random rng = new Random();
		int quoteIndex;
		double scrollY;
		DispatcherTimer timer;

		public AboutDialog()
		{
			InitializeComponent();

			var assembly = typeof(AboutDialog).Assembly;
			var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location);

			var major = RevisionClass.Major;
			var minor = RevisionClass.Minor;
			var build = RevisionClass.Build;
			var revision = RevisionClass.Revision;

			VersionText.Text = $"Version {major}.{minor}.{build} (rev {revision})";

			var copyrightAttr = (AssemblyCopyrightAttribute)assembly
				.GetCustomAttributes(typeof(AssemblyCopyrightAttribute), false)
				.FirstOrDefault();
			CopyrightText.Text = "Copyright " + (copyrightAttr?.Copyright ?? "");

			VersionInfoBox.Text = BuildVersionInformation(assembly, fileVersion);
			AssemblyList.ItemsSource = LoadAssemblyList();

			// Shuffle quotes
			for (int i = 0; i < quotes.Length; i++)
			{
				int j = rng.Next(i, quotes.Length);
				var tmp = quotes[i];
				quotes[i] = quotes[j];
				quotes[j] = tmp;
			}

			Loaded += OnLoaded;
			Unloaded += OnUnloaded;
		}

		void OnLoaded(object sender, RoutedEventArgs e)
		{
			scrollY = -quoteCanvas.ActualHeight;
			StartScrolling();
		}

		void OnUnloaded(object sender, RoutedEventArgs e)
		{
			timer?.Stop();
		}

		void StartScrolling()
		{
			timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
			timer.Tick += OnTimerTick;
			timer.Start();
		}

		void OnTimerTick(object sender, EventArgs e)
		{
			if (quoteCanvas.ActualWidth < 10 || quoteCanvas.ActualHeight < 10)
				return;

			scrollY += 1.0;
			double canvasH = quoteCanvas.ActualHeight;

			if (scrollY > canvasH)
			{
				scrollY = -canvasH;
				quoteIndex = (quoteIndex + 1) % quotes.Length;
			}

			quoteCanvas.Children.Clear();

			var tb = new TextBlock
			{
				Text = quotes[quoteIndex],
				TextWrapping = TextWrapping.Wrap,
				FontSize = 11,
				FontFamily = new FontFamily("Segoe UI"),
				Foreground = Brushes.Black,
				Width = quoteCanvas.ActualWidth - 10
			};

			Canvas.SetLeft(tb, 5);
			Canvas.SetTop(tb, scrollY);
			quoteCanvas.Children.Add(tb);
		}

		static string BuildVersionInformation(Assembly assembly, FileVersionInfo fileVersion)
		{
			var sb = new StringBuilder();

			var infoVersion = assembly
				.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
				.OfType<AssemblyInformationalVersionAttribute>()
				.FirstOrDefault();
			if (infoVersion is not null)
				sb.AppendLine($"OpenDevelop Version : {infoVersion.InformationalVersion}");

			sb.AppendLine($".NET Version         : {RuntimeInformation.FrameworkDescription}");
			sb.AppendLine($"OS Version           : {Environment.OSVersion}");
			sb.AppendLine($"Current culture      : {CultureInfo.CurrentCulture.EnglishName} ({CultureInfo.CurrentCulture.Name})");
			sb.AppendLine($"Running as           : {(IntPtr.Size * 8)}-bit process");
			sb.AppendLine($"Working Set Memory   : {Environment.WorkingSet / 1024} kb");
			sb.AppendLine($"GC Heap Memory       : {GC.GetTotalMemory(false) / 1024} kb");

			return sb.ToString();
		}

		static List<AssemblyInfo> LoadAssemblyList()
		{
			return AppDomain.CurrentDomain.GetAssemblies()
				.Select(asm =>
				{
					var name = asm.GetName();
					string location;
					try { location = asm.Location; }
					catch (NotSupportedException) { location = "dynamic"; }
					return new AssemblyInfo
					{
						Name = name.Name,
						Version = name.Version?.ToString() ?? "",
						Location = location
					};
				})
				.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		void OnOkClick(object sender, RoutedEventArgs e)
		{
			DialogResult = true;
		}

		void OnCopyClick(object sender, RoutedEventArgs e)
		{
			var sb = new StringBuilder();
			foreach (var asm in (System.Collections.IEnumerable)AssemblyList.ItemsSource)
			{
				if (asm is AssemblyInfo ai)
					sb.AppendLine($"{ai.Name},{ai.Version},{ai.Location}");
			}
			Clipboard.SetText(sb.ToString());
		}

		class AssemblyInfo
		{
			public string Name { get; set; }
			public string Version { get; set; }
			public string Location { get; set; }
		}
	}
}
