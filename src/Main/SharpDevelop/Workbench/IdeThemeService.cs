using System;
using System.Windows;

using AvalonDock;
using AvalonDock.Themes;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.SharpDevelop.Workbench
{
	/// <summary>Stores and applies the user-selected Visual Studio IDE theme.</summary>
	public static class IdeThemeService
	{
		const string PropertyKey = "SharpDevelop.IdeTheme";
		public const string Light = "Light";
		public const string Dark = "Dark";
		public const string Blue = "Blue";

		// Semantic application theme resources (doc/technotes/ilspy.md "Immediate next actions"
		// #5): swapped into Application.Current.Resources.MergedDictionaries alongside
		// DockingManager.Theme below, so both the AvalonDock chrome and the main shell chrome
		// (WindowBackground/ToolWindowBackground/etc. - see Themes/Theme.Light.xaml) change from
		// one IdeThemeService.Apply() call instead of two separate theme authorities.
		static readonly Uri LightSemanticThemeUri = new("/OpenDevelop;component/Themes/Theme.Light.xaml", UriKind.Relative);
		static readonly Uri DarkSemanticThemeUri = new("/OpenDevelop;component/Themes/Theme.Dark.xaml", UriKind.Relative);
		static ResourceDictionary currentSemanticTheme;

		static DockingManager dockingManager;

		public static string CurrentTheme {
			get { return Normalize(SD.PropertyService.Get(PropertyKey, Light)); }
		}

		/// <summary>
		/// Raised after the IDE theme (dock chrome + semantic shell resources) has been applied,
		/// whether from <see cref="Attach"/> (initial) or <see cref="SetTheme"/> (user change) -
		/// with the theme name (<see cref="Light"/>/<see cref="Dark"/>/<see cref="Blue"/>) that was
		/// just applied. Lets an AddIn whose own rendering has a theme concept of its own (e.g.
		/// ILSpy's DecompilerTextView/AvalonEdit syntax colors, driven by ILSpy's own
		/// ICSharpCode.ILSpy.Themes.ThemeManager) stay in sync with the shell's theme choice
		/// instead of maintaining an independent, unsynchronized theme authority - see
		/// doc/technotes/ilspy.md "Full application theming" / "Immediate next actions" #5.
		/// </summary>
		public static event EventHandler<string> ThemeChanged;

		internal static void Attach(DockingManager manager)
		{
			dockingManager = manager;
			RegisterXceedPropertyGridIcons();
			Apply(manager, CurrentTheme);
			ThemeChanged?.Invoke(null, CurrentTheme);
		}

		/// <summary>
		/// Provides the VS2017 icons the Xceed PropertyGrid template references
		/// (<c>{DynamicResource Xceed.Icons.*}</c>) as real <see cref="System.Windows.Media.ImageSource"/>
		/// objects - the template cannot load them itself, because a pack URI string pointing at a
		/// .xaml icon is not an image format the ImageSource converter understands. The values are
		/// theme-independent, so registering once at startup is enough.
		/// </summary>
		static void RegisterXceedPropertyGridIcons()
		{
			var resources = Application.Current.Resources;
			SetIcon(resources, "Xceed.Icons.GroupBy", "Icons.16x16.GroupBy");
			SetIcon(resources, "Xceed.Icons.SortAscending", "Icons.16x16.SortAscending");
			SetIcon(resources, "Xceed.Icons.Settings", "Icons.16x16.Settings");
			SetIcon(resources, "Xceed.Icons.Event", "Icons.16x16.Event");
			SetIcon(resources, "Xceed.Icons.Clear", "Icons.16x16.Clear");
			SetIcon(resources, "Xceed.Icons.Search", "Icons.16x16.Search");
		}

		static void SetIcon(System.Windows.ResourceDictionary resources, string key, string iconKey)
		{
			if (resources.Contains(key))
				return;
			resources[key] = ICSharpCode.Core.Presentation.PresentationResourceService.GetImageSource(iconKey);
		}

		public static void SetTheme(string theme)
		{
			theme = Normalize(theme);
			SD.PropertyService.Set(PropertyKey, theme);
			if (dockingManager != null)
				Apply(dockingManager, theme);
			ThemeChanged?.Invoke(null, theme);
		}

		static string Normalize(string theme)
		{
			if (theme == Dark || theme == Blue)
				return theme;
			return Light;
		}

		static void Apply(DockingManager manager, string theme)
		{
			switch (theme) {
				case Dark:
					manager.Theme = new Vs2013DarkTheme();
					break;
				case Blue:
					manager.Theme = new Vs2013BlueTheme();
					break;
				default:
					manager.Theme = new Vs2013LightTheme();
					break;
			}
			ApplySemanticTheme(theme == Dark ? DarkSemanticThemeUri : LightSemanticThemeUri);
		}

		// "Blue" maps to the Light semantic dictionary for now - the doc only asks for Light/Dark
		// coverage in this first slice; Blue keeps its existing AvalonDock-only look otherwise.
		static void ApplySemanticTheme(Uri themeUri)
		{
			var resources = Application.Current?.Resources;
			if (resources == null)
				return;
			if (currentSemanticTheme != null)
				resources.MergedDictionaries.Remove(currentSemanticTheme);
			currentSemanticTheme = new ResourceDictionary { Source = themeUri };
			resources.MergedDictionaries.Add(currentSemanticTheme);
		}
	}
}
