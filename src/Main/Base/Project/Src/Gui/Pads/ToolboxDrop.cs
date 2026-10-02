// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// What a Toolbox drag carries, and how a document's source view asks the document's designer where
// a dropped control goes. A document shown as Design and Source side by side has ONE toolbox: the
// designer's. Its items have to land correctly whichever half they are dropped on, and only the
// designer knows its framework's markup (a GtkBuilder <child><object class=... id=.../></child> is
// not a XAML <Button />), so the source view hands it the text and applies the edit it plans.

using System;
using System.Windows;

namespace ICSharpCode.SharpDevelop.Gui
{
	/// <summary>The drag-data contract shared by every Toolbox and every drop target.</summary>
	public static class ToolboxDragData
	{
		/// <summary>The dragged control's type name, as the owning designer spells it
		/// ("GtkSwitch", "StackPanel", "Windows.UI.Xaml.Controls.Button", ...).</summary>
		public const string TypeNameFormat = "OpenDevelop.ToolboxTypeName";

		/// <summary>The older format the XAML source editor and the WPF designer read.</summary>
		public const string ComponentTypeNameFormat = "ComponentTypeName";

		/// <summary>Fills <paramref name="data"/> with every format a toolbox drop target reads.</summary>
		public static void Pack(DataObject data, string typeName)
		{
			if (data == null)
				throw new ArgumentNullException(nameof(data));
			data.SetData(TypeNameFormat, typeName);
			data.SetData(ComponentTypeNameFormat, typeName);
		}

		/// <summary>The dragged control's type name, or null when the drag is not a toolbox drag.</summary>
		public static string GetTypeName(IDataObject data)
		{
			if (data == null)
				return null;
			if (data.GetDataPresent(TypeNameFormat))
				return data.GetData(TypeNameFormat) as string;
			if (data.GetDataPresent(ComponentTypeNameFormat))
				return data.GetData(ComponentTypeNameFormat) as string;
			return null;
		}
	}

	/// <summary>A text edit planned for a toolbox drop onto a source view.</summary>
	public sealed class ToolboxSourceEdit
	{
		public ToolboxSourceEdit(int offset, int removalLength, string text, int caretOffset)
		{
			Offset = offset;
			RemovalLength = removalLength;
			Text = text ?? throw new ArgumentNullException(nameof(text));
			CaretOffset = caretOffset;
		}

		/// <summary>Where the edit starts in the source text it was planned against.</summary>
		public int Offset { get; }
		public int RemovalLength { get; }
		public string Text { get; }
		/// <summary>Where the caret goes once the edit is applied.</summary>
		public int CaretOffset { get; }

		/// <summary>The smallest edit that turns <paramref name="before"/> into
		/// <paramref name="after"/>, with the caret at the end of the inserted text - or null when they
		/// are equal. A designer that rewrites its whole document still only replaces the part that changed,
		/// so the source view keeps its scroll position, folding and undo granularity.</summary>
		public static ToolboxSourceEdit FromDifference(string before, string after)
		{
			before ??= "";
			after ??= "";
			var prefix = 0;
			var max = Math.Min(before.Length, after.Length);
			while (prefix < max && before[prefix] == after[prefix])
				prefix++;
			var suffix = 0;
			while (suffix < max - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
				suffix++;
			if (prefix == before.Length && prefix == after.Length)
				return null;
			var text = after.Substring(prefix, after.Length - prefix - suffix);
			return new ToolboxSourceEdit(prefix, before.Length - prefix - suffix, text, prefix + text.Length);
		}
	}

	/// <summary>
	/// Implemented by a designer view content whose toolbox items can be dropped onto the source view
	/// of the same document. The source view finds it among its window's other views.
	/// </summary>
	[ViewContentService]
	public interface IToolboxSourceDropHandler
	{
		/// <summary>Whether this designer recognises the dragged item at all (drives the drag cursor).</summary>
		bool CanAcceptToolboxDrop(IDataObject data);

		/// <summary>The edit that inserts the dragged item at <paramref name="offset"/> of
		/// <paramref name="sourceText"/> - the source view's current text, which may hold edits the
		/// designer has not seen yet - or null when it cannot go there.</summary>
		ToolboxSourceEdit PlanToolboxDrop(IDataObject data, string sourceText, int offset);
	}

	/// <summary>
	/// Implemented by a source view that accepts toolbox drops. A real drop computes the offset under the
	/// pointer and calls this; so does automation, which is how the drop is tested without a pointer.
	/// </summary>
	[ViewContentService]
	public interface IToolboxDropTarget
	{
		/// <summary>Inserts the dragged item at <paramref name="offset"/>; false when nothing accepts it there.</summary>
		bool DropToolboxItem(IDataObject data, int offset);
	}
}
