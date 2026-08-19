using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>
	/// Owns the selection outline, its optional name label, and up to eight named resize
	/// handles - the drawn overlay only. Positioning and hit-testing are the only things this
	/// type does; it has no mouse/gesture logic of its own (each backend keeps driving its own
	/// drag/click state machine exactly as before, just calling <see cref="ShowSelection"/>/
	/// <see cref="HandleAt"/>/<see cref="ClearSelection"/> instead of touching private fields).
	///
	/// The formulas in <see cref="ShowSelection"/>/<see cref="HandleAt"/> are a relocation of
	/// UnoDesignSurfaceControl's existing LayoutSelection()/HandlePositions()/HandleAt() - same
	/// numbers, just shared. WinForms is the degenerate case: it only ever shows the "se"
	/// handle and never shows a label.
	/// </summary>
	public sealed class SelectionAdornerLayer
	{
		const double HandleSize = 7;

		public Canvas Visual { get; } = new() { IsHitTestVisible = false };

		readonly Rectangle selectionBox;
		readonly TextBlock? selectionLabel;
		readonly Dictionary<string, Rectangle> handles = new(StringComparer.Ordinal);
		Rect designSelection;
		string? selectionName;
		bool showNameLabel = true;

		/// <summary>Whether the name label is shown at all, independent of whether a name was
		/// supplied. <see cref="selectionName"/> itself keeps gating <see cref="HandleAt"/> even
		/// when this is false - hiding the label must not also disable resize handles, which is
		/// exactly why this is a separate switch rather than backends passing a null/empty label
		/// to <see cref="ShowSelection"/> when they want the label hidden.</summary>
		public bool ShowNameLabel {
			get => showNameLabel;
			set {
				if (showNameLabel == value)
					return;
				showNameLabel = value;
				UpdateLabelVisibility();
			}
		}

		/// <param name="handleNames">Which named handles this instance shows - WinUI passes all
		/// eight ("nw","n","ne","e","se","s","sw","w"); WinForms passes just <c>["se"]</c>.</param>
		/// <param name="showLabel">Whether a name label is shown above the selection (WinUI:
		/// true; WinForms: false - it has no label element today).</param>
		public SelectionAdornerLayer(IReadOnlyList<string> handleNames, Brush selectionBrush, bool showLabel = true)
		{
			selectionBox = new Rectangle {
				Stroke = selectionBrush,
				StrokeThickness = 1.5,
				StrokeDashArray = new DoubleCollection { 4, 2 },
				IsHitTestVisible = false,
				Visibility = Visibility.Collapsed
			};
			Visual.Children.Add(selectionBox);
			if (showLabel)
			{
				selectionLabel = new TextBlock {
					Background = selectionBrush,
					Foreground = Brushes.White,
					FontSize = 10,
					Padding = new Thickness(3, 1, 3, 1),
					IsHitTestVisible = false,
					Visibility = Visibility.Collapsed
				};
				Visual.Children.Add(selectionLabel);
			}
			foreach (var name in handleNames)
			{
				var handle = new Rectangle {
					Width = HandleSize,
					Height = HandleSize,
					Fill = Brushes.White,
					Stroke = selectionBrush,
					StrokeThickness = 1,
					IsHitTestVisible = false,
					Visibility = Visibility.Collapsed
				};
				handles[name] = handle;
				Visual.Children.Add(handle);
			}
		}

		/// <summary>Shows the selection outline, label and handles for a design-space rect,
		/// positioned through <paramref name="viewport"/>.</summary>
		public void ShowSelection(Rect designRect, DesignViewport viewport, string? label = null)
		{
			designSelection = designRect;
			selectionName = label;
			Layout(viewport);
		}

		/// <summary>Changes the selection outline's stroke color (e.g. WinForms recolors it
		/// when the selected component is locked) - a purely visual property, not gesture
		/// state, so it stays settable independent of <see cref="ShowSelection"/>.</summary>
		public Brush SelectionStroke
		{
			set => selectionBox.Stroke = value;
		}

		public void ClearSelection()
		{
			selectionBox.Visibility = Visibility.Collapsed;
			if (selectionLabel != null)
				selectionLabel.Visibility = Visibility.Collapsed;
			foreach (var handle in handles.Values)
				handle.Visibility = Visibility.Collapsed;
		}

		void UpdateLabelVisibility()
		{
			if (selectionLabel == null)
				return;
			selectionLabel.Visibility = showNameLabel && !string.IsNullOrEmpty(selectionName)
				? Visibility.Visible : Visibility.Collapsed;
		}

		/// <summary>Re-lays-out the current selection at a new viewport (e.g. after a zoom/pan
		/// change), without changing which element is selected.</summary>
		public void Relayout(DesignViewport viewport) => Layout(viewport);

		void Layout(DesignViewport viewport)
		{
			if (designSelection.IsEmpty)
			{
				ClearSelection();
				return;
			}
			var scale = viewport.Scale;
			var (left, top) = viewport.DesignToSurface(designSelection.X, designSelection.Y);
			var w = designSelection.Width * scale;
			var h = designSelection.Height * scale;

			selectionBox.Width = w;
			selectionBox.Height = h;
			Canvas.SetLeft(selectionBox, left);
			Canvas.SetTop(selectionBox, top);
			selectionBox.Visibility = Visibility.Visible;

			if (selectionLabel != null)
			{
				Canvas.SetLeft(selectionLabel, left);
				Canvas.SetTop(selectionLabel, Math.Max(0, top - 17));
				selectionLabel.Text = selectionName ?? "";
				UpdateLabelVisibility();
			}

			foreach (var (name, (hx, hy)) in HandlePositions())
			{
				if (!handles.TryGetValue(name, out var handle))
					continue;
				var (sx, sy) = viewport.DesignToSurface(hx, hy);
				Canvas.SetLeft(handle, sx - HandleSize / 2);
				Canvas.SetTop(handle, sy - HandleSize / 2);
				handle.Visibility = Visibility.Visible;
			}
		}

		/// <summary>The eight resize-handle anchor points in design coordinates (only the ones
		/// this instance was constructed with are actually shown).</summary>
		IEnumerable<(string Name, (double X, double Y))> HandlePositions()
		{
			var (x, y) = (designSelection.X, designSelection.Y);
			var (w, h) = (designSelection.Width, designSelection.Height);
			var (cx, cy) = (x + w / 2, y + h / 2);
			yield return ("nw", (x, y));
			yield return ("n", (cx, y));
			yield return ("ne", (x + w, y));
			yield return ("e", (x + w, cy));
			yield return ("se", (x + w, y + h));
			yield return ("s", (cx, y + h));
			yield return ("sw", (x, y + h));
			yield return ("w", (x, cy));
		}

		/// <summary>The resize handle under a design-space point, or null - same tolerance and
		/// "center third is always a move" logic as UnoDesignSurfaceControl.HandleAt today.</summary>
		public string? HandleAt(Point designPoint, DesignViewport viewport)
		{
			if (designSelection.IsEmpty || string.IsNullOrEmpty(selectionName))
				return null;
			var scale = viewport.Scale;
			var tolerance = (HandleSize / 2 + 2) / scale;
			var (x, y) = (designSelection.X, designSelection.Y);
			var (w, h) = (designSelection.Width, designSelection.Height);
			var (cx, cy) = (x + w / 2, y + h / 2);
			if (Math.Abs(designPoint.X - cx) < w / 3 && Math.Abs(designPoint.Y - cy) < h / 3)
				return null;
			foreach (var (name, (hx, hy)) in HandlePositions())
			{
				if (!handles.ContainsKey(name))
					continue;
				if (Math.Abs(designPoint.X - hx) <= tolerance && Math.Abs(designPoint.Y - hy) <= tolerance)
					return name;
			}
			return null;
		}
	}
}
