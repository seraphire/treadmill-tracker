using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TreadmillApp.Models;

namespace TreadmillApp.Services;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WendingPacket
{
    public float  SpeedKmh;        // offset 0
    public float  InclineDegrees;  // offset 4
    public float  CadenceSpm;      // offset 8
    public uint   TotalSteps;      // offset 12
    public uint   DistanceM;       // offset 16
    public uint   ElapsedS;        // offset 20
    public byte   HeartRate;       // offset 24
    public byte   Flags;           // offset 25
    public ushort PacketSeq;       // offset 26
    public uint   StepTimestampMs; // offset 28
                                   // total: 32 bytes
}

internal static class WendingFlags
{
    public const byte BeltRunning = 0x01;
    public const byte BtConnected = 0x02;
    public const byte DataFresh   = 0x04;
}

public sealed class WendingBroadcaster : IDisposable
{
    private const string TargetAddr      = "127.0.0.1";
    private const int    TargetPort      = 7654;
    private const int    SendIntervalMs  = 20;   // 50 Hz
    private const int    CadenceWindowMs = 2000;

    private readonly UdpClient _udp = new();
    private CancellationTokenSource? _cts;

    private readonly object _lock = new();

    // Packet fields
    private float    _speedKmh;
    private float    _inclineDegrees;
    private float    _cadenceSpm;
    private uint     _totalSteps;
    private uint     _distanceM;
    private uint     _elapsedS;
    private byte     _heartRate;
    private bool     _btConnected;
    private ushort   _packetSeq;
    private uint     _stepTimestampMs;
    private DateTime _lastFtmsTime = DateTime.MinValue;

    // Cadence rolling window
    private readonly Queue<(long TimestampMs, uint Steps)> _stepWindow = new();
    private uint _prevSteps;

    public void Start()
    {
        lock (_lock) { _btConnected = true; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = SendLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        lock (_lock)
        {
            _btConnected = false;
            _speedKmh    = 0;
            _cadenceSpm  = 0;
            _stepWindow.Clear();
        }
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>
    /// Called on every FTMS or vendor metrics update. <paramref name="session"/> may be null
    /// between sessions. <paramref name="isFtms"/> marks the source for the data_fresh flag.
    /// </summary>
    public void UpdateMetrics(TreadmillMetrics metrics, TreadmillSession? session, bool isFtms)
    {
        lock (_lock)
        {
            _speedKmh       = (float)(metrics.CurrentSpeed  ?? 0.0);
            _inclineDegrees = (float)(metrics.Inclination   ?? 0.0);
            _heartRate      = metrics.HeartRate             ?? 0;
            _distanceM      = session?.DistanceMeters       ?? 0;
            _elapsedS       = (uint)(session?.Duration.TotalSeconds ?? 0);

            uint steps = session?.Steps ?? 0;
            _totalSteps = steps;

            if (isFtms) _lastFtmsTime = metrics.Timestamp;

            UpdateCadence(steps);
        }
    }

    private void UpdateCadence(uint steps)
    {
        if (steps == _prevSteps) return;

        long nowMs = Environment.TickCount64;
        _stepTimestampMs = (uint)nowMs;
        _prevSteps = steps;

        _stepWindow.Enqueue((nowMs, steps));

        // Trim entries older than the cadence window
        while (_stepWindow.Count > 1 && nowMs - _stepWindow.Peek().TimestampMs > CadenceWindowMs)
            _stepWindow.Dequeue();

        if (_stepWindow.Count >= 2)
        {
            var oldest = _stepWindow.Peek();
            double windowSecs = (nowMs - oldest.TimestampMs) / 1000.0;
            double stepDelta  = steps - oldest.Steps;
            _cadenceSpm = windowSecs > 0 ? (float)(stepDelta / windowSecs * 60.0) : 0;
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(SendIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                byte[] bytes;
                lock (_lock)
                {
                    byte flags = 0;
                    if (_speedKmh > 0)                                           flags |= WendingFlags.BeltRunning;
                    if (_btConnected)                                            flags |= WendingFlags.BtConnected;
                    if (_lastFtmsTime > DateTime.MinValue &&
                        DateTime.Now - _lastFtmsTime < TimeSpan.FromSeconds(2)) flags |= WendingFlags.DataFresh;

                    var pkt = new WendingPacket
                    {
                        SpeedKmh        = _speedKmh,
                        InclineDegrees  = _inclineDegrees,
                        CadenceSpm      = _cadenceSpm,
                        TotalSteps      = _totalSteps,
                        DistanceM       = _distanceM,
                        ElapsedS        = _elapsedS,
                        HeartRate       = _heartRate,
                        Flags           = flags,
                        PacketSeq       = _packetSeq++,
                        StepTimestampMs = _stepTimestampMs,
                    };
                    bytes = PacketToBytes(pkt);
                }

                try { await _udp.SendAsync(bytes, bytes.Length, TargetAddr, TargetPort); }
                catch { /* fire-and-forget — Wending may not be running */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static byte[] PacketToBytes(WendingPacket packet)
    {
        int size = Marshal.SizeOf<WendingPacket>();
        var bytes = new byte[size];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { Marshal.StructureToPtr(packet, handle.AddrOfPinnedObject(), false); }
        finally { handle.Free(); }
        return bytes;
    }

    public void Dispose()
    {
        Stop();
        _udp.Dispose();
    }
}
