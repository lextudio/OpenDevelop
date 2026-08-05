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
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Debugging;

namespace Debugger.AddIn.Tooltips
{
	/// <summary>
	/// Control displaying and executing <see cref="IVisualizerCommand">visualizer commands</see>.
	/// </summary>
	public class VisualizerPicker : ComboBox
	{
		public VisualizerPicker()
		{
			// LibreWPF's implicit-style lookup only queries the exact element type and does
			// not walk BaseType like WPF does (see FrameworkElement.FindImplicitStyleResource),
			// so ComboBox subclasses never pick up the implicit ComboBox style from the
			// semantic theme dictionary (Themes/Theme.Light.xaml / Theme.Dark.xaml) and fall
			// back to the Aero2 chrome with its hardcoded light body. Resolve the style by its
			// type key instead and pin it once the element is realized; the DynamicResource
			// token colors inside the style keep following theme switches, so this is one-time.
			Loaded += (_, _) => {
				if (Style == null && Application.Current != null)
					Style = Application.Current.TryFindResource(typeof(ComboBox)) as Style;
			};
			this.SelectionChanged += VisualizerPicker_SelectionChanged;
		}

		void VisualizerPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (this.SelectedItem == null) {
				return;
			}
			var clickedCommand = this.SelectedItem as IVisualizerCommand;
			if (clickedCommand == null) {
				throw new InvalidOperationException(
					string.Format("{0} clicked, only instances of {1} must be present in {2}.",
								  this.SelectedItem.GetType().ToString(), 
								  typeof(IVisualizerCommand).Name,
								  typeof(VisualizerPicker).Name));
			}
			clickedCommand.Execute();
			// Make no item selected, so that multiple selections of the same item always execute the command.
			// This triggers VisualizerPicker_SelectionChanged again, which returns immediately.
			this.SelectedIndex = -1;
		}

		public new IEnumerable<IVisualizerCommand> ItemsSource
		{
			get { return (IEnumerable<IVisualizerCommand>)base.ItemsSource; }
			set { base.ItemsSource = value; }
		}
	}
}
