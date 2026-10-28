using System.Diagnostics;
using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Cost;
using AgentSpeed.Linux.Platform;
using AgentSpeed.Linux.UI;
using AgentSpeed.Linux.Services;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Reporting;
using AgentSpeed.Services;
using AgentSpeed.Traffic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using UiDispatcher = AgentSpeed.Linux.Platform.Dispatcher;

namespace AgentSpeed.Linux;

internal sealed class LinuxApp : Application
{
    private IClassicDesktopStyleApplicationLifetime _desktop = null!;
    private FileStream? _instance;
    private TrayIcon? _tray;
    private TopBarBridge? _bridge;
    private LinuxPowerEvents? _power;
    private readonly Avalonia.Threading.DispatcherTimer _maintenance = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTimeOffset _nextUpdateCheck;
    private readonly Dictionary<string, OrbWindow> _orbs = [];
    private bool _syncingOrbs;
    private bool _repositionOrbsRequested;
    public readonly Stopwatch Clock = Stopwatch.StartNew();
    public SettingsService Settings { get; private set; } = null!;
    public Localizer Loc { get; private set; } = null!;
    public SpeedHub Speed { get; private set; } = null!;
    public HistoryHub History { get; private set; } = null!;
    public HistoryService HistoryService { get; private set; } = null!;
    public LiveMonitor Monitor { get; private set; } = null!;
    public TrafficService Traffic { get; private set; } = null!;
    public CostService Cost { get; private set; } = null!;
    public ReportController Report { get; private set; } = null!;
    public LinuxUpdater Updater { get; private set; } = null!;
    public MainWindow Main { get; private set; } = null!;
    public bool Quitting { get; private set; }
    private bool _restart;
    public bool Preview { get; private set; }
    internal static string BuildVersion => System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(LinuxApp).Assembly)?.InformationalVersion.Split('+')[0]
        ?? typeof(LinuxApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string Version => BuildVersion;
    public event Action? Changed;

    internal OrbLayoutResult PlaceNewOrb(OrbsDocument document, string newId, string? fromId)
    {
        var displays = LinuxOrbDisplays();
        var centers = _orbs.Values
            .Select(window => (window.Definition.Id, Center: window.LiveCenter))
            .ToDictionary(pair => pair.Id, pair => pair.Center, StringComparer.Ordinal);
        return LinuxOrbLayout.PlaceNew(document, displays, centers, newId, fromId);
    }

    internal IReadOnlyList<OrbDisplay> LinuxOrbDisplays()
    {
        var screens = Main?.Screens.All ?? Array.Empty<Screen>();
        var primary = Main?.Screens.Primary;
        return LinuxOrbLayout.Displays(screens, primary);
    }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;
        LinuxPaths.EnsurePrivate(LinuxPaths.Config);
        LinuxPaths.EnsurePrivate(LinuxPaths.Data);
        LinuxPaths.EnsurePrivate(LinuxPaths.Cache);
        try
        {
            _instance = new FileStream(Path.Combine(LinuxPaths.Cache, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            await TopBarBridge.ActivateExistingAsync("dashboard");
            desktop.Shutdown(); return;
        }
        Preview = desktop.Args?.Contains("--preview") == true || desktop.Args?.Contains("--smoke-test") == true;
        var dispatcher = new UiDispatcher();
        Settings = new SettingsService(dispatcher); Settings.Load();
        ApplyAppearance();
        if (Preview)
        {
            Settings.MakeEphemeral();
            Settings.Update(s => s with { NetworkEnabled = false, Report = s.Report with { Enabled = false },
                Orbs = new AgentSpeed.Presentation.Settings.Orbs.OrbsDocument(
                    AgentSpeed.Presentation.Orb.OrbStyles.All.Select(style => new AgentSpeed.Presentation.Settings.Orbs.OrbDefinition(style.ToString()) { Style = style })) });
        }
        Loc = new Localizer(Localizer.LoadEmbeddedTable(), LanguageResolver.Resolve(Settings.Current.Language, CultureInfo.CurrentUICulture));
        Speed = new SpeedHub(new UiSynchronizationContext());
        History = new HistoryHub(new UiSynchronizationContext());
        Cost = new CostService(dispatcher, Settings, Loc, Version, LinuxPaths.History);
        Cost.Prices.TableAccepted += (_, _) => History.Refresh();
        Monitor = new LiveMonitor(Speed, Settings.Current.ToMonitorConfiguration(), Cost.Book);
        HistoryService = new HistoryService(History, Settings.Current.ToMonitorConfiguration(), LinuxPaths.History, Cost.Book);
        Traffic = new TrafficService(Speed);
        Report = new ReportController(dispatcher, Settings, Speed, Traffic, Cost.Book, Version);
        Updater = new LinuxUpdater(this);
        _nextUpdateCheck = AgentSpeed.Presentation.Updates.UpdateSchedule.FirstCheck(DateTimeOffset.UtcNow, Settings.Current.LastUpdateCheck);
        Updater.Changed += () =>
        {
            if (Updater.Available is { } release) Main?.ShowUpdate(release.DisplayVersion);
        };
        Report.Activate();
        Speed.LivePublished += (_, s) => HistoryService.ObserveLive(s);
        Speed.SnapshotChanged += (_, _) => { RefreshTray(); Changed?.Invoke(); };
        Traffic.Hub.SnapshotChanged += (_, _) => Changed?.Invoke();
        Cost.Changed += (_, _) => Changed?.Invoke();
        Settings.Changed += (_, e) =>
        {
            Loc.LanguageCode = LanguageResolver.Resolve(e.Current.Language, CultureInfo.CurrentUICulture);
            ApplyAppearance();
            Monitor.Update(e.Current.ToMonitorConfiguration()); HistoryService.Update(e.Current.ToMonitorConfiguration());
            Cost.OnSettingsChanged(e.Previous, e.Current);
            Traffic.Monitor.SetEnabled(e.Current.Traffic.Enabled);
            if (!e.Current.NetworkEnabled) Updater.Cancel();
            if (e.Current.NetworkEnabled != e.Previous.NetworkEnabled || e.Current.AutoCheckUpdates != e.Previous.AutoCheckUpdates)
                _nextUpdateCheck = AgentSpeed.Presentation.Updates.UpdateSchedule.FirstCheck(DateTimeOffset.UtcNow, e.Current.LastUpdateCheck);
            SyncOrbs(); Changed?.Invoke();
        };
        Main = new MainWindow(this); desktop.MainWindow = Main;
        ApplyAppearance();
        if (desktop.Args?.Contains("--background") != true) Main.Show();
        CreateTray(); SyncOrbs();
        if (!Preview)
        {
            Monitor.Start(); HistoryService.Start(); Traffic.Monitor.Start(Settings.Current.Traffic.Enabled); Cost.Start();
        }
        else StartPreview();
        _bridge = new TopBarBridge(this);
        try { await _bridge.StartAsync(); }
        catch (Exception ex) { AgentSpeed.App.AppLog.Warn("GNOME top bar bridge is unavailable.", ex); }
        _power = new LinuxPowerEvents(this);
        if (!Preview) await _power.StartAsync();
        Main.PropertyChanged += (_, e) => { if (e.Property == TopLevel.ActualThemeVariantProperty) SyncOrbs(); };
        _maintenance.Tick += async (_, _) =>
        {
            if (LinuxAccessibility.Refresh()) { ApplyAppearance(); SyncOrbs(); }
            if (!Preview && Version != "0.0.0" && Settings.Current.NetworkEnabled && Settings.Current.AutoCheckUpdates
                && DateTimeOffset.UtcNow >= _nextUpdateCheck)
            {
                _nextUpdateCheck = AgentSpeed.Presentation.Updates.UpdateSchedule.NextCheck(DateTimeOffset.UtcNow);
                await Updater.CheckAsync(manual: false);
            }
        };
        _maintenance.Start();
        desktop.Exit += (_, _) =>
        {
            DisposeServices();
            if (_restart) Process.Start(new ProcessStartInfo("/usr/bin/agent-speed") { UseShellExecute = false });
        };
        base.OnFrameworkInitializationCompleted();
        if (desktop.Args?.Contains("--smoke-test") == true)
        {
            await System.Threading.Tasks.Task.Delay(1800);
            try
            {
                var first = _orbs.Values.First();
                X11Windows.SetClickThrough(first, true);
                if (X11Windows.InputRectangleCount(first) != 0) throw new InvalidOperationException("X11 click-through was not applied.");
                X11Windows.SetClickThrough(first, false, first.Definition.Size);
                if (X11Windows.InputRectangleCount(first) <= 0) throw new InvalidOperationException("X11 mouse input was not restored.");
                var center = (int)(first.Definition.Size * .75 * first.RenderScaling);
                if (X11Windows.InputContains(first, 0, 0) || !X11Windows.InputContains(first, center, center))
                    throw new InvalidOperationException("The orb input shape blocks transparent pixels or misses its visible center.");
                await Main.CheckSurfacesAsync();
                foreach (var orb in _orbs.Values)
                {
                    var expected = RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Light
                        ? AgentSpeed.Presentation.Colors.OrbAppearance.Light : AgentSpeed.Presentation.Colors.OrbAppearance.Dark;
                    if (Main.RequestedThemeVariant != RequestedThemeVariant) throw new InvalidOperationException("Main window theme is out of sync.");
                    if (orb.Appearance != expected) throw new InvalidOperationException("The orb did not follow the actual desktop window theme.");
                    orb.SavePreview(Path.Combine(LinuxPaths.Cache, $"smoke-{orb.Definition.Style}.png"));
                    orb.SavePreview(Path.Combine(LinuxPaths.Cache, $"smoke-{orb.Definition.Style}-cpu.png"), software: true);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"UI smoke test failed: {ex}"); Environment.ExitCode = 1; }
            finally { Quit(); }
        }
    }
    public void SyncOrbs(bool repositionExisting = false)
    {
        if (_syncingOrbs)
        {
            _repositionOrbsRequested |= repositionExisting;
            return;
        }

        _syncingOrbs = true;
        _repositionOrbsRequested = repositionExisting;
        try
        {
            var repeat = false;
            do
            {
                repeat = false;
                var reposition = _repositionOrbsRequested;
                _repositionOrbsRequested = false;
                var settings = Settings.Current;
                var document = settings.Orbs;
                var screens = Main?.Screens.All ?? Array.Empty<Screen>();
                var layout = LinuxOrbLayout.Resolve(document, LinuxOrbDisplays());
                if (layout.Document != document)
                {
                    _repositionOrbsRequested |= reposition;
                    Settings.Update(s => s with { Orbs = layout.Document });
                    repeat = true;
                    continue;
                }

                foreach (var (id, window) in _orbs.ToArray())
                    if (!settings.ShowOrbs || document.Find(id) is not { Visible: true })
                    {
                        _orbs.Remove(id);
                        LinuxOrbPositionLifecycle.Close(document.Find(id) is not null, window.FlushPosition, window.Close);
                    }
                if (settings.ShowOrbs)
                    foreach (var definition in document.Orbs.Where(o => o.Visible))
                    {
                        if (!layout.Locations.TryGetValue(definition.Id, out var location)) continue;
                        var screen = screens.FirstOrDefault(s => LinuxOrbLayout.Key(s) == location.Display)
                            ?? Main?.Screens.Primary ?? screens.FirstOrDefault();
                        if (screen is null) continue;
                        if (!_orbs.TryGetValue(definition.Id, out var window))
                        {
                            window = new OrbWindow(this, definition);
                            _orbs[definition.Id] = window;
                            window.PlaceAt(location, screen, layout.Arrangement);
                            window.Show();
                        }
                        else
                        {
                            var resized = window.Configure(definition);
                            if (reposition || resized) window.PlaceAt(location, screen, layout.Arrangement);
                        }
                    }
                Monitor.SetSubsets(document.Orbs.Select(o => o.TrackedSources).OfType<SourceSet>().Distinct().ToArray());
            } while (repeat || _repositionOrbsRequested);
        }
        finally { _syncingOrbs = false; }
    }
    private void ApplyAppearance()
    {
        RequestedThemeVariant = Settings.Current.Appearance switch
        {
            AppearanceMode.Light => Avalonia.Styling.ThemeVariant.Light,
            AppearanceMode.Dark => Avalonia.Styling.ThemeVariant.Dark,
            _ => LinuxAccessibility.PrefersDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
        };
        if (Main is not null) Main.RequestedThemeVariant = RequestedThemeVariant;
    }
    public void Activate(string action)
    {
        if (action == "quit") { Quit(); return; }
        if (action == "toggle-orbs") { Settings.Update(s => s.WithShowOrbs(!s.ShowOrbs)); return; }
        if (action == "restore-input")
        { Settings.Update(s => s with { Orbs = s.Orbs.WithOrbs(s.Orbs.Orbs.Select(o => o with { ClickThrough = false })) }); return; }
        Main.Show(); Main.Activate();
        if (Main.WindowState == WindowState.Minimized) Main.WindowState = WindowState.Normal;
        X11Windows.Activate(Main);
        if (action is "dashboard" or "settings" or "history") Main.SelectPage(action);
    }
    private void CreateTray()
    {
        _tray = new TrayIcon { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://agent-speed/Assets/icon.png"))), ToolTipText = "Agent Speed" };
        var menu = new NativeMenu();
        foreach (var (label, action) in new[] { ("Agent Speed", "dashboard"), ("Usage history", "history"), ("Settings", "settings"),
                     ("Show / hide orbs", "toggle-orbs"), ("Restore mouse input", "restore-input"), ("Quit", "quit") })
        { var item = new NativeMenuItem(label); item.Click += (_, _) => Activate(action); menu.Add(item); }
        _tray.Menu = menu; _tray.Clicked += (_, _) => Activate("dashboard"); _tray.IsVisible = true;
        TrayIcon.SetIcons(this, [_tray]);
    }
    private void RefreshTray()
    {
        if (_tray is not null) _tray.ToolTipText = $"Agent Speed · {Speed.Snapshot.Live.ValueFor(Settings.Current.Metric):0.0} tok/s";
    }
    private Avalonia.Threading.DispatcherTimer? _previewTimer;

    /// <summary>UI smoke only: freeze the synthetic preview clock so a screenshot does not race a timer tick.</summary>
    internal void PausePreviewForSmoke() => _previewTimer?.Stop();

    private void StartPreview()
    {
        History.Publish(HistoryState.Ready(DateTimeOffset.Now), PreviewHistory.Days());
        Speed.BeginPreview();
        _previewTimer = new(Avalonia.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(500) };
        void Publish()
        {
            var value = 84 + 38 * Math.Sin(Clock.Elapsed.TotalSeconds * .5);
            var now = DateTimeOffset.Now;
            Speed.PublishPreview(SpeedSnapshot.Empty with
            {
                Timestamp = now, State = MonitorState.Running, Live = new(value, value * 5), PeakToday = new(184, 920),
                Today = new(132, 1_480_000, 1_220_000, 0, 87_400), GeneratingCount = 1,
                Sources = [new(SourceKind.Codex, true, "preview", true, 6, new(value, value * 5), default, 1)],
                Agents = [new("preview", SourceKind.Codex, "Preview project", "gpt-6.1-sol", AgentPhase.Generating, false, new(value, value * 5), value, now)],
                History = Enumerable.Range(0, 120).Select(i => new HistoryPoint(now.AddSeconds(i - 119), new(84 + 38 * Math.Sin(i * .15), 0), default)).ToArray(),
            });
        }
        Publish();
        _previewTimer.Tick += (_, _) => Publish();
        _previewTimer.Start();
    }
    public void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        FlushOrbPositions();
        Settings.Flush();
        Report.PrepareToQuit(() => _desktop.Shutdown(Environment.ExitCode));
    }
    internal void FlushOrbPositions()
    {
        foreach (var orb in _orbs.Values.ToArray()) orb.FlushPosition();
    }
    public void Restart() { _restart = true; Quit(); }
    private void DisposeServices()
    {
        _previewTimer?.Stop(); _maintenance.Stop(); _power?.Dispose();
        foreach (var orb in _orbs.Values) orb.Close();
        _bridge?.Dispose(); _tray?.Dispose();
        Updater.Dispose(); Report.Dispose(); Monitor.Dispose(); HistoryService.Dispose(); Traffic.Dispose(); Cost.Dispose(); Settings.Dispose(); _instance?.Dispose();
    }
}
