using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ICSharpCode.SharpDevelop.Designer.Surface;

/// <summary>
/// Locates an actually rendered Toolbox row and waits until the compositor agrees that a pointer
/// at its layout position will hit that row.  DevFlow uses these bounds for real pointer input,
/// so ItemContainerGenerator alone is not sufficient after a virtualized or grouped scroll.
/// </summary>
public static class DesignerToolboxHitTest
{
	public static ListBoxItem? FindRealizedContainer(ItemsControl itemsControl, object item)
	{
		return FindInVisualTree(itemsControl);

		ListBoxItem? FindInVisualTree(DependencyObject node)
		{
			for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) {
				var child = VisualTreeHelper.GetChild(node, index);
				if (child is ListBoxItem listBoxItem && ReferenceEquals(listBoxItem.DataContext, item))
					return listBoxItem;
				if (FindInVisualTree(child) is { } found)
					return found;
			}
			return null;
		}
	}

	public static bool WaitUntilHitTestableAt(FrameworkElement element, int timeoutMilliseconds = 4000)
	{
		var itemsControl = FindAncestor<ItemsControl>(element);
		return itemsControl != null && WaitUntilHitTestableAt(itemsControl, element, timeoutMilliseconds);
	}

	/// <summary>Waits until the row is the actual hit target inside its owning virtualized list.
	/// The Tools pad can be hosted in a docked presentation source whose root is not a
	/// <see cref="Visual"/> on LibreWPF, so testing from the presentation-source root rejects a
	/// row that the list itself will correctly receive. The list-local hit test is the contract
	/// needed by the later screen-bounds probe and synthetic pointer gesture.</summary>
	public static bool WaitUntilHitTestableAt(ItemsControl itemsControl, FrameworkElement element, int timeoutMilliseconds = 4000)
	{
		for (var elapsed = 0; ; elapsed += 100) {
			if (element.IsLoaded && element.IsVisible
				&& HitTestResolvesToElement(itemsControl, element)
				&& IsNotCoveredOutsideTheList(element))
				return true;
			if (elapsed >= timeoutMilliseconds)
				return false;
			PumpRenderFrames(100);
		}
	}

	static bool HitTestResolvesToElement(ItemsControl itemsControl, FrameworkElement element)
	{
		try {
			var center = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), itemsControl);
			for (DependencyObject? node = itemsControl.InputHitTest(center) as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node)) {
				if (ReferenceEquals(node, element)) return true;
			}
		} catch (InvalidOperationException) { }
		return false;
	}

	/// <summary>When the docked pad has a normal visual presentation root, also prove that no
	/// sibling popup or adorner covers the row. LibreWPF may expose a non-visual root for a docked
	/// pad; in that case the list-local hit test above is the strongest available proof.</summary>
	static bool IsNotCoveredOutsideTheList(FrameworkElement element)
	{
		if (PresentationSource.FromVisual(element)?.RootVisual is not Visual rootVisual)
			return true;
		try {
			var center = element.TransformToAncestor(rootVisual)
				.Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
			for (DependencyObject? node = VisualTreeHelper.HitTest(rootVisual, center)?.VisualHit; node != null; node = VisualTreeHelper.GetParent(node)) {
				if (ReferenceEquals(node, element)) return true;
			}
		} catch (InvalidOperationException) { }
		return false;
	}

	static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
	{
		for (DependencyObject? node = VisualTreeHelper.GetParent(element); node != null; node = VisualTreeHelper.GetParent(node))
			if (node is T result) return result;
		return null;
	}

	static void PumpRenderFrames(int milliseconds)
	{
		var frame = new DispatcherFrame();
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
		timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
		timer.Start();
		Dispatcher.PushFrame(frame);
	}
}
