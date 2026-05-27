using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TreadmillApp.Models;
using TreadmillApp.Services;
using TreadmillApp.Services.Strava;

namespace TreadmillApp;

public partial class SettingsWindow : Window
{
    private readonly TreadmillBleManager             _ble;
    private readonly AppDataService                  _appData;
    private readonly StravaService                   _strava;
    private readonly ObservableCollection<BleDevice> _devices = new();
    private          bool                            _loading;

    public SettingsWindow(TreadmillBleManager ble, AppDataService appData, StravaService strava)
    {
        _ble     = ble;
        _appData = appData;
        _strava  = strava;

        _loading = true;
        InitializeComponent();
        _loading = false;

        DeviceList.ItemsSource = _devices;

        UpdateLastDeviceLabel();
        UpdateButtonStates(_ble.State);
        LoadStravaState();
        LoadWorkoutState();
        LoadGoalsState();
        LoadAppState();

        _ble.DeviceDiscovered += OnDeviceDiscovered;
        _ble.StateChanged     += OnStateChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        _ble.DeviceDiscovered -= OnDeviceDiscovered;
        _ble.StateChanged     -= OnStateChanged;

        if (_ble.State == ConnectionState.Scanning)
            _ble.StopScanning();

        base.OnClosed(e);
    }

    // =========================================================================
    // BLE event handlers
    // =========================================================================

    private void OnDeviceDiscovered(object? sender, BleDevice device)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_devices.Any(d => d.BluetoothAddress == device.BluetoothAddress))
                _devices.Add(device);
        });
    }

    private void OnStateChanged(object? sender, ConnectionState state)
    {
        Dispatcher.Invoke(() => UpdateButtonStates(state));
    }

    // =========================================================================
    // Button handlers
    // =========================================================================

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        _devices.Clear();
        _ble.StartScanning();
    }

    private void StopScan_Click(object sender, RoutedEventArgs e) => _ble.StopScanning();

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is BleDevice device)
        {
            _ = _ble.ConnectAsync(device);
            Close();
        }
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            this,
            "Forget the saved treadmill? You'll need to scan and reconnect on the next launch.",
            "Forget Saved Device",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);

        if (confirm != System.Windows.MessageBoxResult.OK) return;

        _appData.ClearLastDevice();
        UpdateLastDeviceLabel();
        UpdateButtonStates(_ble.State);
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtonStates(_ble.State);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // =========================================================================
    // Strava tab
    // =========================================================================

    private void LoadStravaState()
    {
        var creds = _strava.GetCredentials();
        if (creds != null)
        {
            ClientIdBox.Text         = creds.ClientId;
            ClientSecretBox.Password = creds.ClientSecret;
            ForgetCredentialsButton.IsEnabled = true;
        }

        UpdateStravaConnectionState();
    }

    private void UpdateStravaConnectionState()
    {
        bool hasCreds  = _strava.HasCredentials;
        bool connected = _strava.IsConnected;

        ConnectStravaButton.IsEnabled    = hasCreds && !connected;
        DisconnectStravaButton.IsEnabled = connected;
        ForgetCredentialsButton.IsEnabled = hasCreds;

        if (connected)
            StravaStatusText.Text = $"Connected as {_strava.AthleteName}.";
        else if (hasCreds)
            StravaStatusText.Text = "Credentials saved. Click \"Connect to Strava…\" to authorize.";
        else
            StravaStatusText.Text = "Not connected. Enter your API credentials above to begin.";
    }

    private void SaveCredentials_Click(object sender, RoutedEventArgs e)
    {
        var id     = ClientIdBox.Text?.Trim()         ?? "";
        var secret = ClientSecretBox.Password?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
        {
            System.Windows.MessageBox.Show(this,
                "Both Client ID and Client Secret are required.",
                "Strava", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        _strava.SaveCredentials(new StravaCredentials(id, secret));
        UpdateStravaConnectionState();
    }

    private void ForgetCredentials_Click(object sender, RoutedEventArgs e)
    {
        var ans = System.Windows.MessageBox.Show(this,
            "Forget the saved Client ID, Client Secret, and any active OAuth tokens?",
            "Forget Strava Credentials",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);
        if (ans != System.Windows.MessageBoxResult.OK) return;

        _strava.ForgetCredentials();
        ClientIdBox.Text         = "";
        ClientSecretBox.Password = "";
        UpdateStravaConnectionState();
    }

    private async void ConnectStrava_Click(object sender, RoutedEventArgs e)
    {
        ConnectStravaButton.IsEnabled = false;
        StravaStatusText.Text = "Waiting for browser authorization…";
        try
        {
            var ok = await _strava.ConnectAsync();
            if (ok)
            {
                var name = _strava.AthleteName;
                var toast = new ToastWindow(
                    "Strava Connected",
                    string.IsNullOrEmpty(name)
                        ? "Future walks will push automatically."
                        : $"Pushing future walks as {name}.",
                    displayMs: 6000,
                    style: ToastStyle.ThumbsUp);
                toast.Show();
            }
            else
            {
                System.Windows.MessageBox.Show(this,
                    "Authorization didn't complete. Check the log for details.",
                    "Strava", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
        finally
        {
            UpdateStravaConnectionState();
        }
    }

    // =========================================================================
    // Goals tab
    // =========================================================================

    private void LoadGoalsState()
    {
        var distM = _appData.DailyDistanceMetersGoal;
        DailyDistanceBox.Text = distM > 0 ? (distM / 1000.0).ToString("0.##") : "";

        var steps = _appData.DailyStepsGoal;
        DailyStepsBox.Text = steps > 0 ? steps.ToString() : "";
    }

    private void DailyDistanceBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var txt = DailyDistanceBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(txt))
        {
            _appData.DailyDistanceMetersGoal = 0;
            return;
        }
        if (double.TryParse(txt, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var km) && km >= 0)
        {
            _appData.DailyDistanceMetersGoal = (int)Math.Round(km * 1000);
            DailyDistanceBox.Text = km.ToString("0.##");
        }
        else
        {
            // Invalid input — restore previous saved value
            LoadGoalsState();
        }
    }

    private void DailyStepsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var txt = DailyStepsBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(txt))
        {
            _appData.DailyStepsGoal = 0;
            return;
        }
        if (int.TryParse(txt, System.Globalization.NumberStyles.Any,
                         System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 0)
        {
            _appData.DailyStepsGoal = n;
            DailyStepsBox.Text = n.ToString();
        }
        else
        {
            LoadGoalsState();
        }
    }

    // =========================================================================
    // Workout tab
    // =========================================================================

    private void LoadWorkoutState()
    {
        _loading = true;
        PauseToleranceSlider.Value = _appData.PauseToleranceSeconds;
        UpdatePauseToleranceLabel(_appData.PauseToleranceSeconds);
        _loading = false;

        MinStepsBox.Text   = _appData.MinWalkSteps.ToString();
        MinSecondsBox.Text = _appData.MinWalkSeconds.ToString();

        JogThresholdBox.Text = _appData.JogThresholdKmh.ToString("0.##",
            System.Globalization.CultureInfo.InvariantCulture);
        RunThresholdBox.Text = _appData.RunThresholdKmh.ToString("0.##",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private void JogThresholdBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(JogThresholdBox.Text?.Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0)
        {
            _appData.JogThresholdKmh = v;
            JogThresholdBox.Text = v.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            JogThresholdBox.Text = _appData.JogThresholdKmh.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void RunThresholdBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(RunThresholdBox.Text?.Trim(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0)
        {
            _appData.RunThresholdKmh = v;
            RunThresholdBox.Text = v.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            RunThresholdBox.Text = _appData.RunThresholdKmh.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void MinStepsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(MinStepsBox.Text?.Trim(), out var n) && n >= 0)
        {
            _appData.MinWalkSteps = n;
            MinStepsBox.Text = n.ToString();
        }
        else
        {
            MinStepsBox.Text = _appData.MinWalkSteps.ToString();
        }
    }

    private void MinSecondsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(MinSecondsBox.Text?.Trim(), out var n) && n >= 0)
        {
            _appData.MinWalkSeconds = n;
            MinSecondsBox.Text = n.ToString();
        }
        else
        {
            MinSecondsBox.Text = _appData.MinWalkSeconds.ToString();
        }
    }

    private void PauseToleranceSlider_ValueChanged(object sender,
        System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        var seconds = (int)Math.Round(e.NewValue);
        UpdatePauseToleranceLabel(seconds);
        if (!_loading)
            _appData.PauseToleranceSeconds = seconds;
    }

    private void UpdatePauseToleranceLabel(int seconds)
    {
        if (PauseToleranceLabel == null) return;
        PauseToleranceLabel.Text = seconds < 60
            ? $"{seconds} sec"
            : $"{seconds / 60} min {seconds % 60:D2} sec".Replace(" 00 sec", "");
    }

    private void DisconnectStrava_Click(object sender, RoutedEventArgs e)
    {
        var ans = System.Windows.MessageBox.Show(this,
            "Disconnect from Strava? Your saved Client ID and Secret stay so you can reconnect without re-entering them.",
            "Disconnect Strava",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);
        if (ans != System.Windows.MessageBoxResult.OK) return;

        _strava.Disconnect();
        UpdateStravaConnectionState();
    }

    // =========================================================================
    // App tab
    // =========================================================================

    private void LoadAppState()
    {
        MinimizeToTrayCheck.IsChecked = _appData.MinimizeToTray;
    }

    private void MinimizeToTrayCheck_Changed(object sender, RoutedEventArgs e)
    {
        _appData.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
    }

    // =========================================================================
    // UI state
    // =========================================================================

    private void UpdateLastDeviceLabel()
    {
        var dev = _appData.LoadLastDevice();
        if (dev != null)
        {
            LastDeviceLabel.Text = $"Saved device:  {dev.Name}  ({dev.Mac})";
            ForgetButton.IsEnabled = true;
        }
        else
        {
            LastDeviceLabel.Text = "No saved device. Click Scan to discover treadmills nearby.";
            ForgetButton.IsEnabled = false;
        }
    }

    private void UpdateButtonStates(ConnectionState state)
    {
        bool disconnected = state == ConnectionState.Disconnected;
        ScanButton.IsEnabled     = disconnected;
        StopScanButton.IsEnabled = state == ConnectionState.Scanning;
        ConnectButton.IsEnabled  = disconnected && DeviceList.SelectedItem != null;
    }
}
