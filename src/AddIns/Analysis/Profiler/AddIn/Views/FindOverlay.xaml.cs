using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.Core;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>A search the user asked the overlay's owner to run.</summary>
/// <param name="Text">What to look for.</param>
/// <param name="Forward">Search towards the end (next) rather than the start (previous).</param>
/// <param name="Incremental">The text just changed: stay on the current match if it still matches.</param>
public sealed record FindRequest(string Text, bool Forward, bool Incremental);

/// <summary>
/// A floating find box for the top-right of a view, like the one the text editor and Visual Studio show
/// for Ctrl+F: type to search, Enter or F3 for the next match, Shift+Enter or Shift+F3 for the previous
/// one, Escape to close. It only collects the query; the owner runs the search and reports the result
/// back with <see cref="ShowResult"/>.
/// </summary>
public partial class FindOverlay : UserControl
{
    public FindOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Raised when a search should run.</summary>
    public event EventHandler<FindRequest>? FindRequested;

    /// <summary>Raised when the overlay was closed by the user.</summary>
    public event EventHandler? Closed;

    public string Text => queryBox.Text;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Shows the overlay, optionally with text to search for, and puts the caret in it.</summary>
    public void Open(string? text = null)
    {
        Visibility = Visibility.Visible;
        if (text is not null)
            queryBox.Text = text;
        queryBox.Focus();
        queryBox.SelectAll();
    }

    public void Close()
    {
        if (!IsOpen)
            return;
        Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs the search for the current text, e.g. for F3 while the overlay is closed.</summary>
    public void Find(bool forward)
    {
        if (queryBox.Text.Length > 0)
            FindRequested?.Invoke(this, new FindRequest(queryBox.Text, forward, Incremental: false));
    }

    /// <summary>Shows "3 of 12", or that nothing matched. <paramref name="current"/> is 1-based; 0 means none.</summary>
    public void ShowResult(int current, int total)
    {
        if (queryBox.Text.Length == 0)
            resultText.Text = string.Empty;
        else if (total == 0)
            resultText.Text = StringParser.Parse("${res:AddIns.Profiler.Report.FindNoResults}");
        else
            resultText.Text = StringParser.Format("${res:AddIns.Profiler.Report.FindResults}", current, total);
    }

    void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (queryBox.Text.Length == 0)
            resultText.Text = string.Empty;
        else
            FindRequested?.Invoke(this, new FindRequest(queryBox.Text, Forward: true, Incremental: true));
    }

    void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Find(forward: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            case Key.F3:
                Find(forward: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void Previous_Click(object sender, RoutedEventArgs e) => Find(forward: false);

    void Next_Click(object sender, RoutedEventArgs e) => Find(forward: true);

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
