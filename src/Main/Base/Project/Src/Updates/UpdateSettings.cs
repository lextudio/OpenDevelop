// Update-check settings, modeled on ILSpy's UpdateSettings
// (externals/ilspy/ILSpy/Updates/UpdateSettings.cs). ILSpy's version is an ISettingsSection
// (ICSharpCode.ILSpyX + TomsToolbox) that the hosting app persists; OpenDevelop's core
// ICSharpCode.Core.PropertyService plays that role here, so this file deliberately does NOT link
// the ILSpy original - only its field semantics (AutomaticUpdateCheckEnabled defaulting to true,
// LastSuccessfulUpdateCheck) are kept identical.
using System;

using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Updates
{
	public sealed class UpdateSettings
	{
		const string AutomaticUpdateCheckEnabledKey = "Updates.AutomaticUpdateCheckEnabled";
		const string LastSuccessfulUpdateCheckKey = "Updates.LastSuccessfulUpdateCheck";

		public bool AutomaticUpdateCheckEnabled {
			get { return PropertyService.Get(AutomaticUpdateCheckEnabledKey, true); }
			set { PropertyService.Set(AutomaticUpdateCheckEnabledKey, value); }
		}

		public DateTime? LastSuccessfulUpdateCheck {
			get {
				string stored = PropertyService.Get<string>(LastSuccessfulUpdateCheckKey, (string)null);
				if (string.IsNullOrEmpty(stored))
					return null;
				if (DateTime.TryParse(stored, System.Globalization.CultureInfo.InvariantCulture,
					System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
					return parsed;
				return null;
			}
			set { PropertyService.Set(LastSuccessfulUpdateCheckKey, value?.ToString("o", System.Globalization.CultureInfo.InvariantCulture)); }
		}
	}
}
