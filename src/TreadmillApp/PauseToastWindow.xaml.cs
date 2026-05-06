using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TreadmillApp;

/// <summary>
/// Persistent pause notification with Hide/Finish buttons and a live
/// countdown. Owns its own DispatcherTimer:
///   - While shown, every tick refreshes the message text
///   - "Hide" makes the window invisible but keeps the timer running so
///     the toast can re-pop itself when the deadline approaches
///   - When the remaining window drops below the re-popup threshold the
///     toast automatically becomes visible again with countdown text
///   - "Finish" raises FinishRequested (caller force-finalizes the
///     session) and closes the window
///   - Closes itself silently when the timer expires (BLE manager will
///     fire SessionCompleted on its own)
/// </summary>
public partial class PauseToastWindow : Window
{
    private static readonly TimeSpan RepopThreshold = TimeSpan.FromSeconds(10);

    private readonly DateTime        _expiresAtUtc;
    private readonly DispatcherTimer _ticker;
    private bool _hidden;
    private bool _closing;

    public event Action? FinishRequested;

    public PauseToastWindow(DateTime expiresAtUtc)
    {
        InitializeComponent();
        _expiresAtUtc = expiresAtUtc;

        PositionBottomRight();
        UpdateMessage();

        // Fade in
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _ticker.Tick += (_, _) => Tick();
        _ticker.Start();
    }

    private TimeSpan Remaining => _expiresAtUtc - DateTime.UtcNow;

    private void Tick()
    {
        var remaining = Remaining;

        if (remaining <= TimeSpan.Zero)
        {
            // Pause window expired — BLE manager will fire SessionCompleted;
            // just close ourselves silently.
            CloseSilently();
            return;
        }

        // Re-pop the window if it was hidden and we're inside the threshold
        if (_hidden && remaining <= RepopThreshold)
        {
            _hidden = false;
            Show();
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        UpdateMessage();
    }

    private void UpdateMessage()
    {
        var remaining = Remaining;
        if (remaining <= TimeSpan.Zero) return;

        if (remaining <= RepopThreshold)
        {
            int secs = (int)Math.Max(0, Math.Ceiling(remaining.TotalSeconds));
            TitleText.Text   = "Ending Walk...";
            MessageText.Text = $"Step back on now or click Finish.  Closing in {secs} sec";
        }
        else
        {
            TitleText.Text   = "Walk Paused";
            MessageText.Text = $"Step back on within {Format(remaining)} or click Finish to end now.";
        }
    }

    private static string Format(TimeSpan ts)
    {
        var s = (int)Math.Ceiling(ts.TotalSeconds);
        if (s < 60) return $"{s} sec";
        var min = s / 60;
        var sec = s % 60;
        return sec == 0 ? $"{min} min" : $"{min} min {sec:D2} sec";
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right  - Width  - 14;
        Top  = area.Bottom - Height - 14;
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _hidden = true;
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
        fadeOut.Completed += (_, _) => { if (!_closing) Hide(); };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        FinishRequested?.Invoke();
        CloseSilently();
    }

    /// <summary>Called by MainWindow when the session resumes or completes externally.</summary>
    public void DismissNow() => CloseSilently();

    private void CloseSilently()
    {
        if (_closing) return;
        _closing = true;
        _ticker.Stop();
        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(300));
        fadeOut.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
