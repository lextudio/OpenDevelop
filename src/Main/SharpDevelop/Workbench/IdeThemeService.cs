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

		static DockingManager dockingManager;

		public static string CurrentTheme {
			get { return Normalize(SD.PropertyService.Get(PropertyKey, Light)); }
		}

		internal static void Attach(DockingManager manager)
		{
			dockingManager = manager;
			Apply(manager, CurrentTheme);
		}

		public static void SetTheme(string theme)
		{
			theme = Normalize(theme);
			SD.PropertyService.Set(PropertyKey, theme);
			if (dockingManager != null)
				Apply(dockingManager, theme);
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
		}
	}
}
