using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using ICSharpCode.SharpDevelop.Designer.Devices;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// The Device Manager: every simulator, emulator and the host computer in one list with their
/// state, and the actions the owning provider supports (start, stop, delete), plus hide/show and
/// creating a device. It shows the shared <see cref="DeviceService"/> state and refreshes it every
/// few seconds while open, so a device started elsewhere turns green here too. All logic lives in
/// <see cref="DeviceManagerModel"/>; this is only the window. Plain Controls (LibreWPF).
/// </summary>
public sealed class DeviceManagerDialog : Window
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    readonly DeviceService service;
    readonly DeviceManagerModel model;
    readonly Func<DevicePreferences> getPreferences;
    readonly Action<DevicePreferences> setPreferences;
    readonly ListBox list = new() { MinWidth = 440 };
    readonly TextBlock status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock environment = new() { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    readonly Button start = new() { Content = "Start" };
    readonly Button stop = new() { Content = "Stop" };
    readonly Button delete = new() { Content = "Delete…" };
    readonly Button hide = new() { Content = "Hide" };
    readonly StackPanel creators = new();
    readonly CancellationTokenSource closed = new();
    bool busy;

    public DeviceManagerDialog(DeviceService service, Func<DevicePreferences> getPreferences, Action<DevicePreferences> setPreferences)
    {
        this.service = service;
        this.getPreferences = getPreferences;
        this.setPreferences = setPreferences;
        model = new DeviceManagerModel(service);

        Title = "Manage devices";
        Width = 720;
        Height = 480;
        MinWidth = 560;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var button in new[] { start, stop, delete, hide })
        {
            button.Margin = new Thickness(0, 0, 0, 6);
            button.Padding = new Thickness(10, 2, 10, 2);
            button.MinWidth = 150;
        }

        var actions = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        actions.Children.Add(start);
        actions.Children.Add(stop);
        actions.Children.Add(delete);
        actions.Children.Add(hide);
        actions.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 10) });
        actions.Children.Add(creators);
        var refresh = new Button { Content = "Refresh", Padding = new Thickness(10, 2, 10, 2), MinWidth = 150, Margin = new Thickness(0, 6, 0, 0) };
        refresh.Click += async (_, _) => await RefreshAsync(announce: true);
        actions.Children.Add(refresh);

        var body = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(environment, Dock.Top);
        body.Children.Add(environment);
        DockPanel.SetDock(status, Dock.Bottom);
        body.Children.Add(status);
        DockPanel.SetDock(actions, Dock.Right);
        body.Children.Add(actions);
        body.Children.Add(list);
        Content = body;

        list.SelectionChanged += (_, _) => UpdateButtons();
        start.Click += async (_, _) => await RunAsync(DeviceAction.Start);
        stop.Click += async (_, _) => await RunAsync(DeviceAction.Stop);
        delete.Click += async (_, _) => await ConfirmAndDeleteAsync();
        hide.Click += (_, _) => ToggleHidden();
        service.Changed += OnServiceChanged;
        Loaded += async (_, _) =>
        {
            BuildCreators();
            Rebuild();
            await RefreshAsync(announce: false);
            _ = PollAsync();
        };
        Closed += (_, _) =>
        {
            service.Changed -= OnServiceChanged;
            closed.Cancel();
        };
    }

    /// <summary>Re-reads the devices; the list keeps its selection by device id.</summary>
    async Task RefreshAsync(bool announce)
    {
        try
        {
            await service.RefreshAsync(closed.Token);
            if (announce)
                SetStatus("Device list refreshed.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task PollAsync()
    {
        while (!closed.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, closed.Token); }
            catch (OperationCanceledException) { return; }
            if (!busy)
                await RefreshAsync(announce: false);
        }
    }

    void OnServiceChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(Rebuild));

    Device? Selected => (list.SelectedItem as ListBoxItem)?.Tag as Device;

    void Rebuild()
    {
        var selectedId = Selected?.Id;
        var rows = model.Rows(service.Devices, getPreferences());
        list.Items.Clear();
        ListBoxItem? restore = null;
        foreach (var row in rows)
        {
            var item = new ListBoxItem { Tag = row.Device, Content = RowContent(row) };
            list.Items.Add(item);
            if (row.Device.Id == selectedId)
                restore = item;
        }

        if (restore != null)
            list.SelectedItem = restore;
        environment.Text = string.Join("   ", service.Providers
            .Select(p => p.Unavailable is { } why ? $"{p.DisplayName}: {why}" : $"{p.DisplayName}: ready"));
        UpdateButtons();
    }

    static FrameworkElement RowContent(DeviceManagerRow row)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Opacity = row.Hidden ? 0.5 : 1 };
        panel.Children.Add(new Ellipse
        {
            Width = 9,
            Height = 9,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = row.Device.State switch
            {
                DeviceState.Booted => Brushes.SeaGreen,
                DeviceState.Booting => Brushes.Goldenrod,
                _ => Brushes.Gray,
            },
        });
        panel.Children.Add(new TextBlock { Text = row.Device.Name, MinWidth = 190, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = row.Device.Platform == DevicePlatform.Host ? "" : row.Device.RuntimeLabel, MinWidth = 110, Opacity = 0.75 });
        panel.Children.Add(new TextBlock { Text = row.StateText, MinWidth = 90 });
        if (row.Hidden)
            panel.Children.Add(new TextBlock { Text = "hidden", FontStyle = FontStyles.Italic, Opacity = 0.75 });
        return panel;
    }

    DeviceManagerRow? SelectedRow() =>
        Selected is { } device
            ? model.Rows(service.Devices, getPreferences()).FirstOrDefault(r => r.Device.Id == device.Id)
            : null;

    void UpdateButtons()
    {
        var row = SelectedRow();
        start.IsEnabled = !busy && row?.CanStart == true;
        stop.IsEnabled = !busy && row?.CanStop == true;
        delete.IsEnabled = !busy && row?.CanDelete == true;
        hide.IsEnabled = !busy && row?.CanHide == true;
        hide.Content = row?.Hidden == true ? "Show in pickers" : "Hide from pickers";
    }

    void SetStatus(string text) => status.Text = text;

    async Task RunAsync(DeviceAction action)
    {
        if (Selected is not { } device || busy)
            return;
        busy = true;
        UpdateButtons();
        SetStatus($"{action} {device.Name}…");
        try
        {
            var result = await model.ExecuteAsync(action, device, closed.Token);
            SetStatus(result.Ok ? $"{device.Name}: {action.ToString().ToLowerInvariant()} requested." : $"{device.Name}: {result.Error}");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            busy = false;
            UpdateButtons();
        }
    }

    async Task ConfirmAndDeleteAsync()
    {
        if (Selected is not { } device)
            return;
        var answer = MessageBox.Show(this, $"Delete {device.Name}? This removes the device and its data from this machine.",
            "Delete device", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK)
            await RunAsync(DeviceAction.Delete);
    }

    void ToggleHidden()
    {
        if (Selected is not { } device)
            return;
        var preferences = getPreferences();
        setPreferences(preferences.IsHidden(device) ? preferences.Unhide(device.Id) : preferences.Hide(device.Id));
        Rebuild();
    }

    void BuildCreators()
    {
        creators.Children.Clear();
        foreach (var provider in service.Providers)
        {
            if (provider is IDeviceCreation)
            {
                var button = NewButton("New " + provider.DisplayName + "…");
                var platform = provider.Platform;
                button.Click += async (_, _) => await CreateWithFormAsync(platform);
                creators.Children.Add(button);
            }
            else if (provider is IExternalDeviceCreator external)
            {
                var button = NewButton(external.Label.TrimEnd('.') + "…");
                button.Click += (_, _) => external.Open();
                creators.Children.Add(button);
            }
        }
    }

    static Button NewButton(string text) => new() { Content = text, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 2, 10, 2), MinWidth = 150 };

    async Task CreateWithFormAsync(DevicePlatform platform)
    {
        if (model.CreationFor(platform) is not { } creation)
            return;
        SetStatus("Reading the available models and runtimes…");
        DeviceCreateOptions options;
        try { options = await creation.GetCreateOptionsAsync(closed.Token); }
        catch (OperationCanceledException) { return; }
        if (options.Types.Count == 0 || options.Runtimes.Count == 0)
        {
            SetStatus("No device models or runtimes are installed; install a runtime in Xcode first.");
            return;
        }

        var form = new NewDeviceForm(options) { Owner = this };
        if (form.ShowDialog() != true)
        {
            SetStatus("");
            return;
        }

        busy = true;
        UpdateButtons();
        SetStatus($"Creating {form.DeviceName}…");
        try
        {
            var result = await model.CreateAsync(platform, form.DeviceName, form.TypeId, form.RuntimeId, closed.Token);
            SetStatus(result.Ok ? $"Created {form.DeviceName}." : $"Could not create {form.DeviceName}: {result.Error}");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            busy = false;
            UpdateButtons();
        }
    }

    /// <summary>The small "new simulator" form: a name, a hardware model and a runtime.</summary>
    sealed class NewDeviceForm : Window
    {
        readonly TextBox name = new() { MinWidth = 280 };
        readonly ComboBox types = new() { MinWidth = 280 };
        readonly ComboBox runtimes = new() { MinWidth = 280 };
        readonly DeviceCreateOptions options;
        IReadOnlyList<RuntimeOption> compatible = [];

        public NewDeviceForm(DeviceCreateOptions options)
        {
            this.options = options;
            Title = "New simulator";
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            foreach (var type in options.Types)
                types.Items.Add(type.Name);
            types.SelectedIndex = 0;
            name.Text = options.Types[0].Name;
            ShowCompatibleRuntimes();
            types.SelectionChanged += (_, _) =>
            {
                // Follow the model's name until the user types their own.
                if (types.SelectedIndex >= 0 && options.Types.Any(t => t.Name == name.Text))
                    name.Text = options.Types[types.SelectedIndex].Name;
                ShowCompatibleRuntimes();
            };

            var ok = new Button { Content = "Create", IsDefault = true, Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 2, 14, 2) };
            ok.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(name.Text) || compatible.Count == 0)
                    return;
                DialogResult = true;
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Label("Name"));
            panel.Children.Add(name);
            panel.Children.Add(Label("Model"));
            panel.Children.Add(types);
            panel.Children.Add(Label("Runtime"));
            panel.Children.Add(runtimes);
            panel.Children.Add(buttons);
            Content = panel;
        }

        /// <summary>A model newer than a runtime cannot be created on it, so only the runtimes that
        /// support the chosen model are offered.</summary>
        void ShowCompatibleRuntimes()
        {
            var typeId = options.Types[Math.Max(types.SelectedIndex, 0)].Id;
            compatible = options.Runtimes.Where(r => r.Supports(typeId)).ToArray();
            runtimes.Items.Clear();
            foreach (var runtime in compatible)
                runtimes.Items.Add(runtime.Name);
            if (compatible.Count > 0)
                runtimes.SelectedIndex = 0;
            else
                runtimes.Items.Add("No installed runtime supports this model");
        }

        static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 8, 0, 2) };

        public string DeviceName => name.Text.Trim();

        public string TypeId => options.Types[Math.Max(types.SelectedIndex, 0)].Id;

        public string RuntimeId => compatible[Math.Max(runtimes.SelectedIndex, 0)].Id;
    }
}
