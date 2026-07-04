// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Extracted from the (WinForms-only, excluded) ConfigurationGuiHelper.cs so that ProjectOptionPanel.cs
// (which is not WinForms-specific) can keep using this enum without pulling in the excluded WinForms
// Control-binding helper class.

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Specifies whether the user enters the property value or the MSBuild code for
	/// the property value.
	/// </summary>
	public enum TextBoxEditMode
	{
		/// <summary>
		/// The user edits the MSBuild property value. It is not evaluated on loading,
		/// and not escaped when saving.
		/// The user can use MSBuild properties with $() in the text box.
		/// </summary>
		EditRawProperty,
		/// <summary>
		/// The user edits the property. When loading the value, it is evaluated;
		/// when saving the value, it is escaped.
		/// The user cannot use MSBuild properties with $() because the $ will be escaped.
		/// </summary>
		EditEvaluatedProperty
	}
}
