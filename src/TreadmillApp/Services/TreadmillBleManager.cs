using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TreadmillApp.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace TreadmillApp.Services;

public enum ConnectionState { Disconnected, Scanning, Connecting, Connected }

public class TreadmillBleManager : IDisposable
{
    private static readonly Guid FtmsServiceUuid   = new("00001826-0000-1000-8000-00805F9B34FB");
    private static readonly Guid TreadmillDataUuid = new("00002ACD-0000-1000-8000-00805F9B34FB");

    private BluetoothLEAdvertisementWatcher? _watcher;
    private readonly HashSet<ulong> _seenAddresses = new();
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _treadmillChar;

    // ── Per-vendor-char log throttle (only the first few packets per
    //     characteristic are logged to the UI to avoid flooding) ────────────
    private readonly object                _vendorLogLock   = new();
    private readonly Dictionary<Guid, int> _vendorCharIndex  = new();
    private readonly Dictionary<Guid, int> _vendorUILogCount = new();

    // ── Vendor polling / watchdog ─────────────────────────────────────────────
    private CancellationTokenSource?      _pollCts;
    private List<GattCharacteristic>      _vendorNotifyChars = new();
    private DateTime                      _lastVendorDataTime = DateTime.MinValue;
    private DateTime                      _lastResubscribeTime = DateTime.MinValue;

    // ── Session tracking ──────────────────────────────────────────────────────
    // Sessions can span multiple "segments" of walking separated by pauses
    // (e.g. user steps off for water). When a pause exceeds PauseTolerance we
    // finalize the session; otherwise the next motion resumes the same one.
    // Each segment baselines the treadmill's own counters because they reset
    // back to 0 when the treadmill goes idle.
    private TreadmillSession? _currentSession;
    private DateTime?         _pauseStartedAt;
    private bool              _wasPaused;
    private uint              _accumulatedDistance;   // metres rolled up from ended segments
    private ushort            _accumulatedSteps;
    private uint              _accumulatedCalories;
    private uint              _segmentDistanceStart;  // treadmill's TotalDistance when current segment began
    private ushort            _segmentStepsStart;
    private uint              _segmentCaloriesStart;
    private BleDevice?        _lastConnectedDevice;
    private bool              _userDisconnecting;

    /// <summary>
    /// How long the user can step off the treadmill before the session is
    /// finalized. Below this, motion resumed within the window continues the
    /// same session (totals carry over). Default 2 minutes.
    /// </summary>
    public TimeSpan PauseTolerance { get; set; } = TimeSpan.FromSeconds(120);

    // ── Events ────────────────────────────────────────────────────────────────
    public event EventHandler<BleDevice>?          DeviceDiscovered;
    public event EventHandler<ConnectionState>?    StateChanged;
    public event EventHandler<TreadmillMetrics>?   MetricsReceived;
    public event EventHandler<string>?             LogMessage;
    public event EventHandler<TreadmillSession>?   SessionStarted;
    public event EventHandler<TreadmillSession>?   SessionUpdated;
    public event EventHandler<TreadmillSession>?   SessionCompleted;
    public event EventHandler<TreadmillSession>?   SessionPaused;
    public event EventHandler<TreadmillSession>?   SessionResumed;
    public event EventHandler?                     ConnectionLost;
    public event EventHandler?                     SystemResumed;

    /// <summary>
    /// True between PowerModes.Suspend and PowerModes.Resume. While set, the
    /// BLE manager suppresses ConnectionLost / auto-reconnect because Windows
    /// is tearing down the connection on purpose; the MainWindow handles the
    /// reconnect on wake via the SystemResumed event with longer retry windows.
    /// </summary>
    public bool IsSystemSleeping { get; private set; }

    public ConnectionState State              { get; private set; } = ConnectionState.Disconnected;
    public BleDevice?      LastConnectedDevice => _lastConnectedDevice;

    // =========================================================================
    // Scanning
    // =========================================================================

    public void StartScanning()
    {
        lock (_seenAddresses) { _seenAddresses.Clear(); }

        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Start();

        SetState(ConnectionState.Scanning);
        Log("Scanning for BLE devices...");
    }

    public void StopScanning()
    {
        if (_watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            _watcher.Stop();
        _watcher = null;

        if (State == ConnectionState.Scanning)
            SetState(ConnectionState.Disconnected);

        Log("Scan stopped.");
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender,
                                          BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.Advertisement.LocalName)) return;
        lock (_seenAddresses) { if (!_seenAddresses.Add(args.BluetoothAddress)) return; }

        ushort? appearanceValue = null;
        var section = args.Advertisement.GetSectionsByType(0x19).FirstOrDefault();
        if (section?.Data.Length >= 2)
        {
            var reader = DataReader.FromBuffer(section.Data);
            reader.ByteOrder = ByteOrder.LittleEndian;
            appearanceValue = reader.ReadUInt16();
        }

        var device = new BleDevice
        {
            DeviceId         = args.BluetoothAddress.ToString(),
            BluetoothAddress = args.BluetoothAddress,
            Name             = args.Advertisement.LocalName,
            Address          = FormatMac(args.BluetoothAddress),
            SignalStrength   = (int)args.RawSignalStrengthInDBm,
            DeviceType       = appearanceValue.HasValue
                                   ? GattAppearanceMapper.GetDeviceType(appearanceValue.Value)
                                   : "Unknown"
        };

        Log($"Found: {device.Name} ({device.Address})  [{device.DeviceType}]");
        DeviceDiscovered?.Invoke(this, device);
    }

    // =========================================================================
    // Connection
    // =========================================================================

    public async Task ConnectAsync(BleDevice device)
    {
        StopScanning();
        SetState(ConnectionState.Connecting);
        Log($"Connecting to {device.Name} ({device.Address})...");

        _lastConnectedDevice = device;
        _userDisconnecting   = false;

        // Reset per-connection state
        _pollCts?.Cancel();
        _pollCts = null;
        lock (_vendorLogLock)
        {
            _vendorCharIndex.Clear();
            _vendorUILogCount.Clear();
        }
        _firstFtmsPacketLogged = false;
        _vendorStepCount       = null;
        _lastFtmsMetrics       = new() { Timestamp = DateTime.Now };
        _vendorNotifyChars.Clear();
        _lastVendorDataTime    = DateTime.MinValue;

        try
        {
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(device.BluetoothAddress);
            if (_device == null)
            {
                Log("ERROR: Could not open device. Ensure it is powered on and in range.");
                SetState(ConnectionState.Disconnected);
                return;
            }

            Log("Device handle obtained. Discovering FTMS service (0x1826)...");
            var servicesResult = await _device.GetGattServicesForUuidAsync(FtmsServiceUuid, BluetoothCacheMode.Uncached);

            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            {
                Log($"FTMS service not found (status: {servicesResult.Status}).");
                var all = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                foreach (var svc in all.Services) Log($"  Service: {svc.Uuid}");
                SetState(ConnectionState.Disconnected);
                return;
            }

            var ftms = servicesResult.Services[0];
            Log("FTMS service found. Getting Treadmill Data characteristic (0x2ACD)...");
            var charsResult = await ftms.GetCharacteristicsForUuidAsync(TreadmillDataUuid, BluetoothCacheMode.Uncached);

            if (charsResult.Status != GattCommunicationStatus.Success || charsResult.Characteristics.Count == 0)
            {
                Log($"Treadmill Data characteristic not found (status: {charsResult.Status}).");
                var allChars = await ftms.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                foreach (var ch in allChars.Characteristics)
                    Log($"  Char: {ch.Uuid}  [{ch.CharacteristicProperties}]");
                SetState(ConnectionState.Disconnected);
                return;
            }

            _treadmillChar = charsResult.Characteristics[0];
            Log($"Treadmill Data found (Properties: {_treadmillChar.CharacteristicProperties}). Enabling notifications...");

            var cccd = await _treadmillChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if (cccd != GattCommunicationStatus.Success)
            {
                Log($"ERROR: Could not enable notifications: {cccd}. Pairing may be required.");
                SetState(ConnectionState.Disconnected);
                return;
            }

            _treadmillChar.ValueChanged += OnTreadmillDataReceived;
            _device.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;

            device.IsConnected = true;
            device.ConnectionTimestamp = DateTime.Now;
            SetState(ConnectionState.Connected);
            Log($"Connected to {device.Name}. Recording all data — walk, then disconnect to save capture file.");

            _ = ScanVendorServicesAsync();
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            SetState(ConnectionState.Disconnected);
        }
    }

    public async Task DisconnectAsync()
    {
        _userDisconnecting = true;
        FinalizeActiveSession();
        _pollCts?.Cancel();
        _pollCts = null;

        if (_treadmillChar != null)
        {
            try
            {
                _treadmillChar.ValueChanged -= OnTreadmillDataReceived;
                await _treadmillChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { }
            _treadmillChar = null;
        }

        if (_device != null)
        {
            _device.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }

        SetState(ConnectionState.Disconnected);
        Log("Disconnected.");
    }

    // =========================================================================
    // FTMS data handler
    // =========================================================================

    private bool _firstFtmsPacketLogged = false;
    private ushort? _vendorStepCount = null;         // latest step count from vendor char
    private TreadmillMetrics _lastFtmsMetrics = new() { Timestamp = DateTime.Now };

    private void OnTreadmillDataReceived(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = new byte[args.CharacteristicValue.Length];
        DataReader.FromBuffer(args.CharacteristicValue).ReadBytes(data);
        if (data.Length < 2) return;

        if (!_firstFtmsPacketLogged)
        {
            _firstFtmsPacketLogged = true;
            var hex = BitConverter.ToString(data).Replace("-", " ");
            ushort f = (ushort)(data[0] | (data[1] << 8));
            Log($"First FTMS packet ({data.Length}b): {hex}");
            Log($"  Flags=0x{f:X4}  SpeedAbsent={(f & 1) != 0}  Dist={(f & 4) != 0}  " +
                $"Incline={(f & 8) != 0}  Energy={(f & 0x80) != 0}  HR={(f & 0x100) != 0}  ElapsedTime={(f & 0x400) != 0}");
        }

        ushort flags = (ushort)(data[0] | (data[1] << 8));
        var metrics = FtmsDataParser.ParseTreadmillData(data, flags);

        // Inject vendor step count — FTMS doesn't carry steps, vendor char does
        if (_vendorStepCount.HasValue)
            metrics.StepCount = _vendorStepCount;

        _lastFtmsMetrics = metrics;
        MetricsReceived?.Invoke(this, metrics);
        UpdateSessionTracking(metrics);
    }

    private void OnDeviceConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _pollCts?.Cancel();
            _pollCts = null;

            // System-suspend-induced disconnects are not "unexpected" — the
            // OS is tearing down BLE on purpose. Skip the LostConnection toast
            // and the auto-reconnect; MainWindow will handle reconnect on
            // wake via the SystemResumed event.
            bool unexpected = !_userDisconnecting && !IsSystemSleeping;
            if (unexpected)
                FinalizeActiveSession();

            _treadmillChar = null;
            SetState(ConnectionState.Disconnected);

            if (unexpected)
            {
                Log("Connection lost.");
                ConnectionLost?.Invoke(this, EventArgs.Empty);
                _ = AutoReconnectAsync();
            }
        }
    }

    // =========================================================================
    // Vendor service discovery
    // =========================================================================

    private static readonly HashSet<string> KnownServiceUuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "00001800-0000-1000-8000-00805f9b34fb",
        "00001801-0000-1000-8000-00805f9b34fb",
        "0000180a-0000-1000-8000-00805f9b34fb",
        "00001826-0000-1000-8000-00805f9b34fb",
        "00001816-0000-1000-8000-00805f9b34fb",
        "00001814-0000-1000-8000-00805f9b34fb",
    };

    private async Task ScanVendorServicesAsync()
    {
        await Task.Delay(500);
        if (_device == null || State != ConnectionState.Connected) return;

        try
        {
            var allResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            var vendorServices = allResult.Services
                .Where(s => !KnownServiceUuids.Contains(s.Uuid.ToString()))
                .ToList();

            if (vendorServices.Count == 0)
            {
                Log("No vendor services found. Steps will be estimated from distance (~).");
                return;
            }

            Log($"Found {vendorServices.Count} vendor service(s) — subscribing to capture step count data:");
            int vendorIdx = 1;
            var pollChars = new List<GattCharacteristic>();
            foreach (var service in vendorServices)
            {
                Log($"  Service V{vendorIdx:D2}: {service.Uuid}");
                var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                foreach (var ch in charsResult.Characteristics)
                {
                    var props = ch.CharacteristicProperties;
                    bool canNotify = (props & (GattCharacteristicProperties.Notify | GattCharacteristicProperties.Indicate)) != 0;
                    bool canRead   = (props & GattCharacteristicProperties.Read) != 0;
                    Log($"    Char: {ch.Uuid}  [{props}]{(canNotify ? " ← subscribe" : "")}{(canRead ? " [readable]" : "")}");

                    if (canNotify)
                    {
                        try
                        {
                            lock (_vendorLogLock) { _vendorCharIndex[ch.Uuid] = vendorIdx; _vendorUILogCount[ch.Uuid] = 0; }

                            var descriptor = (props & GattCharacteristicProperties.Notify) != 0
                                ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                                : GattClientCharacteristicConfigurationDescriptorValue.Indicate;

                            var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(descriptor);
                            if (status == GattCommunicationStatus.Success)
                            {
                                ch.ValueChanged += OnVendorCharacteristicChanged;
                                _vendorNotifyChars.Add(ch);
                                Log($"    → Subscribed (V{vendorIdx:D2})");
                            }
                            else
                            {
                                Log($"    → Subscribe failed: {status}");
                            }
                        }
                        catch (Exception ex) { Log($"    → Subscribe error: {ex.Message}"); }
                    }

                    if (canRead)
                    {
                        lock (_vendorLogLock) _vendorCharIndex.TryAdd(ch.Uuid, vendorIdx);
                        pollChars.Add(ch);
                    }
                }
                vendorIdx++;
            }

            if (_vendorNotifyChars.Count > 0 || pollChars.Count > 0)
            {
                _lastVendorDataTime = DateTime.Now;
                _pollCts = new CancellationTokenSource();
                _ = VendorWatchdogAsync(pollChars, _pollCts.Token);
                var watchdogMsg = pollChars.Count > 0
                    ? $"Watchdog active — polling {pollChars.Count} readable char(s) every 1 s, re-subscribing if silent >20 s."
                    : "Watchdog active — will re-subscribe notify chars if silent >20 s.";
                Log(watchdogMsg);
            }

            Log($"Capture active. Walk on the treadmill, note the step count, then click Disconnect.");
        }
        catch (Exception ex)
        {
            Log($"Vendor scan error: {ex.Message}");
        }
    }

    // Vendor packet format (17 bytes, characteristic 0000fff1):
    //   [0]  0x02        STX
    //   [1]  0x51        Message type
    //   [2]  0x03/0x04   Status (0x03 = running, 0x04 = slowing to stop)
    //   [3]  byte        Speed in 0.1 mph
    //   [4]  0x00
    //   [5]  byte        Elapsed seconds (low byte)
    //   [6]  0x00        Elapsed seconds (high byte, always 0)
    //   [7]  byte        Unknown field A
    //   [8]  0x00
    //   [9]  byte        Unknown field B
    //   [10] 0x00
    //   [11] byte        Step count (low byte)  ← confirmed from capture analysis
    //   [12] 0x00        Step count (high byte, always 0 in observed data)
    //   [13] 0x00
    //   [14] 0x00
    //   [15] byte        XOR checksum of bytes [1]..[14]
    //   [16] 0x03        ETX
    private void OnVendorCharacteristicChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = new byte[args.CharacteristicValue.Length];
        DataReader.FromBuffer(args.CharacteristicValue).ReadBytes(data);

        // Log only the first couple of samples per char to the UI to avoid flooding
        lock (_vendorLogLock)
        {
            _vendorUILogCount.TryGetValue(sender.Uuid, out int count);
            if (count < 2)
            {
                _vendorUILogCount[sender.Uuid] = count + 1;
                var hex = BitConverter.ToString(data).Replace("-", " ");
                Log($"  V{_vendorCharIndex.GetValueOrDefault(sender.Uuid, 0):D2} ({data.Length}b): {hex}");
            }
        }

        ParseVendorPacket(data);
    }

    private void ParseVendorPacket(byte[] data)
    {
        // Any packet — even the short idle "heartbeat" packets the treadmill
        // streams while stationary — proves the BLE link is alive. Mark this
        // before the format filters so the watchdog doesn't think vendor data
        // has gone silent just because the user isn't walking.
        _lastVendorDataTime = DateTime.Now;

        // Only handle the 17-byte active data packet (status 0x03 or 0x04)
        if (data.Length != 17) return;
        if (data[0] != 0x02 || data[1] != 0x51 || data[16] != 0x03) return;
        if (data[2] != 0x03 && data[2] != 0x04) return;

        // Validate XOR checksum (bytes [1]..[14] XOR'd must equal byte [15])
        byte xor = 0;
        for (int i = 1; i <= 14; i++) xor ^= data[i];
        if (xor != data[15]) return;

        // Step count is a uint16 LE at bytes [11, 12]
        ushort steps = (ushort)(data[11] | (data[12] << 8));
        _vendorStepCount = steps;
        _lastVendorDataTime = DateTime.Now;

        // Fire an immediate UI update merged with the last known FTMS values so
        // the step count appears without waiting for the next FTMS packet
        var merged = new TreadmillMetrics
        {
            CurrentSpeed   = _lastFtmsMetrics.CurrentSpeed,
            AverageSpeed   = _lastFtmsMetrics.AverageSpeed,
            TotalDistance  = _lastFtmsMetrics.TotalDistance,
            Inclination    = _lastFtmsMetrics.Inclination,
            ElevationGain  = _lastFtmsMetrics.ElevationGain,
            Pace           = _lastFtmsMetrics.Pace,
            ExpendedEnergy = _lastFtmsMetrics.ExpendedEnergy,
            HeartRate      = _lastFtmsMetrics.HeartRate,
            ElapsedSeconds = _lastFtmsMetrics.ElapsedSeconds,
            StepCount      = steps,
            Timestamp      = DateTime.Now
        };
        MetricsReceived?.Invoke(this, merged);
    }

    // =========================================================================
    // Session tracking
    // =========================================================================

    private void UpdateSessionTracking(TreadmillMetrics metrics)
    {
        bool moving = metrics.CurrentSpeed.HasValue && metrics.CurrentSpeed.Value > 0;

        if (moving)
        {
            // Resuming after a brief pause: bake the just-ended segment's
            // totals into accumulators and start fresh from the treadmill's
            // current values (which may have reset back to 0 while idle).
            if (_wasPaused && _currentSession != null)
            {
                _accumulatedDistance  = _currentSession.DistanceMeters;
                _accumulatedSteps     = _currentSession.Steps;
                _accumulatedCalories  = _currentSession.Calories;
                _segmentDistanceStart = metrics.TotalDistance ?? 0;
                _segmentStepsStart    = _vendorStepCount      ?? 0;
                _segmentCaloriesStart = metrics.ExpendedEnergy ?? 0;
                _wasPaused      = false;
                _pauseStartedAt = null;
                Log("Walk resumed.");
                SessionResumed?.Invoke(this, _currentSession);
            }

            if (_currentSession == null)
            {
                _currentSession        = new TreadmillSession { StartTime = DateTime.Now };
                _accumulatedDistance   = 0;
                _accumulatedSteps      = 0;
                _accumulatedCalories   = 0;
                _segmentDistanceStart  = metrics.TotalDistance ?? 0;
                _segmentStepsStart     = _vendorStepCount      ?? 0;
                _segmentCaloriesStart  = metrics.ExpendedEnergy ?? 0;
                _wasPaused             = false;
                _pauseStartedAt        = null;
                Log($"Walk started at {_currentSession.StartTime:HH:mm:ss} — session logging active.");
                SessionStarted?.Invoke(this, _currentSession);
            }

            _currentSession.AddSpeedSample(metrics.CurrentSpeed!.Value);

            if (metrics.TotalDistance.HasValue)
            {
                long delta = (long)metrics.TotalDistance.Value - (long)_segmentDistanceStart;
                if (delta < 0)
                {
                    // Treadmill reset its counter mid-segment without a pause
                    // we noticed — bake current and rebaseline.
                    _accumulatedDistance  = _currentSession.DistanceMeters;
                    _segmentDistanceStart = metrics.TotalDistance.Value;
                    delta = 0;
                }
                _currentSession.DistanceMeters = (uint)(_accumulatedDistance + delta);
            }

            if (_vendorStepCount.HasValue)
            {
                int delta = (int)_vendorStepCount.Value - (int)_segmentStepsStart;
                if (delta < 0)
                {
                    _accumulatedSteps  = _currentSession.Steps;
                    _segmentStepsStart = _vendorStepCount.Value;
                    delta = 0;
                }
                _currentSession.Steps = (ushort)Math.Min(ushort.MaxValue, _accumulatedSteps + delta);
            }

            if (metrics.ExpendedEnergy.HasValue)
            {
                long delta = (long)metrics.ExpendedEnergy.Value - (long)_segmentCaloriesStart;
                if (delta < 0)
                {
                    _accumulatedCalories  = _currentSession.Calories;
                    _segmentCaloriesStart = metrics.ExpendedEnergy.Value;
                    delta = 0;
                }
                _currentSession.Calories = (uint)(_accumulatedCalories + delta);
            }

            SessionUpdated?.Invoke(this, _currentSession);
        }
        else if (_currentSession != null)
        {
            // Zero-speed sample inside an active session — start (or continue)
            // the pause timer. Finalize only if it exceeds tolerance.
            if (!_wasPaused)
            {
                _wasPaused      = true;
                _pauseStartedAt = DateTime.Now;
                Log($"Walk paused — waiting up to {PauseTolerance.TotalSeconds:F0}s for resume.");
                SessionPaused?.Invoke(this, _currentSession);
            }

            if (_pauseStartedAt.HasValue &&
                DateTime.Now - _pauseStartedAt.Value >= PauseTolerance)
            {
                FinalizeActiveSession();
            }
        }
    }

    /// <summary>
    /// Force-finalizes the active session immediately (e.g. user clicked
    /// "Finish" on the pause toast). No-op if there is no active session.
    /// </summary>
    public void FinalizeSessionNow()
    {
        if (_currentSession == null) return;
        FinalizeActiveSession();
    }

    private void FinalizeActiveSession()
    {
        if (_currentSession == null) return;
        _currentSession.EndTime = DateTime.Now;
        var s = _currentSession;
        _currentSession      = null;
        _wasPaused           = false;
        _pauseStartedAt      = null;
        _accumulatedDistance = 0;
        _accumulatedSteps    = 0;
        _accumulatedCalories = 0;
        Log($"Walk ended — {s.DistanceMeters} m · {s.Steps} steps · {s.Calories} kcal · {s.Duration:mm\\:ss}");
        SessionCompleted?.Invoke(this, s);
    }

    private async Task AutoReconnectAsync()
    {
        var device = _lastConnectedDevice;
        if (device == null) return;

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            await Task.Delay(3000);
            if (_userDisconnecting || State != ConnectionState.Disconnected) return;
            Log($"Reconnect attempt {attempt}/5 to {device.Name}...");
            await ConnectAsync(device);
            if (State == ConnectionState.Connected) return;
        }

        Log("Could not reconnect. Scan and connect manually when ready.");
    }

    private async Task VendorWatchdogAsync(List<GattCharacteristic> pollChars, CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            tick++;

            // Poll readable chars every second
            foreach (var ch in pollChars)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var result = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                    if (result.Status != GattCommunicationStatus.Success) continue;

                    var data = new byte[result.Value.Length];
                    DataReader.FromBuffer(result.Value).ReadBytes(data);
                    ParseVendorPacket(data);
                }
                catch { }
            }

            // Re-subscribe notify chars only when we'd actually benefit from
            // recovering vendor data — i.e. during an active walk. Throttle
            // to once per minute so we don't hammer the BLE stack even if
            // re-subscription doesn't restore notifications.
            if (tick % 5 == 0 &&
                _currentSession != null &&
                _vendorNotifyChars.Count > 0 &&
                _lastVendorDataTime != DateTime.MinValue &&
                DateTime.Now - _lastVendorDataTime  > TimeSpan.FromSeconds(20) &&
                DateTime.Now - _lastResubscribeTime > TimeSpan.FromSeconds(60))
            {
                _lastResubscribeTime = DateTime.Now;
                Log("Vendor notifications silent — re-subscribing...");
                foreach (var ch in _vendorNotifyChars)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        var descriptor = (ch.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0
                            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                        await ch.WriteClientCharacteristicConfigurationDescriptorAsync(descriptor);
                    }
                    catch { }
                }
            }
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private void SetState(ConnectionState state) { State = state; StateChanged?.Invoke(this, state); }
    private void Log(string msg) => LogMessage?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {msg}");

    private static string FormatMac(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        Array.Reverse(bytes);
        return string.Join(":", bytes.Take(6).Select(b => b.ToString("X2")));
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopScanning();
        _device?.Dispose();
    }

    public TreadmillBleManager()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    // =========================================================================
    // Power events — sleep / hibernate / wake
    // =========================================================================

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                HandleSuspend();
                break;
            case PowerModes.Resume:
                HandleResume();
                break;
        }
    }

    private void HandleSuspend()
    {
        IsSystemSleeping = true;
        Log("System suspending — saving any active walk and standing by.");

        // Save any walk in progress before Windows tears down the BLE link.
        // The session is appended to the local store; Strava upload (if it
        // was about to happen) is deferred — it'll retry on next launch.
        if (_currentSession != null)
            FinalizeActiveSession();
    }

    private void HandleResume()
    {
        Log("System resumed.");
        IsSystemSleeping = false;
        // MainWindow handles the reconnect UX via this event so it can show
        // the right toasts and apply a longer retry window than the standard
        // unexpected-disconnect path (BLE post-wake can take 10–30 sec).
        SystemResumed?.Invoke(this, EventArgs.Empty);
    }
}
