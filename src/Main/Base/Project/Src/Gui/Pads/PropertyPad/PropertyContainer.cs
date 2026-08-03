using System;
using System.Collections;

using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui
{
	[ViewContentService]
	public interface IHasPropertyContainer
	{
		PropertyContainer PropertyContainer { get; }
	}

	public sealed class PropertyContainer
	{
		public PropertyContainer() : this(true) { }

		internal PropertyContainer(bool createPadOnConstruction)
		{
			if (createPadOnConstruction) {
				// Was SD.Workbench.GetPad(typeof(PropertyPad)).CreatePad() - forced the legacy
				// Pad to materialize. PropertyPadViewModel (its modern replacement,
				// doc/technotes/ilspy.md "Docking and layout replacement") is a `[Shared]` MEF
				// singleton, already constructed by the time DockWorkspace.ToolPanes is first
				// accessed (during workbench startup) - so there's nothing left to force here;
				// just touch the service to keep this constructor's "the pad host exists by now"
				// intent visible.
				_ = SD.Services.GetService(typeof(IPropertyPadHost));
			}
		}

		static IPropertyPadHost Host => SD.Services.GetService(typeof(IPropertyPadHost)) as IPropertyPadHost;

		public bool IsActivePropertyContainer {
			get { return Host?.ActiveContainer == this; }
		}

		object selectedObject;
		object[] selectedObjects;

		public object SelectedObject {
			get { return selectedObject; }
			set {
				selectedObject = value;
				selectedObjects = null;
				Host?.UpdateSelectedObjectIfActive(this);
			}
		}

		public object[] SelectedObjects {
			get { return selectedObjects; }
			set {
				selectedObject = null;
				selectedObjects = value;
				Host?.UpdateSelectedObjectIfActive(this);
			}
		}

		object propertyGridReplacementContent;

		public object PropertyGridReplacementContent {
			get { return propertyGridReplacementContent; }
			set {
				propertyGridReplacementContent = value;
			}
		}

		public void Clear()
		{
			SelectedObject = null;
			PropertyGridReplacementContent = null;
		}
	}
}
