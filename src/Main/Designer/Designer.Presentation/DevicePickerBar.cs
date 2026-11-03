using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

using ICSharpCode.SharpDevelop.Designer.Devices;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// The preview-target row above the design surface: a chip showing the current target that opens
/// a searchable, grouped picker, a favorite toggle, and a quick strip of favorite devices.
/// The designer owns the state and feeds it in through <see cref="Refresh"/>; the bar only raises
/// <see cref="DeviceChosen"/> and <see cref="FavoriteToggled"/>, so any designer (MAUI, Uno, ...)
/// can use it. Built from plain Controls because OpenDevelop runs on LibreWPF.
/// </summary>
public sealed class DevicePickerBar
{
    const int VisibleFavorites = 5;

    readonly Button chip = new() { Margin = new Thickness(6, 3, 0, 3), Padding = new Thickness(6, 1, 6, 1), MinWidth = 220, HorizontalContentAlignment = HorizontalAlignment.Left };
    readonly ToggleButton favoriteToggle = new() { Width = 24, Height = 22, Margin = new Thickness(4, 3, 0, 3), Padding = new Thickness(0) };
    readonly StackPanel strip = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
    readonly Popup popup = new() { StaysOpen = false, Placement = PlacementMode.Bottom };
    readonly TextBox search = new() { Margin = new Thickness(0, 0, 0, 6) };
    readonly StackPanel list = new();

    Device current = Device.Host;
    IReadOnlyList<Device> available = [];
    DevicePreferences prefs = DevicePreferences.Empty;
    IReadOnlyList<string> unavailable = [];

    public DevicePickerBar()
    {
        Panel = new StackPanel { Orientation = Orientation.Horizontal };
        Panel.Children.Add(chip);
        Panel.Children.Add(favoriteToggle);
        Panel.Children.Add(strip);

        chip.Click += (_, _) => Open();
        favoriteToggle.Click += (_, _) => FavoriteToggled?.Invoke(this, current.ModelKey);

        popup.PlacementTarget = chip;
        var scroll = new ScrollViewer { MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
        var body = new StackPanel { Width = 380 };
        body.Children.Add(search);
        body.Children.Add(scroll);
        popup.Child = new Border
        {
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("Border", SystemColors.ControlDarkBrush),
            Background = Brush("Background", SystemColors.WindowBrush),
            Child = body,
        };
        search.TextChanged += (_, _) => BuildList();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                popup.IsOpen = false;
            else if (e.Key == Key.Enter && FirstMatch() is { } first)
                Choose(first);
        };
    }

    /// <summary>The row to place in the designer; it appends its own display options to it.</summary>
    public StackPanel Panel { get; }

    public event EventHandler<Device>? DeviceChosen;

    public event EventHandler<string>? FavoriteToggled;

    /// <summary>Re-renders chip, favorite toggle and quick strip from the view's current state.</summary>
    public void Refresh(Device target, IReadOnlyList<Device> visibleDevices, DevicePreferences preferences,
        IReadOnlyList<string> unavailableReasons, string tooltip)
    {
        current = target;
        available = visibleDevices;
        prefs = preferences;
        unavailable = unavailableReasons;

        chip.Content = Row(target, showRuntime: true);
        chip.ToolTip = tooltip;
        bool favorite = prefs.IsFavorite(target.ModelKey);
        favoriteToggle.IsChecked = favorite;
        favoriteToggle.Content = favorite ? "★" : "☆";
        favoriteToggle.ToolTip = favorite ? "Remove from favorites" : "Add to favorites";

        strip.Children.Clear();
        var favorites = DevicePreferences.ResolveAll(prefs.Favorites, available);
        foreach (var target1 in favorites.Take(VisibleFavorites))
            strip.Children.Add(QuickButton(target1));
        if (favorites.Count > VisibleFavorites)
        {
            var more = new Button { Content = "…", Margin = new Thickness(2, 3, 0, 3), Padding = new Thickness(6, 0, 6, 0), ToolTip = "More favorites" };
            more.Click += (_, _) =>
            {
                var menu = new ContextMenu();
                foreach (var extra in favorites.Skip(VisibleFavorites))
                {
                    var item = new MenuItem { Header = extra.Name + " · " + extra.RuntimeLabel };
                    var captured = extra;
                    item.Click += (_, _) => Choose(captured);
                    menu.Items.Add(item);
                }

                menu.PlacementTarget = more;
                menu.IsOpen = true;
            };
            strip.Children.Add(more);
        }

        if (popup.IsOpen)
            BuildList();
    }

    void Open()
    {
        search.Text = "";
        BuildList();
        popup.IsOpen = true;
        search.Focus();
    }

    void Choose(Device target)
    {
        popup.IsOpen = false;
        if (target.Id != current.Id)
            DeviceChosen?.Invoke(this, target);
    }

    Device? FirstMatch()
    {
        string query = search.Text;
        return DeviceCatalog.Group(available.Where(t => DeviceCatalog.Matches(t, query)))
            .SelectMany(g => g.Entries).Select(e => e.Best).FirstOrDefault();
    }

    Button QuickButton(Device target)
    {
        bool isCurrent = target.ModelKey == current.ModelKey;
        var button = new Button
        {
            Content = Row(target, showRuntime: false),
            Margin = new Thickness(2, 3, 0, 3),
            Padding = new Thickness(5, 1, 5, 1),
            ToolTip = target.Name + " · " + target.RuntimeLabel + (target.IsRunning ? " (running)" : ""),
            FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
            BorderThickness = new Thickness(isCurrent ? 2 : 1),
        };
        button.Click += (_, _) => Choose(target);
        return button;
    }

    void BuildList()
    {
        list.Children.Clear();
        string query = search.Text;
        bool searching = !string.IsNullOrWhiteSpace(query);

        if (!searching)
        {
            AddStrip("Favorites", DevicePreferences.ResolveAll(prefs.Favorites, available));
            AddStrip("Recent", DevicePreferences.ResolveAll(prefs.Recents, available));
        }

        var matches = available.Where(t => DeviceCatalog.Matches(t, query)).ToArray();
        foreach (var group in DeviceCatalog.Group(matches))
        {
            list.Children.Add(Header(group.Title + " (" + group.Entries.Count + ")"));
            foreach (var entry in group.Entries)
            {
                list.Children.Add(ListRow(entry.Best));
                if (entry.OtherRuntimes.Count == 0)
                    continue;
                var others = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
                foreach (var other in entry.OtherRuntimes)
                    others.Children.Add(ListRow(other));
                list.Children.Add(new Expander
                {
                    Header = entry.OtherRuntimes.Count + " more runtime" + (entry.OtherRuntimes.Count == 1 ? "" : "s"),
                    Margin = new Thickness(18, 0, 0, 2),
                    IsExpanded = searching,
                    Content = others,
                });
            }
        }

        if (matches.Length == 0)
            list.Children.Add(new TextBlock { Text = "No device matches.", Margin = new Thickness(4, 6, 0, 6), Opacity = 0.6 });

        if (!searching && unavailable.Count > 0)
        {
            list.Children.Add(Header("Unavailable"));
            foreach (var reason in unavailable)
                list.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, Opacity = 0.6, Margin = new Thickness(4, 1, 0, 3) });
        }
    }

    void AddStrip(string title, IReadOnlyList<Device> targets)
    {
        if (targets.Count == 0)
            return;
        list.Children.Add(Header(title));
        var wrap = new WrapPanel();
        foreach (var target in targets)
            wrap.Children.Add(QuickButton(target));
        list.Children.Add(wrap);
    }

    UIElement ListRow(Device target)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var button = new Button
        {
            Content = Row(target, showRuntime: true),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(4, 2, 4, 2),
            FontWeight = target.Id == current.Id ? FontWeights.Bold : FontWeights.Normal,
        };
        button.Click += (_, _) => Choose(target);
        Grid.SetColumn(button, 0);
        grid.Children.Add(button);

        bool favorite = prefs.IsFavorite(target.ModelKey);
        var star = new Button
        {
            Content = favorite ? "★" : "☆",
            Width = 24,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ToolTip = favorite ? "Remove from favorites" : "Add to favorites",
        };
        star.Click += (_, _) => FavoriteToggled?.Invoke(this, target.ModelKey);
        Grid.SetColumn(star, 1);
        grid.Children.Add(star);
        return grid;
    }

    static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Opacity = 0.75,
        Margin = new Thickness(2, 8, 0, 3),
    };

    /// <summary>State dot, platform icon, name and (optionally) runtime. The dot is green for a
    /// device known to be running and grey otherwise.</summary>
    static FrameworkElement Row(Device target, bool showRuntime)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = target.IsRunning ? Brushes.SeaGreen : Brushes.Gray,
            Opacity = target.Platform == DevicePlatform.Host ? 0 : 1,
        });
        row.Children.Add(DeviceIcons.For(target.Platform, landscape: false));
        row.Children.Add(new TextBlock
        {
            Text = target.Name,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (showRuntime && target.Platform != DevicePlatform.Host)
        {
            row.Children.Add(new TextBlock
            {
                Text = "· " + target.RuntimeLabel,
                Margin = new Thickness(6, 0, 0, 0),
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return row;
    }

    static Brush Brush(string resourceKey, Brush fallback) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;
}
