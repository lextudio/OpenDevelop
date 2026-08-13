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
using System.CodeDom.Compiler;
using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using System.Linq;
using System.Reflection;
using System.Text;
using ICSharpCode.Core;
using ICSharpCode.FormsDesigner.Services;

namespace ICSharpCode.FormsDesigner
{
	/// <summary>
	/// An abstract base class for CodeDOM designer loaders.
	/// </summary>
	public abstract class AbstractCodeDomDesignerLoader : CodeDomDesignerLoader
	{
		bool loading;
		IDesignerLoaderHost designerLoaderHost = null;
		ITypeResolutionService typeResolutionService = null;
		
		public override bool Loading {
			get { return base.Loading || loading; }
		}
		
		protected override ITypeResolutionService TypeResolutionService {
			get { return this.typeResolutionService; }
		}
		
		protected IDesignerLoaderHost DesignerLoaderHost {
			get { return this.designerLoaderHost; }
		}
		
		public override void Dispose()
		{
			try {
				IComponentChangeService componentChangeService = (IComponentChangeService)this.GetService(typeof(IComponentChangeService));
				if (componentChangeService != null) {
					LoggingService.Debug("Forms designer: Removing ComponentAdded handler for nested container setup");
					componentChangeService.ComponentAdded -= ComponentContainerSetUp;
				} else {
					LoggingService.Info("Forms designer: Could not remove ComponentAdding handler because IComponentChangeService is no longer available");
				}
			} finally {
				base.Dispose();
			}
		}
		
		public override void BeginLoad(IDesignerLoaderHost host)
		{
			this.loading = true;
			this.typeResolutionService = (ITypeResolutionService)host.GetService(typeof(ITypeResolutionService));
			this.designerLoaderHost = host;
			
			base.BeginLoad(host);
		}
		
		static void ComponentContainerSetUp(object sender, ComponentEventArgs e)
		{
			// Real WinForms designers get System.Windows.Forms.Control.AllowDrop set to true by
			// ParentControlDesigner.Initialize() (via TypeDescriptor.CreateDesigner, using the
			// [Designer("System.Windows.Forms.Design.ParentControlDesigner, System.Design")]
			// attribute on Control/Panel/etc.) - that's what lets a real designer accept toolbox
			// drops at all (WindowsFormsHost.ProcessExternalDragEvent walks up from the drop point
			// looking for the first AllowDrop=true control). System.Design.dll (which contains the
			// real ParentControlDesigner) isn't available in this portable environment - confirmed
			// via TypeResolutionService's own FileNotFoundException for "System.Design" at load
			// time - so no per-control IDesigner ever gets created and AllowDrop is never set,
			// silently breaking every WinForms designer's drag-and-drop regardless of what's being
			// dragged. Set it directly here instead of depending on a real ParentControlDesigner.
			if (e.Component is System.Windows.Forms.Control control) {
				control.AllowDrop = true;

				// Likewise, a real ParentControlDesigner.OnDragDrop is what actually creates a new
				// component from the dropped System.Drawing.Design.ToolboxItem and adds it to the
				// container - Control.OnDragDrop's own default implementation does nothing (that
				// behavior is layered on by the designer, not the control). Without the real
				// ParentControlDesigner (see the AllowDrop comment above for why), replicate just
				// that part of it here so a toolbox drop actually produces a component.
				control.DragEnter -= OnDesignerControlDragEnter;
				control.DragEnter += OnDesignerControlDragEnter;
				control.DragDrop -= OnDesignerControlDragDrop;
				control.DragDrop += OnDesignerControlDragDrop;
			}

			// HACK: This reflection mess fixes SD2-1374 and SD2-1375. However I am not sure why it is needed in the first place.
			// There seems to be a problem with the nested container class used
			// by the designer. It only establishes a connection to the service
			// provider of the DesignerHost after it has been queried for
			// an IServiceContainer service. This does not always happen
			// automatically, so we enforce that here. We have to use
			// reflection because the request for IServiceContainer is
			// not forwarded by higher-level GetService methods.
			// Also, be very careful when trying to troubleshoot this using
			// the debugger because it automatically gets all properties and
			// this can cause side effects here, such as initializing that service
			// so that the problem no longer appears.
			INestedContainer nestedContainer = e.Component.Site.GetService(typeof(INestedContainer)) as INestedContainer;
			if (nestedContainer != null) {
				MethodInfo getServiceMethod = nestedContainer.GetType().GetMethod("GetService", BindingFlags.Instance | BindingFlags.NonPublic, null, new [] {typeof(Type)}, null);
				if (getServiceMethod != null) {
					LoggingService.Debug("Forms designer: Initializing nested service container of " + e.Component.ToString() + " using Reflection");
					getServiceMethod.Invoke(nestedContainer, BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.NonPublic, null, new [] {typeof(IServiceContainer)}, null);
				}
			}
		}

		static void OnDesignerControlDragEnter(object sender, System.Windows.Forms.DragEventArgs e)
		{
			if (e.Data.GetDataPresent(typeof(System.Drawing.Design.ToolboxItem))) {
				e.Effect = System.Windows.Forms.DragDropEffects.Copy;
			}
		}

		// Turns a toolbox drop into a real component via the designer's own toolbox entry point
		// (System.Drawing.Design.IToolboxUser.ToolPicked) rather than creating the component here:
		// LibreWinForms' portable designer already implements parenting, transactions, and selection
		// behind that interface. Creating the component directly instead -
		// toolboxItem.CreateComponents(host) + Controls.Add - skips those behaviors.
		//
		// ToolPicked creates the component inside the nearest container designer of the CURRENT
		// selection, so the drop target is communicated by selecting it first.
		static void OnDesignerControlDragDrop(object sender, System.Windows.Forms.DragEventArgs e)
		{
			if (!(sender is System.Windows.Forms.Control targetControl))
				return;

			if (!(e.Data.GetData(typeof(System.Drawing.Design.ToolboxItem)) is System.Drawing.Design.ToolboxItem toolboxItem))
				return;

			IDesignerHost host = targetControl.Site?.GetService(typeof(IDesignerHost)) as IDesignerHost;
			if (host == null)
				return;

			var selectionService = host.GetService(typeof(ISelectionService)) as ISelectionService;
			selectionService?.SetSelectedComponents(new object[] { targetControl }, SelectionTypes.Replace);

			if (!(host.GetDesigner(host.RootComponent) is System.Drawing.Design.IToolboxUser toolboxUser)
			    || !toolboxUser.GetToolSupported(toolboxItem))
				return;

			var componentsBeforeDrop = new System.Collections.Generic.HashSet<System.ComponentModel.IComponent>(
				host.Container.Components.Cast<System.ComponentModel.IComponent>());

			toolboxUser.ToolPicked(toolboxItem);

			// ToolPicked has no drop-point overload (the underlying CreateTool(tool, start, end) is
			// internal to LibreWinForms), so it centers the new control in its container - move it
			// to where the user actually dropped it. Going through the PropertyDescriptor rather
			// than the CLR property keeps the designer's change notification/serialization in sync.
			System.Drawing.Point dropLocation = targetControl.PointToClient(new System.Drawing.Point(e.X, e.Y));
			foreach (System.ComponentModel.IComponent component in host.Container.Components) {
				if (componentsBeforeDrop.Contains(component) || !(component is System.Windows.Forms.Control addedControl))
					continue;

				ApplyToolboxDefaults(addedControl);

				var locationProperty = TypeDescriptor.GetProperties(component)["Location"];
				if (locationProperty != null && !locationProperty.IsReadOnly)
					locationProperty.SetValue(component, dropLocation);
			}
		}

		// LibreWinForms intentionally keeps these compatibility controls at Size.Empty. Apply the
		// familiar WinForms toolbox sizes only when OpenDevelop creates a new designer component;
		// loaded source and explicit rubber-band sizes remain authoritative.
		static void ApplyToolboxDefaults(System.Windows.Forms.Control control)
		{
			if (control.Size.IsEmpty) {
				System.Drawing.Size size;
				if (control is System.Windows.Forms.ComboBox)
					size = new System.Drawing.Size(121, 21);
				else if (control is System.Windows.Forms.ListBox)
					size = new System.Drawing.Size(120, 96);
				else if (control is System.Windows.Forms.Panel || control is System.Windows.Forms.GroupBox)
					size = new System.Drawing.Size(200, 100);
				else if (control is System.Windows.Forms.Label)
					size = new System.Drawing.Size(100, 23);
				else if (control is System.Windows.Forms.TextBox)
					size = new System.Drawing.Size(100, 20);
				else if (control is System.Windows.Forms.NumericUpDown)
					size = new System.Drawing.Size(120, 20);
				else
					size = System.Drawing.Size.Empty;

				if (!size.IsEmpty) {
					var sizeProperty = TypeDescriptor.GetProperties(control)["Size"];
					if (sizeProperty != null && !sizeProperty.IsReadOnly)
						sizeProperty.SetValue(control, size);
				}
			}

			if (string.IsNullOrEmpty(control.Text)
			    && control is System.Windows.Forms.ButtonBase or System.Windows.Forms.Label or System.Windows.Forms.GroupBox) {
				var textProperty = TypeDescriptor.GetProperties(control)["Text"];
				if (textProperty != null && !textProperty.IsReadOnly)
					textProperty.SetValue(control, control.Name);
			}
		}
		
		protected override void Initialize()
		{
			CodeDomLocalizationModel model = FormsDesigner.Gui.OptionPanels.LocalizationModelOptionsPanel.DefaultLocalizationModel;
			
			if (FormsDesigner.Gui.OptionPanels.LocalizationModelOptionsPanel.KeepLocalizationModel) {
				// Try to find out the current localization model of the designed form
				CodeDomLocalizationModel existingModel = this.GetCurrentLocalizationModelFromDesignedFile();
				if (existingModel != CodeDomLocalizationModel.None) {
					LoggingService.Debug("Determined existing localization model, using that: " + existingModel.ToString());
					model = existingModel;
				} else {
					LoggingService.Debug("Could not determine existing localization model, using default: " + model.ToString());
				}
			} else {
				LoggingService.Debug("Using default localization model: " + model.ToString());
			}
			
			CodeDomLocalizationProvider localizationProvider = new CodeDomLocalizationProvider(designerLoaderHost, model);
			IDesignerSerializationManager manager = (IDesignerSerializationManager)designerLoaderHost.GetService(typeof(IDesignerSerializationManager));
			manager.AddSerializationProvider(new SharpDevelopSerializationProvider());
			manager.AddSerializationProvider(localizationProvider);
			base.Initialize();
			
			IComponentChangeService componentChangeService = (IComponentChangeService)this.GetService(typeof(IComponentChangeService));
			if (componentChangeService != null) {
				LoggingService.Debug("Forms designer: Adding ComponentAdded handler for nested container setup");
				componentChangeService.ComponentAdded += ComponentContainerSetUp;
			} else {
				LoggingService.Warn("Forms designer: Cannot add ComponentAdded handler for nested container setup because IComponentChangeService is unavailable");
			}
		}
		
		/// <summary>
		/// When overridden in derived classes, this method should return the current
		/// localization model of the designed file or None, if it cannot be determined.
		/// </summary>
		/// <returns>The default implementation always returns None.</returns>
		protected virtual CodeDomLocalizationModel GetCurrentLocalizationModelFromDesignedFile()
		{
			return CodeDomLocalizationModel.None;
		}
		
		protected override void OnEndLoad(bool successful, ICollection errors)
		{
			this.loading = false;
			//when control's Dispose() has a exception and on loading also raised exception
			//then this is only place where this error can be logged, because after errors is
			//catched internally in .net
			try {
				base.OnEndLoad(successful, errors);
			} catch(ExceptionCollection e) {
				LoggingService.Error("DesignerLoader.OnEndLoad error " + e.Message, e);
				foreach(Exception ine in e.Exceptions) {
					LoggingService.Error("DesignerLoader.OnEndLoad error " + ine.Message, ine);
				}
				throw;
			} catch(Exception e) {
				LoggingService.Error("DesignerLoader.OnEndLoad error " + e.Message, e);
				throw;
			}
		}
		
		protected override void ReportFlushErrors(ICollection errors)
		{
			StringBuilder sb = new StringBuilder(StringParser.Parse("${res:ICSharpCode.SharpDevelop.FormDesigner.ReportFlushErrors}") + Environment.NewLine + Environment.NewLine);
			foreach (var error in errors) {
				sb.AppendLine(error.ToString());
				sb.AppendLine();
			}
			MessageService.ShowError(sb.ToString());
		}
	}
}
