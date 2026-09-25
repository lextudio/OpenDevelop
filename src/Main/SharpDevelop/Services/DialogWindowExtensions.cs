#nullable enable
using System;
using System.Windows;
using System.Windows.Threading;

namespace ICSharpCode.SharpDevelop.Services;

internal static class DialogWindowExtensions
{
    /// <summary>
    /// Ends a modal dialog from a button's Click handler. The close is posted rather than done
    /// inline: on the portable WPF backend a Click handler runs inside the render loop, where closing
    /// (and so disposing) the window throws - see WpfMessageService.CloseDialog.
    /// </summary>
    public static void CloseDialog(this Window window, bool result) =>
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (window.IsVisible)
                window.DialogResult = result;
        }));
}
