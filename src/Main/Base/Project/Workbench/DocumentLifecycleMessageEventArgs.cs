using System;
using System.Collections.Generic;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Workbench
{
	/// <summary>Completed document lifecycle facts owned by <c>FileService</c>.</summary>
	public abstract class DocumentLifecycleMessageEventArgs : EventArgs
	{
		protected DocumentLifecycleMessageEventArgs(OpenedFile document, long revision)
		{
			Document = document ?? throw new ArgumentNullException(nameof(document));
			Revision = revision;
		}

		public OpenedFile Document { get; }
		public long Revision { get; }
	}

	public sealed class DocumentOpenedMessageEventArgs : DocumentLifecycleMessageEventArgs
	{
		public DocumentOpenedMessageEventArgs(OpenedFile document, IViewContent initialView, long revision) : base(document, revision) => InitialView = initialView;
		public IViewContent InitialView { get; }
	}

	public sealed class DocumentClosedMessageEventArgs : DocumentLifecycleMessageEventArgs
	{
		public DocumentClosedMessageEventArgs(OpenedFile document, FileName fileName, long revision) : base(document, revision) => FileName = fileName;
		public FileName FileName { get; }
	}

	public sealed class DocumentRenamedMessageEventArgs : DocumentLifecycleMessageEventArgs
	{
		public DocumentRenamedMessageEventArgs(OpenedFile document, FileName oldFileName, FileName newFileName, long revision) : base(document, revision)
		{
			OldFileName = oldFileName;
			NewFileName = newFileName;
		}

		public FileName OldFileName { get; }
		public FileName NewFileName { get; }
	}

	public sealed class DocumentViewsChangedMessageEventArgs : DocumentLifecycleMessageEventArgs
	{
		public DocumentViewsChangedMessageEventArgs(OpenedFile document, IReadOnlyList<IViewContent> views, long revision) : base(document, revision)
		{
			Views = views ?? throw new ArgumentNullException(nameof(views));
		}

		public IReadOnlyList<IViewContent> Views { get; }
	}
}
