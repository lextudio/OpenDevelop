// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// 17.14-compatible replacement for externals/vs-editor-api's
// src/Editor/Text/Def/TextData/Document/TextDocumentFileActionEventArgs.cs.
//
// The open-source snapshot predates the 4-argument rename constructor and the OldFilePath
// property that Microsoft.VisualStudio.Text.Data 17.14 exposes. OpenDevelop.VSEditorCompat
// raises the rename event with both paths, so the source copy of this one type is excluded
// from Microsoft.VisualStudio.Text.Data.csproj and this file supplies the newer shape. The
// sources are MIT; this file carries the same terms.

using System;

namespace Microsoft.VisualStudio.Text
{
	/// <summary>
	/// Describes the type of file action.
	/// </summary>
	[Flags]
	public enum FileActionTypes
	{
		/// <summary>
		/// The content was saved to disk.
		/// </summary>
		ContentSavedToDisk = 1,

		/// <summary>
		/// The content was loaded from disk.
		/// </summary>
		ContentLoadedFromDisk = 2,

		/// <summary>
		/// The document was renamed.
		/// </summary>
		DocumentRenamed = 4
	}

	/// <summary>
	/// Provides information for events that are raised when an <see cref="ITextDocument"/> has
	/// loaded from or saved to disk.
	/// </summary>
	public class TextDocumentFileActionEventArgs : EventArgs
	{
		/// <summary>
		/// Initializes a new instance of a <see cref="TextDocumentFileActionEventArgs"/> for a
		/// file action event.
		/// </summary>
		/// <param name="filePath">The path to the file.</param>
		/// <param name="time">The <see cref="DateTime"/> when the file action occurred.</param>
		/// <param name="fileActionType">The <see cref="FileActionTypes"/> that occurred.</param>
		/// <exception cref="ArgumentNullException"><paramref name="filePath"/> is null.</exception>
		public TextDocumentFileActionEventArgs(string filePath, DateTime time, FileActionTypes fileActionType)
			: this(filePath, filePath, time, fileActionType)
		{
		}

		/// <summary>
		/// Initializes a new instance of a <see cref="TextDocumentFileActionEventArgs"/> for a
		/// rename file action event.
		/// </summary>
		/// <param name="oldFilePath">The path to the file before the rename.</param>
		/// <param name="filePath">The path to the file after the rename.</param>
		/// <param name="time">The <see cref="DateTime"/> when the file action occurred.</param>
		/// <param name="fileActionType">The <see cref="FileActionTypes"/> that occurred.</param>
		/// <exception cref="ArgumentNullException">
		/// <paramref name="oldFilePath"/> or <paramref name="filePath"/> is null.
		/// </exception>
		public TextDocumentFileActionEventArgs(string oldFilePath, string filePath, DateTime time, FileActionTypes fileActionType)
		{
			if (oldFilePath == null)
			{
				throw new ArgumentNullException(nameof(oldFilePath));
			}
			if (filePath == null)
			{
				throw new ArgumentNullException(nameof(filePath));
			}

			OldFilePath = oldFilePath;
			FilePath = filePath;
			Time = time;
			FileActionType = fileActionType;
		}

		/// <summary>
		/// Gets the path to the file before the rename, or the same value as
		/// <see cref="FilePath"/> when the action is not a rename.
		/// </summary>
		public string OldFilePath { get; }

		/// <summary>
		/// Gets the path to the file.
		/// </summary>
		public string FilePath { get; }

		/// <summary>
		/// Gets the <see cref="DateTime"/> when the file action occurred.
		/// </summary>
		public DateTime Time { get; }

		/// <summary>
		/// Gets the <see cref="FileActionTypes"/> that occurred.
		/// </summary>
		public FileActionTypes FileActionType { get; }
	}
}
