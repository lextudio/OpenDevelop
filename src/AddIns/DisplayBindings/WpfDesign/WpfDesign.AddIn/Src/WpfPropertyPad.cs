// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.WpfDesign;

using XceedPropertyGrid = Xceed.Wpf.Toolkit.PropertyGrid.PropertyGrid;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// Properties Pad for the WPF designer: a Xceed <see cref="XceedPropertyGrid"/> bound to the
	/// current design surface's primary selection (via <see cref="DesignItemPropertyGridAdapter"/>),
	/// plus a simple Events section — Xceed's PropertyGrid has no events tab of its own, so event
	/// handler generation is driven separately through <see cref="IEventHandlerService"/>.
	/// </summary>
	public class WpfPropertyPad : AbstractPadContent
	{
		readonly Grid rootGrid = new Grid();
		readonly XceedPropertyGrid propertyGrid = new XceedPropertyGrid();
		readonly ListBox eventsList = new ListBox();

		WpfViewContent activeWpfView;
		DesignItem currentItem;

		public WpfPropertyPad()
		{
			rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
			rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

			propertyGrid.IsCategorized = true;
			propertyGrid.ShowSearchBox = true;
			Grid.SetRow(propertyGrid, 0);

			var eventsHeader = new TextBlock {
				Text = "Events",
				FontWeight = FontWeights.Bold,
				Margin = new Thickness(4, 4, 4, 2)
			};
			Grid.SetRow(eventsHeader, 1);

			eventsList.DisplayMemberPath = null;
			eventsList.ItemTemplate = CreateEventItemTemplate();
			eventsList.MouseDoubleClick += OnEventItemDoubleClick;
			Grid.SetRow(eventsList, 2);

			rootGrid.Children.Add(propertyGrid);
			rootGrid.Children.Add(eventsHeader);
			rootGrid.Children.Add(eventsList);

			SD.Workbench.ActiveViewContentChanged += OnActiveViewContentChanged;
			OnActiveViewContentChanged(null, null);
		}

		static DataTemplate CreateEventItemTemplate()
		{
			var nameText = new FrameworkElementFactory(typeof(TextBlock));
			nameText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DesignItemProperty.Name)));
			nameText.SetValue(FrameworkElement.WidthProperty, 120d);

			var handlerText = new FrameworkElementFactory(typeof(TextBlock));
			handlerText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DesignItemProperty.ValueOnInstance)));

			var panel = new FrameworkElementFactory(typeof(StackPanel));
			panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
			panel.AppendChild(nameText);
			panel.AppendChild(handlerText);

			return new DataTemplate(typeof(DesignItemProperty)) { VisualTree = panel };
		}

		void OnActiveViewContentChanged(object sender, EventArgs e)
		{
			if (activeWpfView != null)
				activeWpfView.DesignContext.Services.Selection.PrimarySelectionChanged -= OnPrimarySelectionChanged;

			activeWpfView = SD.Workbench.ActiveViewContent as WpfViewContent;

			if (activeWpfView != null)
				activeWpfView.DesignContext.Services.Selection.PrimarySelectionChanged += OnPrimarySelectionChanged;

			OnPrimarySelectionChanged(null, null);
		}

		void OnPrimarySelectionChanged(object sender, EventArgs e)
		{
			currentItem = activeWpfView?.DesignContext.Services.Selection.PrimarySelection;

			propertyGrid.SelectedObject = currentItem != null ? new DesignItemPropertyGridAdapter(currentItem) : null;
			eventsList.ItemsSource = currentItem?.Properties.Where(p => p.IsEvent).ToArray();
		}

		void OnEventItemDoubleClick(object sender, MouseButtonEventArgs e)
		{
			if (!(eventsList.SelectedItem is DesignItemProperty eventProperty))
				return;

			var eventHandlerService = activeWpfView?.DesignContext.Services.GetService<IEventHandlerService>();
			eventHandlerService?.CreateEventHandler(eventProperty);
		}

		public override object Control {
			get { return rootGrid; }
		}
	}
}
