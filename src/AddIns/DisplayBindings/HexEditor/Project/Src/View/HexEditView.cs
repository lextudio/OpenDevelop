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
using System.IO;
using System.Windows.Input;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;

namespace HexEditor.View
{
	/// <summary>
	/// IClipboardHandler/IUndoHandler are WinForms-only bridging interfaces (see their doc
	/// comments) - as a WPF addin this instead wires the standard ApplicationCommands via
	/// CommandBindings, the same pattern ResourceEditorViewModel.View uses.
	/// </summary>
	public class HexEditView : AbstractViewContent
	{
		HexEditContainer hexEditContainer;

		public HexEditView(OpenedFile file)
		{
			hexEditContainer = new HexEditContainer();
			hexEditContainer.hexEditControl.DocumentChanged += DocumentChanged;
			hexEditContainer.hexEditControl.Selection.SelectionChanged += SelectionChanged;

			hexEditContainer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut,
				(s, e) => { if (hexEditContainer.HasSomethingSelected) SD.Clipboard.SetText(hexEditContainer.Cut()); },
				(s, e) => e.CanExecute = hexEditContainer.HasSomethingSelected));
			hexEditContainer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
				(s, e) => { if (hexEditContainer.HasSomethingSelected) SD.Clipboard.SetText(hexEditContainer.Copy()); },
				(s, e) => e.CanExecute = hexEditContainer.HasSomethingSelected));
			hexEditContainer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste,
				(s, e) => hexEditContainer.Paste(SD.Clipboard.GetText()),
				(s, e) => e.CanExecute = true));
			hexEditContainer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete,
				(s, e) => { if (hexEditContainer.HasSomethingSelected) hexEditContainer.Delete(); },
				(s, e) => e.CanExecute = hexEditContainer.HasSomethingSelected));
			hexEditContainer.CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll,
				(s, e) => hexEditContainer.SelectAll(),
				(s, e) => e.CanExecute = true));
			hexEditContainer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo,
				(s, e) => { if (hexEditContainer.CanUndo) hexEditContainer.Undo(); },
				(s, e) => e.CanExecute = hexEditContainer.CanUndo));
			hexEditContainer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo,
				(s, e) => { if (hexEditContainer.CanRedo) hexEditContainer.Redo(); },
				(s, e) => e.CanExecute = hexEditContainer.CanRedo));

			this.Files.Add(file);

			file.ForceInitializeView(this);

			SD.AnalyticsMonitor.TrackFeature(typeof(HexEditView));
		}

		public override object Control {
			get { return hexEditContainer; }
		}

		public override void Save(OpenedFile file, Stream stream)
		{
			SD.AnalyticsMonitor.TrackFeature(typeof(HexEditView), "Save");
			this.hexEditContainer.SaveFile(file, stream);
			this.TitleName = Path.GetFileName(file.FileName);
			this.TabPageText = this.TitleName;
		}

		public override void Load(OpenedFile file, Stream stream)
		{
			SD.AnalyticsMonitor.TrackFeature(typeof(HexEditView), "Load");
			this.hexEditContainer.LoadFile(file, stream);
		}

		public override bool IsReadOnly {
			get { return false; }
		}

		void DocumentChanged(object sender, EventArgs e)
		{
			if (PrimaryFile != null) PrimaryFile.MakeDirty();
			CommandManager.InvalidateRequerySuggested();
		}

		void SelectionChanged(object sender, System.EventArgs e)
		{
			CommandManager.InvalidateRequerySuggested();
		}
	}
}
