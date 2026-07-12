using System;
using System.Windows.Controls;

using ICSharpCode.SharpDevelop.Workbench;

using XceedPropertyGrid = Xceed.Wpf.Toolkit.PropertyGrid.PropertyGrid;

namespace ICSharpCode.SharpDevelop.Gui
{
	public class PropertyPad : AbstractPadContent
	{
		static PropertyPad instance;

		readonly PropertyContainer emptyContainer = new PropertyContainer(false);
		readonly ContentPresenter contentPresenter = new ContentPresenter();
		readonly Grid propertyGridContainer = new Grid();
		readonly XceedPropertyGrid propertyGrid = new XceedPropertyGrid();

		PropertyContainer activeContainer;
		object currentReplacementContent;

		internal static PropertyContainer ActiveContainer {
			get { return instance?.activeContainer; }
		}

		void SetActiveContainer(PropertyContainer pc)
		{
			if (activeContainer == pc)
				return;
			if (pc == null)
				pc = emptyContainer;
			activeContainer = pc;

			UpdateSelectedObjectIfActive(pc);
			UpdateReplacementContent(pc);
		}

		internal static void UpdateSelectedObjectIfActive(PropertyContainer container)
		{
			if (instance == null) return;
			if (instance.activeContainer != container)
				return;
			if (container.SelectedObjects != null)
				instance.propertyGrid.SelectedObject = container.SelectedObjects;
			else
				instance.propertyGrid.SelectedObject = container.SelectedObject;
		}

		static void UpdateReplacementContent(PropertyContainer container)
		{
			if (instance == null) return;
			if (instance.activeContainer != container)
				return;
			var replacement = container.PropertyGridReplacementContent;
			if (instance.currentReplacementContent != replacement)
			{
				instance.currentReplacementContent = replacement;
				if (replacement != null)
					instance.contentPresenter.Content = replacement;
				else
					instance.contentPresenter.Content = instance.propertyGridContainer;
			}
		}

		public static XceedPropertyGrid Grid {
			get { return instance?.propertyGrid; }
		}

		public override object Control {
			get { return contentPresenter; }
		}

		IHasPropertyContainer previousContent;

		void WorkbenchActiveContentChanged(object sender, EventArgs e)
		{
			var activeViewOrPad = SD.Workbench.ActiveContent;
			IHasPropertyContainer c = (activeViewOrPad as IServiceProvider)?.GetService<IHasPropertyContainer>();
			if (c == null) {
				if (previousContent == null) {
					c = SD.GetActiveViewContentService<IHasPropertyContainer>();
				} else {
					if (previousContent is IViewContent && previousContent != SD.Workbench.ActiveViewContent) {
						c = null;
					} else {
						c = previousContent;
					}
				}
			}
			if (c != null) {
				SetActiveContainer(c.PropertyContainer);
			} else {
				SetActiveContainer(null);
			}
			previousContent = c;
		}

		public PropertyPad()
		{
			instance = this;

			propertyGrid.IsCategorized = true;
			propertyGrid.ShowSearchBox = true;
			propertyGridContainer.Children.Add(propertyGrid);

			contentPresenter.Content = propertyGridContainer;

			SD.Workbench.ActiveContentChanged += WorkbenchActiveContentChanged;
			SD.Workbench.ActiveViewContentChanged += WorkbenchActiveContentChanged;
			WorkbenchActiveContentChanged(null, null);
		}

		public override void Dispose()
		{
			base.Dispose();
			SD.Workbench.ActiveContentChanged -= WorkbenchActiveContentChanged;
			SD.Workbench.ActiveViewContentChanged -= WorkbenchActiveContentChanged;
			instance = null;
		}
	}
}
