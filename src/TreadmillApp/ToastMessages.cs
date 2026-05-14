using System;

namespace TreadmillApp;

/// <summary>
/// Random text variations for toast notifications that fire often enough
/// that a single fixed phrase gets stale. Each call returns one randomly
/// chosen string from a curated pool. Time-of-day-aware where it matters
/// (e.g. "Rise and stride!" only fires before noon).
///
/// New pools are easy to add — define an array, expose a <c>XxxTitle()</c>
/// (or <c>XxxMessage()</c>) method, call it from the relevant toast site.
/// </summary>
internal static class ToastMessages
{
    private static string Pick(string[] options) =>
        options[Random.Shared.Next(options.Length)];

    // =========================================================================
    // First walk of the day  (used in MainWindow SessionCompleted handler)
    // =========================================================================

    public static string FirstWalkOfDayTitle()
    {
        var pool = DateTime.Now.Hour < 12 ? _firstWalkMorning : _firstWalkAnytime;
        return Pick(pool);
    }

    private static readonly string[] _firstWalkMorning =
    {
        "Morning warrior!",
        "Welcome to the day!",
        "Up and at 'em!",
        "Good morning, mover.",
        "Rise and stride!",
        "Dawn patrol.",
        "Coffee can wait.",
        "Walking into the day.",
        "Way to start the morning!",
        "First miles, first light.",
        "Today, you arrived.",
        "Sunrise on the belt.",
    };

    private static readonly string[] _firstWalkAnytime =
    {
        "Way to start!",
        "And we're off!",
        "Today's first miles.",
        "Engines on.",
        "First steps locked in.",
        "Day, meet treadmill.",
        "Stride one, today.",
        "Off and running.",
        "First walk in the books.",
        "Here we go.",
        "On the board for today.",
        "Today's not skipping you.",
    };

    // =========================================================================
    // Walk / Jog / Run complete  (verb-aware via a format-fn pool)
    // =========================================================================

    public static string WalkCompleteTitle(string verb)
    {
        var fmt   = _walkCompleteFormats[Random.Shared.Next(_walkCompleteFormats.Length)];
        var lower = verb.ToLowerInvariant();
        return fmt(verb, lower);
    }

    private static readonly Func<string, string, string>[] _walkCompleteFormats =
    {
        (v, _) => $"{v} Complete!",
        (v, _) => $"{v} wrapped.",
        (v, _) => $"{v} in the books.",
        (v, _) => $"{v} done.",
        (v, _) => $"{v} logged.",
        (v, _) => $"{v} checked off.",
        (_, l) => $"Nice {l}.",
        (_, l) => $"Good {l}.",
        (_, l) => $"Solid {l}.",
        (_, l) => $"That's a {l}.",
        (v, _) => $"{v} down.",
    };

    // =========================================================================
    // Walk Started  (fires once per session at the top)
    // =========================================================================

    public static string WalkStartedTitle() => Pick(_walkStartedTitles);

    private static readonly string[] _walkStartedTitles =
    {
        "Walk Started",
        "Tracking on.",
        "Logging started.",
        "Here we go!",
        "Let's go!",
        "Belt rolling.",
        "Session live.",
        "Recording.",
        "Walking now.",
        "On the move.",
        "Clock's running.",
    };

    // =========================================================================
    // Welcome back  (used for both pause-resume and system-wake reconnect)
    // =========================================================================

    public static string WelcomeBackTitle() => Pick(_welcomeBackTitles);

    private static readonly string[] _welcomeBackTitles =
    {
        "Welcome back!",
        "Back at it.",
        "Back in motion.",
        "Back on the belt.",
        "And we're rolling.",
        "Resumed.",
        "Off again.",
        "Walking again.",
        "Onward.",
        "Picking it up.",
        "Right where we left off.",
    };

    // =========================================================================
    // Daily goal hit
    // =========================================================================

    public static string GoalHitTitle() => Pick(_goalHitTitles);

    private static readonly string[] _goalHitTitles =
    {
        "Daily Goal Hit!",
        "Crushed it!",
        "Goal: ✓",
        "Target acquired.",
        "There it is.",
        "Goal in the bag.",
        "Made it.",
        "Goal hit, day made.",
        "Today's done.",
        "Done and done.",
        "Nailed it.",
    };

    // =========================================================================
    // Walk uploaded to Strava
    // =========================================================================

    public static string UploadedTitle() => Pick(_uploadedTitles);

    private static readonly string[] _uploadedTitles =
    {
        "Walk Uploaded",
        "Pushed to Strava",
        "Live on Strava",
        "Walk synced",
        "On the feed",
        "Posted",
        "Uploaded",
        "Walk's on Strava",
        "Logged and shared",
    };

    // =========================================================================
    // Ghost nag  (haven't walked today, gentle once-per-day prompt at startup)
    // =========================================================================

    public static string GhostNagTitle()   => Pick(_ghostNagTitles);
    public static string GhostNagMessage() => Pick(_ghostNagMessages);

    private static readonly string[] _ghostNagTitles =
    {
        "Ready when you are.",
        "Still here.",
        "Treadmill's waiting.",
        "Patient and ready.",
        "Standing by.",
        "Hey there.",
        "*tap tap*",
        "No rush.",
        "Whenever you're free.",
        "The belt's lonely.",
    };

    private static readonly string[] _ghostNagMessages =
    {
        "No walk logged today yet — your treadmill is patiently waiting.",
        "Today's still got time. Step on whenever you're ready.",
        "Just checking in — no walk yet today.",
        "Streak says hi. The belt is right here.",
        "Plenty of day left for a quick one.",
        "The day isn't done yet — let's get a walk in.",
        "Friendly reminder: I'm still here.",
        "Easy fix: a quick walk and today's in the books.",
    };
}
