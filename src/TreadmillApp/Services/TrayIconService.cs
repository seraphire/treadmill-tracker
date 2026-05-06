using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using TreadmillApp.Models;

namespace TreadmillApp.Services;

/// <summary>
/// Manages the system tray icon with three states:
///   Disconnected  — disconnected.ico
///   Connected/Idle — connected.ico
///   Walking        — animates through animate1.ico … animate3.ico (stopwatch sweep)
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon      _notify;
    private readonly Icon            _iconConnected;
    private readonly Icon            _iconDisconnected;
    private readonly Icon[]          _walkFrames;
    private readonly DispatcherTimer _animTimer;
    private readonly Window          _mainWindow;
    private readonly Action          _exitCallback;

    private int  _frameIndex;
    private bool _disposed;

    public TrayIconService(Window mainWindow, Action exitCallback)
    {
        _mainWindow   = mainWindow;
        _exitCallback = exitCallback;

        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

        // ── State icons ───────────────────────────────────────────────────────
        _iconConnected    = LoadIcon(Path.Combine(dir, "images", "connected.ico"));
        _iconDisconnected = LoadIcon(Path.Combine(dir, "images", "disconnected.ico"));

        // ── Walk animation frames (animate1.ico … animateN.ico) ───────────────
        var frames = new System.Collections.Generic.List<Icon>();
        for (int i = 1; i <= 8; i++)
        {
            var p = Path.Combine(dir, "images", $"animate{i}.ico");
            if (!File.Exists(p)) break;
            frames.Add(new Icon(p));
        }
        _walkFrames = frames.Count > 0 ? frames.ToArray() : new[] { _iconConnected };

        // ── Animation timer ───────────────────────────────────────────────────
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _animTimer.Tick += OnAnimTick;

        // ── Context menu ──────────────────────────────────────────────────────
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Window",  null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _exitCallback());

        // ── NotifyIcon ────────────────────────────────────────────────────────
        _notify = new NotifyIcon
        {
            Icon             = _iconDisconnected,
            Text             = "Treadmill Tracker",
            Visible          = true,
            ContextMenuStrip = menu,
        };
        _notify.DoubleClick += (_, _) => ShowWindow();
        _notify.MouseClick  += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowWindow();
        };
    }

    // ── Public state API ──────────────────────────────────────────────────────

    public void SetDisconnected()
    {
        StopAnimation();
        _notify.Icon = _iconDisconnected;
        _notify.Text = "Treadmill Tracker — Disconnected";
    }

    public void SetConnectedIdle(string deviceName)
    {
        StopAnimation();
        _notify.Icon = _iconConnected;
        _notify.Text = $"Treadmill Tracker — {deviceName}";
    }

    public void SetWalking(string info)
    {
        _notify.Text = $"Treadmill Tracker — Walking  {info}";
        if (!_animTimer.IsEnabled)
        {
            _frameIndex = 0;
            _notify.Icon = _walkFrames[0];
            _animTimer.Start();
        }
    }

    // ── Notifications ─────────────────────────────────────────────────────────

    public void ShowToast(string title, string message, int durationMs = 5000)
    {
        // ToastWindow is shown by MainWindow; this is the fallback balloon for
        // when the main window is hidden and no ToastWindow would be visible.
        _notify.BalloonTipTitle = title;
        _notify.BalloonTipText  = message;
        _notify.BalloonTipIcon  = ToolTipIcon.None;
        _notify.ShowBalloonTip(durationMs);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void OnAnimTick(object? sender, EventArgs e)
    {
        _frameIndex = (_frameIndex + 1) % _walkFrames.Length;
        _notify.Icon = _walkFrames[_frameIndex];
    }

    private void StopAnimation()
    {
        _animTimer.Stop();
        _frameIndex = 0;
    }

    private static Icon LoadIcon(string path) =>
        File.Exists(path) ? new Icon(path) : SystemIcons.Application;

    private void ShowWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _animTimer.Stop();
        _notify.Visible = false;
        _notify.Dispose();
        if (_iconConnected    != SystemIcons.Application) _iconConnected.Dispose();
        if (_iconDisconnected != SystemIcons.Application) _iconDisconnected.Dispose();
        foreach (var f in _walkFrames)
            if (f != _iconConnected && f != _iconDisconnected) f.Dispose();
    }
}
