using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Devices;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Surface;
using ICSharpCode.WinUIXamlDesigner;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// iOS Simulator previews for the Uno designer. When the project targets iOS (and this machine has
/// simulators), the canvas gets the shared device picker row and progress card, and choosing a simulator
/// swaps the desktop design host for the iOS head running inside it: same document, same DDP, a real
/// device runtime. Everything device-generic (picker, Device Manager, session phases, overlay, host
/// process) lives in OpenDevelop's shared designer layers; this file only connects them to Uno.
/// </summary>
sealed partial class UnoDesignRuntimeHost : IWinUIXamlChrome, IWinUIXamlDevicePreview, IWinUIXamlSizePresets
{
	/// <summary>Folder (beside this assembly) that holds the iOS head's app bundle.</summary>
	internal const string IosHostFolder = "UnoHost.iOS";
	const string IosHostBundle = "WinUIXamlDesigner.UnoHost.app";
	internal const string AndroidHostFolder = "UnoHost.Android";
	const string AndroidHostApk = "WinUIXamlDesigner.UnoHost.apk";
	const string AndroidPackage = "com.lextudio.opendevelop.unodesignhost";

	Device previewDevice = Device.Host;
	DevicePreferences devicePreferences = DevicePreferences.Empty;
	ProjectTargets projectTargets = ProjectTargets.None;
	bool desktopOnlyProject;
	string projectFile;
	string connectKind = "unknown";
	DevicePickerBar deviceBar;
	SessionOverlay deviceOverlay;
	UIElement chromeRoot;
	bool chromeBuilt;
	int connectGeneration;
	CancellationTokenSource connectCancellation = new();
	Device deviceBeforeSwitch = Device.Host;
	bool landscape;
	bool frameEnabled = true;
	CheckBox frameToggle;
	Button orientationButton;
	/// <summary>Longest wait for an emulator that had to be started (a cold boot of a small AVD takes minutes).</summary>
	static readonly TimeSpan EmulatorBootTimeout = TimeSpan.FromMinutes(8);
	DesignerHostProcessClient currentDeviceHost;
	readonly DesignerSession deviceSession = new();
	static DeviceManagerDialog deviceManager;

	public string PreviewDeviceId => previewDevice.Id;

	public bool IsLandscape => landscape;

	public bool HasDeviceFrame => surface.DeviceChrome != null;

	public DesignerSessionStatus SessionStatus => deviceSession.Status;

	public IReadOnlyList<Device> OfferedDevices => OfferedDevicesList();
	public IReadOnlyList<string> SizePresetKeys => surface.SizePresetKeys;

	public async Task<bool> SwitchDeviceAsync(string deviceId)
	{
		var device = OfferedDevicesList().FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
		if (device == null)
			return false;
		await SwitchDeviceAsync(device).ConfigureAwait(true);
		return true;
	}

	/// <summary>Called from the constructor: reads what the project targets and restores the remembered device.</summary>
	void InitializeDeviceTargets(string projectFileName)
	{
		projectFile = projectFileName;
		bool isUno = !hostDisplayName.Contains("WinUI", StringComparison.OrdinalIgnoreCase)
			&& !hostDisplayName.Contains("ProGPU", StringComparison.OrdinalIgnoreCase);
		projectTargets = isUno ? ProjectTargets.ReadFromFile(projectFileName) : ProjectTargets.None;
		desktopOnlyProject = isUno && !projectTargets.SupportsIos && !projectTargets.SupportsAndroid;
		// The canvas originally advertised every responsive form factor regardless of the
		// project's TFM. That made a net10.0-desktop-only Uno project look deployable to a
		// phone or tablet. Keep the desktop design-size option, but expose mobile sizes only
		// when at least one declared TFM can actually run on a mobile target.
		if (isUno) {
		surface.SetSizePresets(projectTargets.SupportsIos || projectTargets.SupportsAndroid
				? [
					new DesignSizePreset("phone", "Phone 390x844"),
					new DesignSizePreset("tablet", "Tablet 768x1024"),
					new DesignSizePreset("desktop", "Desktop 1280x720")
				]
				: [new DesignSizePreset("desktop", "Desktop 1280x720")]);
		}
		ApplyDeviceChrome();
		if (!OffersDevices)
			return;
		devicePreferences = DevicePreferencesStore.Load();
		DeviceService.Default.Changed += OnSharedDevicesChanged;
		DevicePreferencesStore.Saved += OnSharedDevicesChanged;
		// The machine's simulators are listed asynchronously (simctl can take seconds); the picker fills in through
		// DeviceService.Changed when they arrive, and a remembered simulator is restored then.
		_ = RestoreRememberedDeviceAsync();
	}

	async Task RestoreRememberedDeviceAsync()
	{
		try
		{
			await DeviceService.Default.RefreshAsync().ConfigureAwait(false);
		}
		catch (Exception)
		{
			return; // the picker simply stays on the computer
		}

		var remembered = ReadRememberedDeviceId();
		if (remembered == null)
			return;
		await dispatcher.InvokeAsync(async () =>
		{
			if (!disposed && previewDevice.Platform == DevicePlatform.Host
				&& OfferedDevicesList().FirstOrDefault(d => d.Id == remembered) is { } device)
				await SwitchDeviceAsync(device);
		}).Task.Unwrap().ConfigureAwait(false);
	}

	/// <summary>Whether this project can preview on a device here: iOS needs macOS, Android needs a Unix shell (the host
	/// process is a watcher script).</summary>
	bool OffersDevices =>
		(projectTargets.SupportsIos && OperatingSystem.IsMacOS())
		|| (projectTargets.SupportsAndroid && !OperatingSystem.IsWindows());

	public UIElement ChromeRoot
	{
		get
		{
			if (!chromeBuilt)
			{
				chromeBuilt = true;
				if (OffersDevices)
					BuildChrome();
			}

			return chromeRoot;
		}
	}

	void BuildChrome()
	{
		var bar = new DevicePickerBar();
		bar.DeviceChosen += (_, device) =>
		{
			devicePreferences = devicePreferences.NoteUsed(device.ModelKey);
			DevicePreferencesStore.Save(devicePreferences);
			_ = SwitchDeviceAsync(device);
		};
		bar.FavoriteToggled += (_, modelKey) =>
		{
			devicePreferences = devicePreferences.ToggleFavorite(modelKey);
			DevicePreferencesStore.Save(devicePreferences);
			RefreshDeviceBar();
		};
		bar.ManageRequested += (_, _) => OpenDeviceManager();
		deviceBar = bar;

		// Portrait/landscape swaps the device canvas (the head renders at whatever size it is asked for).
		var orientation = new Button
		{
			Width = 26,
			Height = 22,
			Margin = new Thickness(4, 3, 0, 3),
			Padding = new Thickness(0),
			ToolTip = "Rotate the device",
		};
		orientation.Click += (_, _) => ToggleOrientation();
		orientationButton = orientation;
		bar.Panel.Children.Add(orientation);

		var frame = new CheckBox
		{
			Content = "Frame",
			IsChecked = frameEnabled,
			Margin = new Thickness(8, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center,
			ToolTip = "Draw a device body around the preview.",
		};
		frame.Checked += (_, _) => SetFrame(true);
		frame.Unchecked += (_, _) => SetFrame(false);
		frameToggle = frame;
		bar.Panel.Children.Add(frame);

		var overlay = new SessionOverlay();
		overlay.CancelRequested += (_, _) => _ = CancelSwitchAsync();
		overlay.RetryRequested += (_, _) => _ = ReconnectAsync();
		overlay.FallbackRequested += (_, _) => _ = SwitchDeviceAsync(Device.Host);
		deviceOverlay = overlay;
		deviceSession.Changed += (_, _) => dispatcher.BeginInvoke(UpdateDeviceOverlay);

		var stack = new Grid();
		stack.Children.Add(surface);
		stack.Children.Add(overlay);
		var panel = new DockPanel();
		DockPanel.SetDock(bar.Panel, Dock.Top);
		panel.Children.Add(bar.Panel);
		panel.Children.Add(stack);
		chromeRoot = panel;
		RefreshDeviceBar();
	}

	/// <summary>The devices this project can preview on: the host computer, plus every visible simulator.</summary>
	IReadOnlyList<Device> OfferedDevicesList()
	{
		var shared = DeviceService.Default.Devices;
		var devices = new List<Device> { Device.Host };
		if (projectTargets.SupportsIos && OperatingSystem.IsMacOS())
			devices.AddRange(devicePreferences.Visible(shared.Where(d => d.Platform == DevicePlatform.IosSimulator)));
		if (projectTargets.SupportsAndroid && !OperatingSystem.IsWindows())
			devices.AddRange(devicePreferences.Visible(shared.Where(d => d.Platform == DevicePlatform.AndroidEmulator)));
		return devices;
	}

	IReadOnlyList<string> UnavailableReasons()
	{
		var reasons = new List<string>();
		var ios = DeviceService.Default.Providers.FirstOrDefault(p => p.Platform == DevicePlatform.IosSimulator);
		if (ios?.Unavailable is { } why)
			reasons.Add("iOS: " + why);
		else if (!OfferedDevicesList().Any(d => d.Platform == DevicePlatform.IosSimulator))
			reasons.Add("iOS: no iPhone or iPad simulator is installed (or all are hidden).");
		if (projectTargets.SupportsAndroid)
		{
			var android = DeviceService.Default.Providers.FirstOrDefault(p => p.Platform == DevicePlatform.AndroidEmulator);
			if (OperatingSystem.IsWindows())
				reasons.Add("Android: previews need macOS or Linux for now.");
			else if (android == null)
				reasons.Add("Android: the Android Device Manager add-in is not loaded.");
			else if (android.Unavailable is { } androidWhy)
				reasons.Add("Android: " + androidWhy);
			else if (!OfferedDevicesList().Any(d => d.Platform == DevicePlatform.AndroidEmulator))
				reasons.Add("Android: no emulator exists (create one in Manage devices) or all are hidden.");
		}

		return reasons;
	}

	void RefreshDeviceBar()
	{
		if (deviceBar == null)
			return;
		deviceBar.Refresh(previewDevice, OfferedDevicesList(), devicePreferences, UnavailableReasons(),
			"Preview device: " + previewDevice.Name);
		if (orientationButton != null)
		{
			orientationButton.IsEnabled = previewDevice.Platform != DevicePlatform.Host;
			orientationButton.Content = DeviceIcons.For(previewDevice.Platform, landscape);
		}
	}

	void OnSharedDevicesChanged(object sender, EventArgs e) => dispatcher.BeginInvoke(() =>
	{
		if (disposed)
			return;
		devicePreferences = DevicePreferencesStore.Load();
		RefreshDeviceBar();
		// A device that was hidden or deleted while in use: go back to the host computer and say why.
		if (previewDevice.Platform != DevicePlatform.Host && OfferedDevicesList().All(d => d.Id != previewDevice.Id))
		{
			SetStatus($"{previewDevice.Name} is no longer available (hidden or deleted); showing the desktop preview.");
			_ = SwitchDeviceAsync(Device.Host);
		}
	});

	void UpdateDeviceOverlay() =>
		deviceOverlay?.Update(
			deviceSession.Status,
			previewDevice.Name,
			previewDevice.Platform == DevicePlatform.Host ? null : "Use desktop",
			canCancel: deviceBeforeSwitch.Id != previewDevice.Id,
			DateTimeOffset.UtcNow);

	static void OpenDeviceManager()
	{
		if (deviceManager is { IsLoaded: true })
		{
			deviceManager.Activate();
			return;
		}

		var dialog = new DeviceManagerDialog(DeviceService.Default, DevicePreferencesStore.Load, preferences => DevicePreferencesStore.Save(preferences));
		try { dialog.Owner = SD.Workbench.MainWindow; } catch (Exception) { }
		dialog.Closed += (_, _) => deviceManager = null;
		deviceManager = dialog;
		dialog.Show();
	}

	/// <summary>Switches the preview to <paramref name="device"/>: the current host goes away, the right one starts (the iOS
	/// head for a simulator, the desktop host for the computer), and the last text is rendered on it.</summary>
	async Task SwitchDeviceAsync(Device device)
	{
		if (disposed || device.Id == previewDevice.Id)
			return;
		deviceBeforeSwitch = previewDevice;
		previewDevice = device;
		RememberDeviceId(device.Id);
		surface.Capabilities = device.Platform == DevicePlatform.Host
			? surface.Capabilities | ICSharpCode.SharpDevelop.Widgets.DesignerCanvasCapabilities.DesignSize
			: surface.Capabilities & ~ICSharpCode.SharpDevelop.Widgets.DesignerCanvasCapabilities.DesignSize;
		ApplyDeviceChrome();
		RefreshDeviceBar();
		await ReconnectAsync();
	}

	void SetFrame(bool enabled)
	{
		frameEnabled = enabled;
		ApplyDeviceChrome();
	}

	/// <summary>Draws a generic device body for a phone or tablet (no Xcode/Android device database is read: the shape is a
	/// plausible default per platform and family). Desktop previews always carry a host-owned
	/// window frame, so a Page has an obvious application boundary even when its XAML fills the
	/// whole render.</summary>
	void ApplyDeviceChrome() => surface.DeviceChrome = previewDevice.Platform == DevicePlatform.Host
		? frameEnabled ? DesktopChrome() : null
		: frameEnabled ? ChromeFor(previewDevice) : null;

	DesignerDeviceChrome DesktopChrome() => new()
	{
		Kind = DesignerDeviceKind.DesktopWindow,
		ScreenCornerRadius = 2,
		BezelThickness = 1,
		Title = hostDisplayName + " desktop preview",
		TitleBarHeight = 26,
		HomeIndicator = false,
	};

	static ICSharpCode.SharpDevelop.Designer.Surface.DesignerDeviceChrome ChromeFor(Device device)
	{
		if (device.Platform == DevicePlatform.Host)
			return null;
		bool tablet = device.Family == DeviceFamily.Tablet;
		bool ios = device.Platform == DevicePlatform.IosSimulator;
		return new ICSharpCode.SharpDevelop.Designer.Surface.DesignerDeviceChrome
		{
			Kind = tablet ? ICSharpCode.SharpDevelop.Designer.Surface.DesignerDeviceKind.Tablet : ICSharpCode.SharpDevelop.Designer.Surface.DesignerDeviceKind.Phone,
			ScreenCornerRadius = tablet ? 18 : ios ? 47 : 28,
			BezelThickness = tablet ? 12 : 6,
			Notch = !tablet && ios ? ICSharpCode.SharpDevelop.Designer.Surface.DesignerNotchKind.Island : ICSharpCode.SharpDevelop.Designer.Surface.DesignerNotchKind.None,
			HomeIndicator = ios && !tablet,
		};
	}

	/// <summary>The overlay's Cancel: abandon the start in flight (its connection is cancelled) and go back to the device that was
	/// showing before the switch.</summary>
	public async Task CancelSwitchAsync()
	{
		var back = deviceBeforeSwitch;
		if (disposed || back.Id == previewDevice.Id)
			return;
		await SwitchDeviceAsync(back).ConfigureAwait(true);
	}

	/// <summary>Swaps portrait and landscape for a device preview and renders again.</summary>
	public void ToggleOrientation()
	{
		if (previewDevice.Platform == DevicePlatform.Host)
			return;
		landscape = !landscape;
		RefreshDeviceBar();
		if (Volatile.Read(ref lastLoadedText) is { } text && client != null)
			_ = RenderAsync(text, Interlocked.Increment(ref version));
	}

	/// <summary>Drops the current host and starts a fresh one for the current device (also the overlay's Retry).</summary>
	internal async Task ReconnectAsync()
	{
		if (disposed)
			return;
		Interlocked.Increment(ref connectGeneration);
		// Whatever the abandoned connection is waiting on (boot, install, launch) is cancelled, not left to finish.
		var abandoned = connectCancellation;
		connectCancellation = new CancellationTokenSource();
		try { abandoned.Cancel(); } catch (Exception) { }
		deviceSession.Reset();
		var old = client;
		client = null;
		sessionOpened = false;
		if (old != null)
		{
			old.Recovered -= OnClientRecovered;
			old.RecoveryFailed -= OnClientRecoveryFailed;
			await Task.Run(old.Dispose).ConfigureAwait(true);
		}

		Interlocked.Increment(ref version);
		connectTask = ConnectAsync(connectKind);
		await connectTask.ConfigureAwait(true);
		var text = Volatile.Read(ref lastLoadedText);
		if (text != null && client != null && !disposed)
		{
			SetStatus("Rendering…");
			_ = RenderAsync(text, Interlocked.Increment(ref version));
		}
	}

	/// <summary>Starts the iOS head in the chosen simulator and wraps it as a design client. Reports every step to the session.</summary>
	async Task<UnoDesignClient> StartIosDeviceClientAsync(Device device, int generation, CancellationToken cancellationToken)
	{
		// Only the current connection reports progress: an abandoned one must not move the shared session.
		void Step(DesignerSessionPhase phase)
		{
			if (generation == Volatile.Read(ref connectGeneration))
				deviceSession.Enter(phase);
		}

		Step(DesignerSessionPhase.Preflight);
		var bundle = LocateIosHostBundle();
		if (bundle == null)
			throw new InvalidOperationException("The iOS design host is not installed with this add-in (expected "
				+ IosHostFolder + "/" + IosHostBundle + "). Build WinUIXamlDesigner.UnoHost with -p:UnoDesignHostMobile=true.");
		var provider = DeviceService.Default.Providers.FirstOrDefault(p => p.Platform == DevicePlatform.IosSimulator);
		if (provider?.Unavailable is { } unavailable)
			throw new InvalidOperationException("iOS simulators are unavailable: " + unavailable);
		var udid = device.Id.StartsWith("ios:", StringComparison.Ordinal) ? device.Id["ios:".Length..] : device.Id;
		if (DeviceService.Default.Devices.All(d => d.Id != device.Id))
			throw new InvalidOperationException($"{device.Name} no longer exists on this machine; pick another device.");

		Step(DesignerSessionPhase.BootingDevice);
		await IosSimulatorHostProcess.PrepareAsync(udid, bundle, cancellationToken).ConfigureAwait(true);
		var bundleId = IosSimulatorHostProcess.ReadBundleId(bundle, "com.lextudio.opendevelop.unodesignhost");
		var host = new IosSimulatorHostProcess(bundle, udid, bundleId, "Uno");
		Step(DesignerSessionPhase.LaunchingHost);
		try
		{
			await host.StartConnectionAsync(cancellationToken).ConfigureAwait(true);
		}
		catch
		{
			host.Dispose();
			throw;
		}

		TrackDeviceHost(host, device, generation);
		Step(DesignerSessionPhase.Rendering);
		return UnoDesignClient.AdoptDeviceHost(host);
	}

	/// <summary>Starts the Android head in the chosen emulator (starting the emulator first when it is not running) and wraps it as
	/// a design client. Reports every step to the session.</summary>
	async Task<UnoDesignClient> StartAndroidDeviceClientAsync(Device device, int generation, CancellationToken cancellationToken)
	{
		void Step(DesignerSessionPhase phase)
		{
			if (generation == Volatile.Read(ref connectGeneration))
				deviceSession.Enter(phase);
		}

		Step(DesignerSessionPhase.Preflight);
		var apk = LocateAndroidHostApk();
		if (apk == null)
			throw new InvalidOperationException("The Android design host is not installed with this add-in (expected "
				+ AndroidHostFolder + "/" + AndroidHostApk + "). Build WinUIXamlDesigner.UnoHost with -p:UnoDesignHostMobile=true.");
		var provider = DeviceService.Default.Providers.FirstOrDefault(p => p.Platform == DevicePlatform.AndroidEmulator);
		if (provider == null)
			throw new InvalidOperationException("The Android Device Manager add-in is not loaded, so emulators cannot be started.");
		if (provider.Unavailable is { } unavailable)
			throw new InvalidOperationException("Android emulators are unavailable: " + unavailable);
		var adb = AndroidEmulatorHostProcess.LocateAdb()
			?? throw new InvalidOperationException("adb was not found. Install the Android SDK platform-tools or set ANDROID_HOME.");
		var avd = device.Id.StartsWith("android:", StringComparison.Ordinal) ? device.Id["android:".Length..] : device.Id;
		if (DeviceService.Default.Devices.All(d => d.Id != device.Id))
			throw new InvalidOperationException($"{device.Name} no longer exists on this machine; pick another device.");

		// An emulator that is not running is started, then waited for: adb lists it a while before it is usable.
		Step(DesignerSessionPhase.BootingDevice);
		var serial = await Task.Run(() => AndroidEmulatorHostProcess.FindSerialForAvd(adb, avd)).ConfigureAwait(true);
		if (serial == null)
		{
			if (provider is not IDeviceManagement management)
				throw new InvalidOperationException($"{device.Name} is not running and cannot be started from here.");
			var started = await management.BootAsync(device).ConfigureAwait(true);
			if (!started.Ok)
				throw new InvalidOperationException($"Could not start {device.Name}: {started.Error}");
		}

		var deadline = DateTime.UtcNow + EmulatorBootTimeout;
		while (true)
		{
			serial ??= await Task.Run(() => AndroidEmulatorHostProcess.FindSerialForAvd(adb, avd)).ConfigureAwait(true);
			if (serial != null && await Task.Run(() => AndroidEmulatorHostProcess.IsBootCompleted(adb, serial)).ConfigureAwait(true))
				break;
			cancellationToken.ThrowIfCancellationRequested();
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException($"{device.Name} did not finish starting within {EmulatorBootTimeout.TotalMinutes:0} minutes.");
			await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(true);
		}

		Step(DesignerSessionPhase.InstallingHost);
		await AndroidEmulatorHostProcess.InstallAsync(adb, serial, apk, cancellationToken).ConfigureAwait(true);
		var host = new AndroidEmulatorHostProcess(adb, serial, apk, AndroidPackage, "Uno");
		Step(DesignerSessionPhase.LaunchingHost);
		try
		{
			await host.StartConnectionAsync(cancellationToken).ConfigureAwait(true);
		}
		catch
		{
			host.Dispose();
			throw;
		}

		TrackDeviceHost(host, device, generation);
		Step(DesignerSessionPhase.Rendering);
		return UnoDesignClient.AdoptDeviceHost(host);
	}

	/// <summary>Remembers the live host for the current connection and marks the session Stale when it exits.</summary>
	void TrackDeviceHost(DesignerHostProcessClient host, Device device, int generation)
	{
		// Only the current connection owns the live-host slot, so an abandoned one cannot hide the real host's exit.
		if (generation == Volatile.Read(ref connectGeneration))
			currentDeviceHost = host;
		host.HostExited += (_, _) => dispatcher.BeginInvoke(() =>
		{
			if (!disposed && ReferenceEquals(currentDeviceHost, host))
			{
				deviceSession.Enter(DesignerSessionPhase.Stale);
				SetStatus($"The design host on {device.Name} stopped.");
			}
		});
	}

	static string LocateAndroidHostApk()
	{
		var directory = Path.GetDirectoryName(typeof(UnoDesignClient).Assembly.Location);
		if (string.IsNullOrEmpty(directory))
			return null;
		var candidate = Path.Combine(directory, AndroidHostFolder, AndroidHostApk);
		return File.Exists(candidate) ? candidate : null;
	}

	static string LocateIosHostBundle()
	{
		var directory = Path.GetDirectoryName(typeof(UnoDesignClient).Assembly.Location);
		if (string.IsNullOrEmpty(directory))
			return null;
		var candidate = Path.Combine(directory, IosHostFolder, IosHostBundle);
		return Directory.Exists(candidate) ? candidate : null;
	}

	/// <summary>A frame arrived: for a device preview the session is now Ready.</summary>
	void NoteDeviceFrame()
	{
		if (previewDevice.Platform != DevicePlatform.Host)
			deviceSession.Enter(DesignerSessionPhase.Ready);
	}

	// ---- Remembering the device per project ---------------------------------------------------

	string RememberedDeviceProperty()
	{
		if (string.IsNullOrEmpty(projectFile))
			return "";
		var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
			System.Text.Encoding.UTF8.GetBytes(projectFile.ToLowerInvariant())));
		return "WinUIDesigner.PreviewDevice." + hash[..32];
	}

	string ReadRememberedDeviceId()
	{
		var property = RememberedDeviceProperty();
		if (property.Length == 0)
			return null;
		try { return PropertyService.Get(property, "") is { Length: > 0 } id ? id : null; }
		catch (Exception) { return null; }
	}

	void RememberDeviceId(string id)
	{
		var property = RememberedDeviceProperty();
		if (property.Length == 0)
			return;
		try { PropertyService.Set(property, id); }
		catch (Exception) { /* a settings write must never break the preview */ }
	}

	void DisposeDevices()
	{
		DeviceService.Default.Changed -= OnSharedDevicesChanged;
		DevicePreferencesStore.Saved -= OnSharedDevicesChanged;
	}
}
