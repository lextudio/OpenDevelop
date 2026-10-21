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
		if (PresentationSource.FromVisual(element)?.RootVisual is not Visual rootVisual)
			return false;
		for (var elapsed = 0; ; elapsed += 100) {
			if (HitTestResolvesToElement(rootVisual, element))
				return true;
			if (elapsed >= timeoutMilliseconds)
				return false;
			PumpRenderFrames(100);
		}
	}

	static bool HitTestResolvesToElement(Visual rootVisual, FrameworkElement element)
	{
		try {
			var center = element.TransformToAncestor(rootVisual)
				.Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
			for (DependencyObject? node = VisualTreeHelper.HitTest(rootVisual, center)?.VisualHit; node != null; node = VisualTreeHelper.GetParent(node)) {
				if (ReferenceEquals(node, element)) return true;
			}
		} catch (InvalidOperationException) { }
		return false;
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
