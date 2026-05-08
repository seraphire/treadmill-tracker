using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Animation;

namespace TreadmillApp;

/// <summary>
/// Coordinates positioning of bottom-right notification toasts so concurrent
/// toasts stack upward (above existing ones) instead of overlapping at the
/// same corner. When a toast closes, remaining toasts slide down to fill
/// the gap.
///
/// Usage:
///   - Constructor of each toast calls <see cref="Place"/>.
///   - When a toast starts its fade-out, it should call <see cref="BeginRemove"/>
///     so the stack reflows during the fade rather than waiting for the
///     Window.Closed event (which fires only after the fade animation
///     completes — that delay leaves a visible gap).
///   - Window.Closed is also subscribed as a safety-net unregister in case
///     a toast is closed without calling BeginRemove first.
/// </summary>
internal static class ToastStack
{
    private const double EdgeMargin       = 14;   // px from screen edge
    private const double Gap              = 8;    // px between stacked toasts
    private const double ReflowDurationMs = 220;  // slide-down animation

    private static readonly List<Window> _open = new();
    private static readonly object       _lock = new();

    public static void Place(Window toast)
    {
        var area = SystemParameters.WorkArea;
        toast.Left = area.Right - toast.Width - EdgeMargin;

        lock (_lock)
        {
            // The new toast's bottom edge sits at the screen's bottom
            // margin, or just above the currently-topmost open toast,
            // whichever is higher.
            double bottom = area.Bottom - EdgeMargin;
            foreach (var w in _open)
                if (w.Top - Gap < bottom)
                    bottom = w.Top - Gap;

            toast.Top = bottom - toast.Height;

            _open.Add(toast);
            toast.Closed += OnClosed;
        }
    }

    /// <summary>
    /// Removes a toast from the stack and slides remaining toasts into
    /// their new positions. Call this when the toast begins fading out
    /// (so the slide and the fade overlap visually). Idempotent — safe
    /// to call before <see cref="Window.Closed"/> fires.
    /// </summary>
    public static void BeginRemove(Window toast)
    {
        lock (_lock)
        {
            if (_open.Remove(toast))
                Reflow();
        }
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not Window w) return;
        lock (_lock)
        {
            // Remove returns false if BeginRemove already pulled it out.
            if (_open.Remove(w))
                Reflow();
            w.Closed -= OnClosed;
        }
    }

    private static void Reflow()
    {
        var area = SystemParameters.WorkArea;
        double cursor = area.Bottom - EdgeMargin;

        // Walk the stack bottom-up (largest Top first) so each toast knows
        // where its bottom-edge target is. Animate any whose Top changes.
        foreach (var t in _open.OrderByDescending(x => x.Top).ToList())
        {
            double newTop = cursor - t.Height;
            if (Math.Abs(newTop - t.Top) > 0.5)
                AnimateTopTo(t, newTop);
            cursor = newTop - Gap;
        }
    }

    private static void AnimateTopTo(Window w, double targetTop)
    {
        var anim = new DoubleAnimation(targetTop, TimeSpan.FromMilliseconds(ReflowDurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        // Once the animation finishes, clear it so the static Top property
        // holds the final value (otherwise the animation would keep
        // overriding any subsequent changes).
        anim.Completed += (_, _) =>
        {
            w.BeginAnimation(Window.TopProperty, null);
            w.Top = targetTop;
        };
        w.BeginAnimation(Window.TopProperty, anim);
    }
}
