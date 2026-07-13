using System;
using System.IO;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.IconEditor
{
	public class IconViewContent : AbstractViewContent
	{
		EditorPanel editor = new EditorPanel();

		public override object Control
		{
			get { return editor; }
		}

		public IconViewContent(OpenedFile file) : base(file)
		{
			editor.IconWasEdited += editor_IconWasEdited;
		}

		void editor_IconWasEdited(object sender, EventArgs e)
		{
			PrimaryFile.MakeDirty();
		}

		public override void Load(OpenedFile file, Stream stream)
		{
			try
			{
				editor.ShowFile(new IconFile(stream));
			}
			catch (InvalidIconException ex)
			{
				SD.MainThread.InvokeAsyncAndForget(delegate
				{
					MessageService.ShowHandledException(ex);
					if (WorkbenchWindow != null)
					{
						WorkbenchWindow.CloseWindow(true);
					}
				});
			}
		}

		public override void Save(OpenedFile file, Stream stream)
		{
			editor.SaveIcon(stream);
		}
	}
}
