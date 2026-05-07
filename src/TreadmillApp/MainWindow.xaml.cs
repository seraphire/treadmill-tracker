using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfColor = System.Windows.Media.Color;
using TreadmillApp.Models;
using TreadmillApp.Services;
using TreadmillApp.Services.Strava;

namespace TreadmillApp;

public partial class MainWindow : Window
{
    private readonly TreadmillBleManager                    _ble      = new();
    private readonly AppDataService                         _appData  = new();
    private readonly StravaService                          _strava   = new();
    private readonly ObservableCollection<SessionViewModel> _sessions = new();
    private readonly List<TreadmillSession>                 _todaySessions = new();

    private SessionViewModel? _activeSessionVm;
    private SavedDevice?      _savedDevice;
    private TrayIconService?  _tray;
    private PauseToastWindow? _pauseToast;
    private string            _connectedDeviceName = "";

    public MainWindow()
    {
        InitializeComponent();
        SessionList.ItemsSource = _sessions;

        // ── Strava log ────────────────────────────────────────────────────────
        _strava.Log += (_, msg) => Dispatcher.Invoke(() => AppendLog(msg));

        // ── BLE events ────────────────────────────────────────────────────────
        // DeviceDiscovered is now consumed by SettingsWindow while it's open.
        _ble.StateChanged += (_, state) =>
            Dispatcher.Invoke(() => ApplyConnectionState(state));

        _ble.MetricsReceived += (_, metrics) =>
            Dispatcher.Invoke(() => UpdateMetrics(metrics));

        _ble.LogMessage += (_, msg) =>
            Dispatcher.Invoke(() => AppendLog(msg));

        _ble.SessionStarted += (_, session) =>
            Dispatcher.Invoke(() =>
            {
                _todaySessions.Add(session);
                _activeSessionVm = new SessionViewModel(session);
                _sessions.Add(_activeSessionVm);
                AppendLog($"Walk started at {session.StartTime:HH:mm:ss}");

                _tray?.SetWalking(session.StartTime.ToString("HH:mm"));
                ShowToast("Walk Started",
                          $"Session logging active — started at {session.StartTime:HH:mm:ss}");
            });

        _ble.SessionUpdated += (_, session) =>
            Dispatcher.Invoke(() =>
            {
                _activeSessionVm?.Refresh();
                UpdateDailyTotals();
                // Keep tray tooltip current
                _tray?.SetWalking($"{session.DistanceMeters} m · {session.Steps} steps");
            });

        _ble.SessionPaused += (_, session) =>
            Dispatcher.Invoke(() =>
            {
                _tray?.SetConnectedIdle(_connectedDeviceName);
                AppendLog("Walk paused.");
                ShowPauseToast();
            });

        _ble.SessionResumed += (_, session) =>
            Dispatcher.Invoke(() =>
            {
                _tray?.SetWalking($"{session.DistanceMeters} m · {session.Steps} steps");
                AppendLog("Walk resumed.");
                _pauseToast?.DismissNow();
                _pauseToast = null;
                ShowToast("Welcome back!",
                          $"Picking up at {session.DistanceMeters} m · {session.Steps} steps. Keep going!",
                          displayMs: 4000,
                          style: ToastStyle.Normal);
            });

        _ble.ConnectionLost += (_, _) =>
            Dispatcher.Invoke(() =>
            {
                var who = string.IsNullOrEmpty(_connectedDeviceName) ? "the treadmill" : _connectedDeviceName;
                ShowToast("Connection Lost",
                          $"Lost contact with {who}. Trying to reconnect…",
                          displayMs: 6000,
                          style: ToastStyle.LostConnection);
            });

        _ble.SystemResumed += (_, _) =>
            Dispatcher.Invoke(() => _ = ReconnectAfterWakeAsync());

        _ble.SessionCompleted += (_, session) =>
        {
            bool keep         = !ShouldDiscardWalk(session);
            bool sleepClosing = _ble.IsSystemSleeping;

            Dispatcher.Invoke(() =>
            {
                _pauseToast?.DismissNow();
                _pauseToast = null;

                if (!keep)
                {
                    AppendLog($"Walk discarded — {session.Steps} step(s), " +
                              $"{(int)session.Duration.TotalSeconds} sec (below thresholds).");
                    // Roll back state added when SessionStarted fired
                    _todaySessions.Remove(session);
                    if (_activeSessionVm != null) _sessions.Remove(_activeSessionVm);
                    _activeSessionVm = null;
                    UpdateDailyTotals();
                    _tray?.SetConnectedIdle(_connectedDeviceName);
                    return;
                }

                _activeSessionVm?.Refresh();
                _activeSessionVm = null;
                UpdateDailyTotals();
                CheckGoalHit();
                _appData.AppendSession(session);
                AppendLog($"Walk complete — {session.DistanceMeters} m · {session.Steps} steps · " +
                          $"{session.Calories} kcal · {session.Duration:mm\\:ss}");

                _tray?.SetConnectedIdle(_connectedDeviceName);

                bool isFirstToday = session.StartTime.Date == DateTime.Today
                                 && _todaySessions.Count(s => s.StartTime.Date == DateTime.Today) == 1;

                var activityType = _appData.ClassifyWalk(session.AverageSpeedKmh);
                var verbCapitalized = activityType switch
                {
                    WalkActivityType.Run => "Run",
                    WalkActivityType.Jog => "Jog",
                    _                    => "Walk",
                };

                // Skip the celebratory toast when the system is going to sleep
                // — the user is walking away from the computer, not finishing
                // a workout. The walk is still saved locally; Strava upload
                // will retry on next launch.
                if (!sleepClosing)
                {
                    ShowToast(
                        isFirstToday ? "Way to start!" : $"{verbCapitalized} Complete!",
                        $"{session.DistanceMeters} m  ·  {session.Steps} steps  ·  " +
                        $"{session.Calories} kcal  ·  {session.Duration:mm\\:ss}",
                        displayMs: 7000,
                        style: isFirstToday ? ToastStyle.FirstRun : ToastStyle.Finish);
                }
            });

            // Discarded walks are never uploaded; sleep-closed walks defer.
            if (keep && !sleepClosing)
                _ = HandleStravaForSessionAsync(session);
        };
    }

    /// <summary>
    /// True if the walk falls below the configured minimum thresholds and
    /// should be silently dropped (no local save, no Strava upload).
    /// Either zero/below-min steps OR sub-min duration triggers a discard.
    /// </summary>
    private bool ShouldDiscardWalk(TreadmillSession s)
    {
        return s.Steps < _appData.MinWalkSteps
            || s.Duration.TotalSeconds < _appData.MinWalkSeconds;
    }

    private async Task HandleStravaForSessionAsync(TreadmillSession session)
    {
        if (_strava.IsConnected)
        {
            var record = SessionRecord.FromSession(session);
            var type   = _appData.ClassifyWalk(session.AverageSpeedKmh);
            var result = await _strava.UploadAsync(record, type);
            if (result.Success && result.ActivityId.HasValue)
            {
                _appData.MarkUploaded(record.StartTime, result.ActivityId.Value, result.ActivityUrl);
                Dispatcher.Invoke(() => ShowUploadedToast(result.ActivityUrl));
            }
            else if (result.Permanent)
            {
                _appData.MarkUploadSkipped(record.StartTime);
            }
            else
            {
                // Transient (network/5xx) — surface it so the user knows.
                Dispatcher.Invoke(() =>
                    ShowToast("Couldn't Upload",
                              "Walk saved locally — we'll try again at the next launch.",
                              displayMs: 6000,
                              style: ToastStyle.LostConnection));
            }
        }
        else if (!_appData.HasShownStravaPrompt)
        {
            _appData.MarkStravaPromptShown();
            Dispatcher.Invoke(() =>
            {
                ShowToast("Connect to Strava?",
                          "Click here to link your account so future walks (and this one) push automatically.",
                          displayMs: 12000,
                          style: ToastStyle.Setup,
                          onClick: OpenSettings);
            });
        }
    }

    // =========================================================================
    // Window lifetime — minimize and close go to tray
    // =========================================================================

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var iconPath = Path.Combine(dir, "images", "connected.ico");
        if (File.Exists(iconPath))
            Icon = new BitmapImage(new Uri(iconPath));

        _tray = new TrayIconService(this, ExitApplication);
        ApplyPauseTolerance();
        LoadSavedDevice();
        LoadTodaySessions();
        MaybeShowGhostNag();
        _ = RetryUnuploadedSessionsAsync();
    }

    /// <summary>
    /// Once-per-day gentle nag: if the user has a saved device and they were
    /// active in the past week but haven't logged a walk today, drop a
    /// "ghosted" toast so the treadmill politely waves at them.
    /// </summary>
    private void MaybeShowGhostNag()
    {
        if (_savedDevice == null) return;
        if (_appData.LastGhostedNagDate?.Date == DateTime.Today) return;
        if (_todaySessions.Any(s => s.StartTime.Date == DateTime.Today)) return;

        var allSessions = _appData.LoadSessions();
        var sevenDaysAgo = DateTime.Today.AddDays(-7);
        bool walkedRecently = allSessions.Any(s =>
            s.StartTime.Date >= sevenDaysAgo && s.StartTime.Date < DateTime.Today);
        if (!walkedRecently) return;

        _appData.MarkGhostedNagShown(DateTime.Today);
        ShowToast("Ready when you are.",
                  "No walk logged today yet — your treadmill is patiently waiting.",
                  displayMs: 9000,
                  style: ToastStyle.Ghosted);
    }

    private void ApplyPauseTolerance()
    {
        _ble.PauseTolerance = TimeSpan.FromSeconds(_appData.PauseToleranceSeconds);
    }

    private async Task RetryUnuploadedSessionsAsync()
    {
        if (!_strava.IsConnected) return;
        var pending = _appData.LoadUnuploadedSessions();
        if (pending.Count == 0) return;

        AppendLog($"Retrying {pending.Count} session(s) not yet uploaded to Strava...");
        int succeeded   = 0;   // newly uploaded this sweep
        int confirmed   = 0;   // Strava already had it (409) — likely a prior
                               // attempt timed out on us but actually succeeded
        foreach (var s in pending)
        {
            var type   = _appData.ClassifyWalk(s.AverageSpeedKmh);
            var result = await _strava.UploadAsync(s, type);
            if (result.Success && result.ActivityId.HasValue)
            {
                _appData.MarkUploaded(s.StartTime, result.ActivityId.Value, result.ActivityUrl);
                succeeded++;
            }
            else if (result.Permanent)
            {
                _appData.MarkUploadSkipped(s.StartTime);
                if (result.Error == "duplicate") confirmed++;
            }
            else
            {
                // Transient failure (network, 5xx) — stop the sweep so we don't
                // hammer Strava. Will retry at next startup.
                break;
            }
        }

        int total = succeeded + confirmed;
        if (total > 0)
        {
            string title = total == 1 ? "Walk Uploaded" : $"{total} Walks Uploaded";
            string msg;
            if (succeeded > 0 && confirmed > 0)
                msg = $"{succeeded} just pushed, {confirmed} already on Strava. Caught up.";
            else if (succeeded == 1)
                msg = "Your previous walk was just pushed to Strava.";
            else if (succeeded > 1)
                msg = $"Caught up — {succeeded} pending walks just made it to Strava.";
            else if (confirmed == 1)
                msg = "Confirmed on Strava (an earlier upload landed even though we never got the response).";
            else
                msg = $"All {confirmed} previously-pending walks confirmed on Strava.";

            Dispatcher.Invoke(() =>
                ShowToast(title, msg, displayMs: 6000, style: ToastStyle.Uploaded));
        }
    }

    private void ShowUploadedToast(string? activityUrl)
    {
        ShowToast("Walk Uploaded",
                  "Pushed to Strava. Click here to view it.",
                  displayMs: 6000,
                  style: ToastStyle.Uploaded,
                  onClick: string.IsNullOrEmpty(activityUrl) ? null : () => OpenInBrowser(activityUrl));
    }

    private static void OpenInBrowser(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* best-effort; ignore */ }
    }

    private bool _exiting;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Minimize button → hide to system tray (the walking animation keeps
        // running, toasts still pop, click the tray icon to restore).
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // X button → really close. Use the Minimize button if you want tray.
        // Re-entry guard: Application.Current.Shutdown() will trigger
        // OnClosing again on its way out.
        if (_exiting) return;
        ExitApplication();
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        // Save any walk currently in progress so it isn't lost on shutdown;
        // Strava upload kicks off async and will retry on next launch if
        // it can't finish before the process exits.
        try { _ble.FinalizeSessionNow(); } catch { /* best-effort */ }

        _tray?.Dispose();
        _ble.Dispose();
        _strava.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void Window_Closing(object sender, CancelEventArgs e) { /* handled by OnClosing */ }

    // =========================================================================
    // Menu handlers
    // =========================================================================

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void FindDevice_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var settings = new SettingsWindow(_ble, _appData, _strava) { Owner = this };
        settings.ShowDialog();
        // Reload saved-device state in case the user forgot or replaced it.
        LoadSavedDevice();
        ApplyConnectionState(_ble.State);
        ApplyPauseTolerance();
        // If they just connected to Strava, sweep any pending uploads.
        _ = RetryUnuploadedSessionsAsync();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            this,
            "Treadmill Tracker\n\n" +
            "Tracks walking sessions on the FS-18F451 treadmill via BLE\n" +
            "for The Conqueror challenge.",
            "About Treadmill Tracker",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    // =========================================================================
    // Startup helpers
    // =========================================================================

    private void LoadSavedDevice()
    {
        _savedDevice = _appData.LoadLastDevice();
        if (_savedDevice != null)
        {
            LastDeviceHint.Text          = $"Last: {_savedDevice.Name}  ({_savedDevice.Mac})";
            QuickConnectButton.IsEnabled = _ble.State == ConnectionState.Disconnected;
        }
        else
        {
            LastDeviceHint.Text          = "";
            QuickConnectButton.IsEnabled = false;
        }
    }

    private void LoadTodaySessions()
    {
        var today = _appData.LoadSessions()
                            .Where(r => r.StartTime.Date == DateTime.Today)
                            .ToList();

        foreach (var record in today)
        {
            var session = TreadmillSession.FromRecord(record);
            _todaySessions.Add(session);
            _sessions.Add(new SessionViewModel(session));
        }

        UpdateDailyTotals();
    }

    // =========================================================================
    // Button handlers
    // =========================================================================

    private void Disconnect_Click(object sender, RoutedEventArgs e) => _ = _ble.DisconnectAsync();

    private void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_savedDevice == null) return;
        _ = _ble.ConnectAsync(BuildBleDeviceFromSaved(_savedDevice));
    }

    private static BleDevice BuildBleDeviceFromSaved(SavedDevice saved) => new()
    {
        BluetoothAddress = saved.Address,
        Name             = saved.Name,
        Address          = saved.Mac,
        DeviceId         = saved.Address.ToString(),
        DeviceType       = "Treadmill",
    };

    /// <summary>
    /// Re-establishes the BLE connection after the system wakes from sleep.
    /// Uses a longer retry window than the standard auto-reconnect because
    /// Windows' BLE stack can take 10–30 seconds to come back up post-wake.
    /// </summary>
    private async Task ReconnectAfterWakeAsync()
    {
        if (_savedDevice == null) return;
        if (_ble.State == ConnectionState.Connected) return;

        AppendLog("Reconnecting after system resume...");
        ShowToast("Welcome back!",
                  "Reconnecting to your treadmill…",
                  displayMs: 5000,
                  style: ToastStyle.Normal);

        // Let Windows' BLE stack settle before the first attempt.
        await Task.Delay(TimeSpan.FromSeconds(5));

        const int maxAttempts = 6;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (_ble.State == ConnectionState.Connected)
            {
                ShowToast("Reconnected",
                          $"Back online with {_savedDevice.Name}.",
                          displayMs: 5000,
                          style: ToastStyle.ThumbsUp);
                return;
            }

            try { await _ble.ConnectAsync(BuildBleDeviceFromSaved(_savedDevice)); }
            catch (Exception ex) { AppendLog($"Resume-reconnect attempt {attempt} failed: {ex.Message}"); }

            if (_ble.State == ConnectionState.Connected)
            {
                ShowToast("Reconnected",
                          $"Back online with {_savedDevice.Name}.",
                          displayMs: 5000,
                          style: ToastStyle.ThumbsUp);
                return;
            }

            if (attempt < maxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(5));
        }

        AppendLog("Could not reconnect after wake. Click Quick Connect when ready.");
        ShowToast("Couldn't Reconnect",
                  "BLE didn't come back. Click Quick Connect when ready.",
                  displayMs: 8000,
                  style: ToastStyle.LostConnection);
    }

    // =========================================================================
    // State & metrics
    // =========================================================================

    private void ApplyConnectionState(ConnectionState state)
    {
        DisconnectButton.IsEnabled   = state == ConnectionState.Connected;
        QuickConnectButton.IsEnabled = state == ConnectionState.Disconnected && _savedDevice != null;

        switch (state)
        {
            case ConnectionState.Disconnected:
                StatusDot.Fill  = new SolidColorBrush(WpfColor.FromRgb(170, 170, 170));
                StatusText.Text = "Disconnected";
                _tray?.SetDisconnected();
                break;

            case ConnectionState.Scanning:
                StatusDot.Fill  = new SolidColorBrush(WpfColor.FromRgb(234, 179, 8));
                StatusText.Text = "Scanning...";
                break;

            case ConnectionState.Connecting:
                StatusDot.Fill  = new SolidColorBrush(WpfColor.FromRgb(234, 179, 8));
                var target      = _ble.LastConnectedDevice?.Name ?? _savedDevice?.Name ?? "device";
                StatusText.Text = $"Connecting to {target}...";
                break;

            case ConnectionState.Connected:
                StatusDot.Fill  = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94));
                if (_ble.LastConnectedDevice is { } dev)
                {
                    _connectedDeviceName = dev.Name;
                    StatusText.Text = $"Connected to {dev.Name}";
                    _savedDevice    = new SavedDevice(dev.BluetoothAddress, dev.Name, dev.Address);
                    _appData.SaveLastDevice(dev.BluetoothAddress, dev.Name, dev.Address);
                    LastDeviceHint.Text = $"Last: {dev.Name}  ({dev.Address})";
                    _tray?.SetConnectedIdle(dev.Name);
                }
                break;
        }
    }

    private void UpdateMetrics(TreadmillMetrics metrics)
    {
        SpeedValue.Text    = metrics.CurrentSpeed.HasValue   ? $"{metrics.CurrentSpeed.Value:F1}"  : "--";
        DistanceValue.Text = metrics.TotalDistance.HasValue  ? $"{metrics.TotalDistance.Value}"    : "--";
        StepsValue.Text    = metrics.StepCount.HasValue      ? $"{metrics.StepCount.Value}"        : "--";
        CaloriesValue.Text = metrics.ExpendedEnergy.HasValue ? $"{metrics.ExpendedEnergy.Value}"   : "--";
        ElapsedValue.Text  = metrics.ElapsedSeconds.HasValue
            ? TimeSpan.FromSeconds(metrics.ElapsedSeconds.Value).ToString(@"mm\:ss")
            : "--";
        StepsLabel.Text    = "STEPS";
    }

    private void UpdateDailyTotals()
    {
        var today = _todaySessions.Where(s => s.StartTime.Date == DateTime.Today).ToList();

        uint     totalDist  = (uint)today.Sum(s => (long)s.DistanceMeters);
        int      totalSteps = today.Sum(s => s.Steps);
        uint     totalCal   = (uint)today.Sum(s => (long)s.Calories);
        TimeSpan totalTime  = today.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration);

        var distGoal  = _appData.DailyDistanceMetersGoal;
        var stepsGoal = _appData.DailyStepsGoal;

        DailyDistanceText.Text = distGoal > 0
            ? $"{totalDist / 1000.0:F2} / {distGoal / 1000.0:0.##} km"
            : $"{totalDist / 1000.0:F2} km";
        DailyStepsText.Text    = stepsGoal > 0
            ? $"{totalSteps} / {stepsGoal}"
            : totalSteps.ToString();
        DailyCaloriesText.Text = totalCal.ToString();
        DailyTimeText.Text     = totalTime.TotalMinutes >= 1 ? $"{(int)totalTime.TotalMinutes} min" : "< 1 min";

        var todayScore = distGoal > 0
            ? $"{totalDist / 1000.0:F2} km today  ({(int)Math.Min(100, Math.Round(totalDist * 100.0 / distGoal))}% of goal)"
            : $"{totalDist / 1000.0:F2} km today";

        var streak = _appData.ComputeStreakDays();
        DailyScoreText.Text = streak > 1
            ? $"🔥 {streak} day streak  ·  {todayScore}"
            : todayScore;
    }

    /// <summary>
    /// Fires a celebration toast the first time the user crosses either daily
    /// goal in a calendar day. Uses winning.png. No-op if neither goal is set
    /// or the goal-hit toast has already shown today.
    /// </summary>
    private void CheckGoalHit()
    {
        if (_appData.LastGoalHitDate?.Date == DateTime.Today) return;

        var distGoal  = _appData.DailyDistanceMetersGoal;
        var stepsGoal = _appData.DailyStepsGoal;
        if (distGoal <= 0 && stepsGoal <= 0) return;

        var today = _todaySessions.Where(s => s.StartTime.Date == DateTime.Today).ToList();
        uint totalDist  = (uint)today.Sum(s => (long)s.DistanceMeters);
        int  totalSteps = today.Sum(s => s.Steps);

        bool distHit  = distGoal  > 0 && totalDist  >= distGoal;
        bool stepsHit = stepsGoal > 0 && totalSteps >= stepsGoal;
        if (!distHit && !stepsHit) return;

        _appData.MarkGoalHit(DateTime.Today);

        string what = (distHit, stepsHit) switch
        {
            (true, true)  => "distance and steps",
            (true, false) => "distance",
            _             => "steps",
        };
        ShowToast("Daily Goal Hit!",
                  $"You crushed your {what} goal — {totalDist / 1000.0:F2} km · {totalSteps} steps.",
                  displayMs: 8000,
                  style: ToastStyle.Winning);
    }

    private void ResetLiveMetrics()
    {
        SpeedValue.Text = DistanceValue.Text = StepsValue.Text = CaloriesValue.Text = ElapsedValue.Text = "--";
    }

    private void AppendLog(string msg)
    {
        LogList.Items.Add(msg);
        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private static void ShowToast(string title, string message, int displayMs = 5000,
                                  ToastStyle style = ToastStyle.Normal,
                                  Action? onClick = null)
    {
        var toast = new ToastWindow(title, message, displayMs, style, onClick);
        toast.Show();
    }

    private void ShowPauseToast()
    {
        // Replace any prior pause toast (shouldn't happen, but be safe).
        _pauseToast?.DismissNow();

        var expiresAt = DateTime.UtcNow + _ble.PauseTolerance;
        _pauseToast = new PauseToastWindow(expiresAt);
        _pauseToast.FinishRequested += () => _ble.FinalizeSessionNow();
        _pauseToast.Closed          += (_, _) => _pauseToast = null;
        _pauseToast.Show();
    }

    // =========================================================================
    // Session view model
    // =========================================================================

    private sealed class SessionViewModel : INotifyPropertyChanged
    {
        private readonly TreadmillSession _s;
        public SessionViewModel(TreadmillSession s) => _s = s;

        public string StartDisplay    => _s.StartTime.ToString("HH:mm");
        public string DurationDisplay => _s.Duration.ToString(@"mm\:ss");
        public string AvgSpeedDisplay => _s.AverageSpeedKmh > 0 ? $"{_s.AverageSpeedKmh:F1}" : "--";
        public string StepsDisplay    => _s.Steps > 0 ? _s.Steps.ToString() : "--";
        public string DistanceDisplay => _s.DistanceMeters > 0 ? $"{_s.DistanceMeters} m" : "--";
        public string StatusDisplay   => _s.IsActive ? "● Active" : "✓ Done";

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
