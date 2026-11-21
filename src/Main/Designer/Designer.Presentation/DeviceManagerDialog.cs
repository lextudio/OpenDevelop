using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.Core;
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
    readonly TreeView list = new() { MinWidth = 440 };
    readonly DeviceGroupExpansion expansion = new();
    readonly TextBlock status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock environment = new() { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    readonly Button start = new() { Content = "Start" };
    readonly Button stop = new() { Content = "Stop" };
    readonly Button edit = new();
    readonly Button delete = new();
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

        Title = ResourceService.GetString("Devices.Title");
        Width = 720;
        Height = 480;
        MinWidth = 560;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        start.Content = ResourceService.GetString("Devices.Start");
        stop.Content = ResourceService.GetString("Devices.Stop");
        delete.Content = ResourceService.GetString("Devices.Delete");
        edit.Content = ResourceService.GetString("Devices.Edit");
        hide.Content = ResourceService.GetString("Devices.HideFromPickers");

        foreach (var button in new[] { start, stop, edit, delete, hide })
        {
            button.Margin = new Thickness(0, 0, 0, 6);
            button.Padding = new Thickness(10, 2, 10, 2);
            button.MinWidth = 150;
        }

        var actions = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        actions.Children.Add(start);
        actions.Children.Add(stop);
        actions.Children.Add(edit);
        actions.Children.Add(delete);
        actions.Children.Add(hide);
        actions.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 10) });
        actions.Children.Add(creators);
        var refresh = new Button { Content = ResourceService.GetString("Devices.Refresh"), Padding = new Thickness(10, 2, 10, 2), MinWidth = 150, Margin = new Thickness(0, 6, 0, 0) };
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

        // Rows are sized for the window; the tree's default scrollviewer measures unbounded and would show
        // a horizontal scrollbar for the rightmost column.
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SelectedItemChanged += (_, _) => UpdateButtons();
        start.Click += async (_, _) => await RunAsync(DeviceAction.Start);
        stop.Click += async (_, _) => await RunAsync(DeviceAction.Stop);
        edit.Click += async (_, _) => await EditSelectedAsync();
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
                SetStatus(ResourceService.GetString("Devices.Refreshed"));
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

    Device? Selected => (list.SelectedItem as TreeViewItem)?.Tag as Device;

    void Rebuild()
    {
        var selectedId = Selected?.Id;
        var rows = model.Rows(service.Devices, getPreferences());
        list.Items.Clear();
        TreeViewItem? restore = null;
        // One collapsible root per platform (the grouping itself is DeviceManagerGroups, which is unit-tested):
        // iOS simulators routinely number in the dozens, so a flat list buried the Android entries.
        foreach (var group in DeviceManagerGroups.Build(rows))
        {
            var root = new TreeViewItem {
                Header = GroupHeaderText(PlatformGroupText(group.Platform)),
                IsExpanded = expansion.IsExpanded(group.Platform),
                Tag = null,
            };
            // Remember the user's expansion choice for the next rebuild/refresh.
            var platform = group.Platform;
            root.Expanded += (_, _) => expansion.Set(platform, true);
            root.Collapsed += (_, _) => expansion.Set(platform, false);

            foreach (var row in group.Rows)
            {
                var item = new TreeViewItem { Tag = row.Device, Header = RowContent(row) };
                root.Items.Add(item);
                if (row.Device.Id == selectedId)
                    restore = item;
            }
            list.Items.Add(root);
        }

        if (restore != null)
        {
            restore.IsSelected = true;
            restore.BringIntoView();
        }
        environment.Text = string.Join("   ", service.Providers
            .Select(p => {
                // The provider's DisplayName is English; the group headings are the localized platform names.
                var name = PlatformGroupText(p.Platform);
                return p.Unavailable is { } why
                    ? string.Format(ResourceService.GetString("Devices.ProviderUnavailable"), name, why)
                    : string.Format(ResourceService.GetString("Devices.ProviderReady"), name);
            }));
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
        // The host device's own name is the English "This computer" from the shared model; show the localized
        // wording on this surface instead.
        var name = row.Device.Platform == DevicePlatform.Host
            ? ResourceService.GetString("Devices.State.ThisComputer")
            : row.Device.Name;
        panel.Children.Add(new TextBlock { Text = name, MinWidth = 190, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = row.Device.Platform == DevicePlatform.Host ? "" : row.Device.RuntimeLabel, MinWidth = 110, Opacity = 0.75 });
        panel.Children.Add(new TextBlock { Text = StateText(row.Device), MinWidth = 90 });
        if (row.Hidden)
            panel.Children.Add(new TextBlock { Text = ResourceService.GetString("Devices.HiddenRow"), FontStyle = FontStyles.Italic, Opacity = 0.75 });
        return panel;
    }

    DeviceManagerRow? SelectedRow() =>
        Selected is { } device
            ? model.Rows(service.Devices, getPreferences()).FirstOrDefault(r => r.Device.Id == device.Id)
            : null;

    void UpdateButtons()
    {
        var row = SelectedRow();
        var editor = row is null ? null : EditorFor(row.Device);
        edit.Content = string.IsNullOrWhiteSpace(editor?.EditLabel)
            ? ResourceService.GetString("Devices.Edit")
            : editor!.EditLabel;
        edit.IsEnabled = !busy && editor?.CanEdit(row!.Device) == true;
        start.IsEnabled = !busy && row?.CanStart == true;
        stop.IsEnabled = !busy && row?.CanStop == true;
        delete.IsEnabled = !busy && row?.CanDelete == true;
        hide.IsEnabled = !busy && row?.CanHide == true;
        hide.Content = row?.Hidden == true
            ? ResourceService.GetString("Devices.ShowInPickers")
            : ResourceService.GetString("Devices.HideFromPickers");
    }

    void SetStatus(string text) => status.Text = text;

    IExternalDeviceEditor? EditorFor(Device device) =>
        service.Providers.FirstOrDefault(p => p.Platform == device.Platform) as IExternalDeviceEditor;

    async Task EditSelectedAsync()
    {
        if (Selected is not { } device || busy || EditorFor(device) is not { } editor || !editor.CanEdit(device))
            return;
        busy = true;
        UpdateButtons();
        SetStatus(string.Format(ResourceService.GetString("Devices.Editing"), device.Name));
        try {
            var result = await editor.EditAsync(device, closed.Token);
            if (!result.Ok) {
                SetStatus(string.Format(ResourceService.GetString("Devices.ActionFailed"), device.Name, result.Error));
                return;
            }
            SetStatus("");
            await RefreshAsync(announce: false);
        } catch (OperationCanceledException) {
        } finally {
            busy = false;
            UpdateButtons();
        }
    }

    async Task RunAsync(DeviceAction action)
    {
        if (Selected is not { } device || busy)
            return;
        busy = true;
        UpdateButtons();
        SetStatus(string.Format(ResourceService.GetString("Devices.ActionStarted"), ActionName(action), device.Name));
        try
        {
            var result = await model.ExecuteAsync(action, device, closed.Token);
            SetStatus(result.Ok
                ? string.Format(ResourceService.GetString("Devices.ActionRequested"), device.Name, ActionName(action))
                : string.Format(ResourceService.GetString("Devices.ActionFailed"), device.Name, result.Error));
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
        var answer = MessageBox.Show(this,
            string.Format(ResourceService.GetString("Devices.DeleteConfirm"), device.Name),
            ResourceService.GetString("Devices.DeleteTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
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
                // The provider's DisplayName is English; the button uses the same localized platform name as its group.
                var button = NewButton(string.Format(ResourceService.GetString("Devices.NewDevice"), PlatformGroupText(provider.Platform)));
                var platform = provider.Platform;
                button.Click += async (_, _) => await CreateWithFormAsync(platform);
                creators.Children.Add(button);
            }
            else if (provider is IExternalDeviceCreator external)
            {
                var button = NewButton(external.Label.TrimEnd('.') + "…");
                button.Click += async (_, _) => await CreateWithExternalAsync(external);
                creators.Children.Add(button);
            }
        }
    }

    /// <summary>
    /// The provider's own editor creates the device itself (Android's AVD editor), so the only thing this manager can do
    /// is wait for it to close and then re-read the list - and show any failure it reported.
    /// </summary>
    async Task CreateWithExternalAsync(IExternalDeviceCreator external)
    {
        if (busy)
            return;
        busy = true;
        UpdateButtons();
        SetStatus(string.Format(ResourceService.GetString("Devices.Editing"), external.Label.TrimEnd('.', '…')));
        try {
            var result = await external.OpenAsync(closed.Token);
            if (!result.Ok) {
                SetStatus(result.Error ?? string.Empty);
                return;
            }
            SetStatus("");
            await RefreshAsync(announce: false);
        } catch (OperationCanceledException) {
        } finally {
            busy = false;
            UpdateButtons();
        }
    }

    static Button NewButton(string text) => new() { Content = text, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 2, 10, 2), MinWidth = 150 };

    async Task CreateWithFormAsync(DevicePlatform platform)
    {
        if (model.CreationFor(platform) is not { } creation)
            return;
        SetStatus(ResourceService.GetString("Devices.ReadingModels"));
        DeviceCreateOptions options;
        try { options = await creation.GetCreateOptionsAsync(closed.Token); }
        catch (OperationCanceledException) { return; }
        if (options.Types.Count == 0 || options.Runtimes.Count == 0)
        {
            SetStatus(ResourceService.GetString("Devices.NoModels"));
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
        SetStatus(string.Format(ResourceService.GetString("Devices.Creating"), form.DeviceName));
        try
        {
            var result = await model.CreateAsync(platform, form.DeviceName, form.TypeId, form.RuntimeId, closed.Token);
            SetStatus(result.Ok
                ? string.Format(ResourceService.GetString("Devices.Created"), form.DeviceName)
                : string.Format(ResourceService.GetString("Devices.CreateFailed"), form.DeviceName, result.Error));
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
            Title = ResourceService.GetString("Devices.NewSimulatorTitle");
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

            var ok = new Button { Content = ResourceService.GetString("Devices.Create"), IsDefault = true, Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = ResourceService.GetString("Devices.Cancel"), IsCancel = true, Padding = new Thickness(14, 2, 14, 2) };
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
            panel.Children.Add(Label(ResourceService.GetString("Devices.Name")));
            panel.Children.Add(name);
            panel.Children.Add(Label(ResourceService.GetString("Devices.Model")));
            panel.Children.Add(types);
            panel.Children.Add(Label(ResourceService.GetString("Devices.Runtime")));
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
                runtimes.Items.Add(ResourceService.GetString("Devices.NoCompatibleRuntime"));
        }

        static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 8, 0, 2) };

        public string DeviceName => name.Text.Trim();

        public string TypeId => options.Types[Math.Max(types.SelectedIndex, 0)].Id;

        public string RuntimeId => compatible[Math.Max(runtimes.SelectedIndex, 0)].Id;
    }

    /// <summary>The platform heading shown on the collapsible root row.</summary>
    static TextBlock GroupHeaderText(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.Bold,
        Opacity = 0.85,
    };

    static string PlatformGroupText(DevicePlatform platform) => platform switch
    {
        DevicePlatform.Host => ResourceService.GetString("Devices.Group.Host"),
        DevicePlatform.IosSimulator => ResourceService.GetString("Devices.Group.Ios"),
        DevicePlatform.AndroidEmulator => ResourceService.GetString("Devices.Group.Android"),
        _ => platform.ToString(),
    };

    /// <summary>The row's state words. Kept here rather than in DeviceManagerModel because that model is shared
    /// with the out-of-process design host and therefore has no resource service.</summary>
    static string StateText(Device device) => device.Platform == DevicePlatform.Host
        ? ResourceService.GetString("Devices.State.ThisComputer")
        : device.State switch
        {
            DeviceState.Booted => ResourceService.GetString("Devices.State.Running"),
            DeviceState.Booting => ResourceService.GetString("Devices.State.Starting"),
            DeviceState.Shutdown => ResourceService.GetString("Devices.State.Stopped"),
            _ => ResourceService.GetString("Devices.State.Unknown"),
        };

    static string ActionName(DeviceAction action) => action switch
    {
        DeviceAction.Start => ResourceService.GetString("Devices.Start"),
        DeviceAction.Stop => ResourceService.GetString("Devices.Stop"),
        _ => ResourceService.GetString("Devices.Delete"),
    };
}
