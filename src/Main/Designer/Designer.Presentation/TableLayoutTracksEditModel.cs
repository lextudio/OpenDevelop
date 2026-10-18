using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>Framework-neutral edit state for the TableLayoutPanel Rows and Columns dialog. The
/// WPF window is only a view: this model owns committing the displayed item before changing its
/// selection or axis, so a validation failure leaves the view on the item it must correct.</summary>
public sealed class TableLayoutTracksEditModel
{
	readonly List<DesignerTableLayoutTrackEdit> rows;
	readonly List<DesignerTableLayoutTrackEdit> columns;

	public TableLayoutTracksEditModel(DesignerTableLayoutTracks source)
	{
		if (source == null) throw new ArgumentNullException(nameof(source));
		rows = Copy(source.Rows); columns = Copy(source.Columns);
		SelectedIndex = Current.Count == 0 ? -1 : 0;
	}

	public bool ShowingRows { get; private set; } = true;
	public int SelectedIndex { get; private set; }
	public IReadOnlyList<DesignerTableLayoutTrackEdit> Rows => rows;
	public IReadOnlyList<DesignerTableLayoutTrackEdit> Columns => columns;
	IReadOnlyList<DesignerTableLayoutTrackEdit> Current => ShowingRows ? rows : columns;

	public string CurrentSizeType => SelectedIndex < 0 ? "AutoSize" : Current[SelectedIndex].SizeType;
	public string CurrentValue => SelectedIndex < 0 || Current[SelectedIndex].SizeType == "AutoSize" ? "" : Current[SelectedIndex].Value.ToString("G", CultureInfo.CurrentCulture);

	public bool TrySelect(int index, string sizeType, string value, out string error)
	{
		if (!TryCommit(sizeType, value, out error)) return false;
		if (index < 0 || index >= Current.Count) { error = "The selected track no longer exists."; return false; }
		SelectedIndex = index; error = ""; return true;
	}

	public bool TrySwitchAxis(bool rows, string sizeType, string value, out string error)
	{
		if (ShowingRows == rows) { error = ""; return true; }
		if (!TryCommit(sizeType, value, out error)) return false;
		ShowingRows = rows; SelectedIndex = Current.Count == 0 ? -1 : 0; error = ""; return true;
	}

	public bool TryCommit(string sizeType, string value, out string error)
	{
		if (SelectedIndex < 0) { error = ""; return true; }
		if (sizeType != "Absolute" && sizeType != "Percent" && sizeType != "AutoSize") {
			error = "Select a valid track size type."; return false;
		}
		if (sizeType == "AutoSize") {
			Current[SelectedIndex].SizeType = sizeType; Current[SelectedIndex].Value = 0; error = ""; return true;
		}
		if (!Double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) || !Double.IsFinite(parsed) || parsed < 0 || parsed > Single.MaxValue) {
			error = "Enter a non-negative number within the supported track-size range."; return false;
		}
		Current[SelectedIndex].SizeType = sizeType; Current[SelectedIndex].Value = parsed; error = ""; return true;
	}

	static List<DesignerTableLayoutTrackEdit> Copy(IEnumerable<DesignerTableLayoutTrackEdit> source)
		=> (source ?? throw new ArgumentNullException(nameof(source))).Select(item => new DesignerTableLayoutTrackEdit {
			SourceIndex = item.SourceIndex, SizeType = item.SizeType, Value = item.Value
		}).ToList();
}
