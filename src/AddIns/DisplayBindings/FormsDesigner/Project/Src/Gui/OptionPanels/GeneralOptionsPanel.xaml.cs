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
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.FormsDesigner.Gui.OptionPanels
{
	/// <summary>
	/// Interaction logic for GeneralOptionsPanelXaml.xaml
	/// </summary>
	public partial class GeneralOptionsPanel : OptionPanel
	{
		public GeneralOptionsPanel()
		{
			InitializeComponent();
		}
		
		public static bool UseSmartTags {
			get {
				return PropertyService.Get("FormsDesigner.DesignerOptions.UseSmartTags", true);
			}
			set {
				PropertyService.Set("FormsDesigner.DesignerOptions.UseSmartTags", value);
			}
		}
		
		public static bool SmartTagAutoShow {
			get {
				return PropertyService.Get("FormsDesigner.DesignerOptions.ObjectBoundSmartTagAutoShow", true);
			}
			set {
				PropertyService.Set("FormsDesigner.DesignerOptions.ObjectBoundSmartTagAutoShow", value);
			}
		}
		
		public static bool InsertTodoComment {
			get {
				return PropertyService.Get("FormsDesigner.DesignerOptions.InsertTodoComment", false);
			}
			set {
				PropertyService.Set("FormsDesigner.DesignerOptions.InsertTodoComment", value);
			}
		}
		
		public static bool GenerateVisualStudioStyleEventHandlers {
			get {
				return PropertyService.Get("FormsDesigner.DesignerOptions.GenerateVisualStudioStyleEventHandlers", false);
			}
			set {
				PropertyService.Set("FormsDesigner.DesignerOptions.GenerateVisualStudioStyleEventHandlers", value);
			}
		}

		/// <summary>How long the IDE waits for the out-of-process WinForms designer host to
		/// answer session/open or session/update before treating it as hung and restarting it
		/// (<see cref="ICSharpCode.SharpDevelop.Designer.Remote.DesignerHostProcessClient"/>'s
		/// default is a fixed 30s). Large real-world forms with hundreds of components can
		/// legitimately need longer than that, so this is user-configurable rather than a
		/// silent hard-coded ceiling.</summary>
		public static int LoadingTimeoutSeconds {
			get {
				return PropertyService.Get("FormsDesigner.DesignerOptions.LoadingTimeoutSeconds", 30);
			}
			set {
				PropertyService.Set("FormsDesigner.DesignerOptions.LoadingTimeoutSeconds", value);
			}
		}


		public override bool SaveOptions()
		{

			PropertyService.Set("FormsDesigner.DesignerOptions.PropertyGridSortAlphabetical", sortAlphabeticalCheckBox.IsChecked);
			PropertyService.Set("FormsDesigner.DesignerOptions.UseOptimizedCodeGeneration", optimizedCodeGenerationCheckBox.IsChecked);
			SmartTagAutoShow = (bool)this.smartTagAutoShowCheckBox.IsChecked;
			PropertyService.Set("FormsDesigner.DesignerOptions.EnableInSituEditing", inPlaceEditCheckBox.IsChecked);
			UseSmartTags = (bool)useSmartTagsCheckBox.IsChecked;
			InsertTodoComment = (bool)insertTodoCommentCheckBox.IsChecked;
			GenerateVisualStudioStyleEventHandlers = (bool)generateVSStyleHandlersCheckBox.IsChecked;
			if (int.TryParse(loadingTimeoutTextBox.Text, out var timeoutSeconds) && timeoutSeconds > 0)
				LoadingTimeoutSeconds = timeoutSeconds;

			return true;
		}


		public override void LoadOptions()
		{
			base.LoadOptions();
			Initialize();
		}

		private void Initialize()
		{
			this.sortAlphabeticalCheckBox.IsChecked =  PropertyService.Get("FormsDesigner.DesignerOptions.PropertyGridSortAlphabetical", false);
			this.optimizedCodeGenerationCheckBox.IsChecked = PropertyService.Get("FormsDesigner.DesignerOptions.UseOptimizedCodeGeneration", true);
			this.smartTagAutoShowCheckBox.IsChecked        = SmartTagAutoShow;
			this.inPlaceEditCheckBox.IsChecked             = PropertyService.Get("FormsDesigner.DesignerOptions.EnableInSituEditing", true);
			this.useSmartTagsCheckBox.IsChecked            = UseSmartTags;
			this.insertTodoCommentCheckBox.IsChecked       = InsertTodoComment;
			this.generateVSStyleHandlersCheckBox.IsChecked = GenerateVisualStudioStyleEventHandlers;
			this.loadingTimeoutTextBox.Text                = LoadingTimeoutSeconds.ToString();
		}
	}
}
