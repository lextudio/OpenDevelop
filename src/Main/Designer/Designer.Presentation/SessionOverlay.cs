using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using ICSharpCode.SharpDevelop.Designer.Devices;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// The progress card a designer lays over its canvas while a preview host starts: a dimming scrim,
/// the four steps of <see cref="DesignerSessionSteps"/>, the elapsed time, and on failure a plain
/// reason with Retry and fallback buttons. It never covers the canvas when the session is Ready or
/// Idle, so the previous frame stays visible (dimmed) while switching. Plain Controls (LibreWPF).
/// </summary>
public sealed class SessionOverlay : Grid
{
    readonly TextBlock title = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    readonly StackPanel steps = new();
    readonly TextBlock reason = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 340 };
    readonly TextBlock elapsed = new() { Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7 };
    readonly Button retry = new() { Content = "Retry", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
    readonly Button cancel = new() { Content = "Cancel", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    readonly Button fallback = new() { Padding = new Thickness(10, 2, 10, 2) };
    readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };

    public SessionOverlay()
    {
        Visibility = Visibility.Collapsed;
        Background = new SolidColorBrush(Color.FromArgb(0x90, 0x80, 0x80, 0x80));
        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("Border", SystemColors.ControlDarkBrush),
            Background = Brush("Background", SystemColors.WindowBrush),
        };
        buttons.Children.Add(retry);
        buttons.Children.Add(fallback);
        var body = new StackPanel { MinWidth = 260 };
        body.Children.Add(title);
        body.Children.Add(steps);
        body.Children.Add(reason);
        body.Children.Add(elapsed);
        body.Children.Add(buttons);
        body.Children.Add(cancel);
        card.Child = body;
        Children.Add(card);
        retry.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
        cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        fallback.Click += (_, _) => FallbackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised by Cancel while the session is still starting; the designer abandons the
    /// start and returns to the previous target.</summary>
    public event EventHandler? CancelRequested;

    public event EventHandler? RetryRequested;

    public event EventHandler? FallbackRequested;

    /// <summary>Shows the card for an in-progress or failed session, hides it otherwise.
    /// <paramref name="fallbackLabel"/> (e.g. "Use macOS") adds the escape button on failure;
    /// <paramref name="canCancel"/> shows Cancel while the session is still starting.</summary>
    public void Update(DesignerSessionStatus status, string deviceName, string? fallbackLabel, bool canCancel, DateTimeOffset now)
    {
        var failed = status.Phase == DesignerSessionPhase.Failed;
        if (!status.InProgress && !failed)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        title.Text = failed ? "Could not start the preview on " + deviceName : DesignerSessionStatus.Describe(status.Phase, deviceName);
        steps.Children.Clear();
        foreach (var step in DesignerSessionSteps.For(status))
        {
            var mark = step.State switch
            {
                SessionStepState.Done => "✓",
                SessionStepState.Active => "…",
                SessionStepState.Failed => "✗",
                _ => "·",
            };
            var row = new TextBlock
            {
                Text = mark + "  " + step.Label,
                Margin = new Thickness(0, 1, 0, 1),
                Opacity = step.State == SessionStepState.Pending ? 0.5 : 1,
            };
            // Inherit the theme's foreground; only the failed row is tinted.
            if (step.State == SessionStepState.Failed)
                row.Foreground = Brushes.Firebrick;
            steps.Children.Add(row);
        }

        reason.Text = failed ? status.Reason ?? "" : "";
        reason.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        elapsed.Text = failed ? "" : $"{(int)status.Elapsed(now).TotalSeconds} s";
        elapsed.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        fallback.Content = fallbackLabel;
        fallback.Visibility = fallbackLabel == null ? Visibility.Collapsed : Visibility.Visible;
        buttons.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        cancel.Visibility = !failed && canCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    static Brush Brush(string resourceKey, Brush fallback) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;
}
