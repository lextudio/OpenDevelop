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
				PadDescriptor desc = SD.Workbench.GetPad(typeof(PropertyPad));
				if (desc != null) desc.CreatePad();
			}
		}

		public bool IsActivePropertyContainer {
			get { return PropertyPad.ActiveContainer == this; }
		}

		object selectedObject;
		object[] selectedObjects;

		public object SelectedObject {
			get { return selectedObject; }
			set {
				selectedObject = value;
				selectedObjects = null;
				PropertyPad.UpdateSelectedObjectIfActive(this);
			}
		}

		public object[] SelectedObjects {
			get { return selectedObjects; }
			set {
				selectedObject = null;
				selectedObjects = value;
				PropertyPad.UpdateSelectedObjectIfActive(this);
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
