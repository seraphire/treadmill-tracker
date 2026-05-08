using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TreadmillApp;

public enum ToastStyle
{
    /// <summary>Compact, default running illustration. (Walk Started, generic info)</summary>
    Normal,
    /// <summary>Big celebratory, neutral "you finished this segment".</summary>
    Finish,
    /// <summary>Big celebratory, special "first walk of the day".</summary>
    FirstRun,
    /// <summary>Big celebratory, "you hit a milestone" (daily goal, Conqueror %).</summary>
    Winning,
    /// <summary>Compact thumbs-up affirmation. Reusable for any "operation succeeded" beat.</summary>
    ThumbsUp,
    /// <summary>Compact, "treadmill waiting for you" gentle nag.</summary>
    Ghosted,
    /// <summary>Compact, "we lost the connection / can't upload right now".</summary>
    LostConnection,
    /// <summary>Compact, "let's set this up" — typically clickable to open Settings.</summary>
    Setup,
    /// <summary>Compact, "your walk made it to the cloud" — usually clickable to open the activity URL.</summary>
    Uploaded,
}

/// <summary>
/// Auto-dismissing notification toast. Optional <c>onClick</c> handler runs
/// when the user clicks the body (otherwise click just dismisses).
/// </summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;

    public ToastWindow(string title, string message, int displayMs = 5000,
                       ToastStyle style = ToastStyle.Normal,
                       Action? onClick = null)
    {
        InitializeComponent();
        TitleText.Text   = title;
        MessageText.Text = message;

        ApplyStyle(style);
        PositionBottomRight();

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
        BeginAnimation(OpacityProperty, fadeIn);

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(displayMs) };
        _dismissTimer.Tick += (_, _) => FadeAndClose();
        _dismissTimer.Start();

        MouseLeftButtonDown += (_, _) =>
        {
            onClick?.Invoke();
            FadeAndClose();
        };
    }

    private void ApplyStyle(ToastStyle style)
    {
        switch (style)
        {
            case ToastStyle.Finish:
                MakeLarge("pack://application:,,,/images/finished.png");
                break;

            case ToastStyle.FirstRun:
                MakeLarge("pack://application:,,,/images/firstrun.png");
                break;

            case ToastStyle.Winning:
                MakeLarge("pack://application:,,,/images/winning.png");
                break;

            case ToastStyle.ThumbsUp:
                ToastImage.Source = new BitmapImage(new Uri("pack://application:,,,/images/thumbsup.png"));
                break;

            case ToastStyle.Ghosted:
                ToastImage.Source = new BitmapImage(new Uri("pack://application:,,,/images/ghosted.png"));
                break;

            case ToastStyle.LostConnection:
                ToastImage.Source = new BitmapImage(new Uri("pack://application:,,,/images/lost%20connection.png"));
                break;

            case ToastStyle.Setup:
                ToastImage.Source = new BitmapImage(new Uri("pack://application:,,,/images/config.png"));
                break;

            case ToastStyle.Uploaded:
                ToastImage.Source = new BitmapImage(new Uri("pack://application:,,,/images/uploaded.png"));
                break;

            case ToastStyle.Normal:
            default:
                // Already running.png from XAML
                break;
        }
    }

    private void MakeLarge(string imagePackUri)
    {
        Width  = 460;
        Height = 168;
        ImageColumn.Width  = new GridLength(120);
        ToastImage.Width   = 112;
        ToastImage.Height  = 112;
        ToastImage.Source  = new BitmapImage(new Uri(imagePackUri));
        TitleText.FontSize     = 18;
        MessageText.FontSize   = 13;
        MessageText.LineHeight = 20;
    }

    private void PositionBottomRight() => ToastStack.Place(this);

    private void FadeAndClose()
    {
        _dismissTimer.Stop();
        // Free our slot in the stack right away so any other toasts above
        // us slide down during our fade-out (rather than waiting for
        // Window.Closed to fire after the animation completes).
        ToastStack.BeginRemove(this);
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
        fadeOut.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
