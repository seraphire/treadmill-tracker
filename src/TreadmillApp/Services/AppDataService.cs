using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using TreadmillApp.Models;

namespace TreadmillApp.Services;

// ── Stored records ────────────────────────────────────────────────────────────

public record SavedDevice(ulong Address, string Name, string Mac);

/// <summary>
/// Speed-based classification for a session. Drives the toast title and the
/// Strava activity name + sport_type. Tier cutoffs are user-configurable.
/// </summary>
public enum WalkActivityType { Walk, Jog, Run }

public record SessionRecord(
    DateTime StartTime,
    DateTime EndTime,
    uint     DistanceMeters,
    ushort   Steps,
    uint     Calories,
    double   AverageSpeedKmh,
    double   MaxSpeedKmh)
{
    public long?   StravaActivityId  { get; set; }
    public string? StravaActivityUrl { get; set; }

    [JsonIgnore] public double   DistanceKm => DistanceMeters / 1000.0;
    [JsonIgnore] public TimeSpan Duration   => EndTime - StartTime;
    [JsonIgnore] public bool     IsUploaded => StravaActivityId.HasValue;

    public static SessionRecord FromSession(TreadmillSession s) => new(
        s.StartTime, s.EndTime!.Value,
        s.DistanceMeters, s.Steps, s.Calories,
        s.AverageSpeedKmh, s.MaxSpeedKmh);
}

// ── Service ───────────────────────────────────────────────────────────────────

public class AppDataService
{
    private static readonly string AppFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "TreadmillApp");

    private static string SettingsPath => Path.Combine(AppFolder, "settings.json");
    private static string SessionsPath => Path.Combine(AppFolder, "sessions.json");
    private static string FlagsPath    => Path.Combine(AppFolder, "flags.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppDataService()
    {
        Directory.CreateDirectory(AppFolder);
    }

    // ── Device ────────────────────────────────────────────────────────────────

    public SavedDevice? LoadLastDevice()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            return JsonSerializer.Deserialize<SavedDevice>(File.ReadAllText(SettingsPath));
        }
        catch { return null; }
    }

    public void SaveLastDevice(ulong address, string name, string mac)
    {
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new SavedDevice(address, name, mac), JsonOpts)); }
        catch { }
    }

    public void ClearLastDevice()
    {
        try { if (File.Exists(SettingsPath)) File.Delete(SettingsPath); }
        catch { }
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    public List<SessionRecord> LoadSessions()
    {
        try
        {
            if (!File.Exists(SessionsPath)) return new();
            return JsonSerializer.Deserialize<List<SessionRecord>>(File.ReadAllText(SessionsPath)) ?? new();
        }
        catch { return new(); }
    }

    public void AppendSession(TreadmillSession session)
    {
        if (session.EndTime == null) return;
        try
        {
            var all = LoadSessions();
            all.Add(SessionRecord.FromSession(session));
            File.WriteAllText(SessionsPath, JsonSerializer.Serialize(all, JsonOpts));
        }
        catch { }
    }

    /// <summary>
    /// Marks the session whose StartTime matches as uploaded to Strava.
    /// </summary>
    public void MarkUploaded(DateTime startTime, long activityId, string? activityUrl)
    {
        try
        {
            var all = LoadSessions();
            var match = all.FirstOrDefault(s => s.StartTime == startTime);
            if (match == null) return;
            match.StravaActivityId  = activityId;
            match.StravaActivityUrl = activityUrl;
            File.WriteAllText(SessionsPath, JsonSerializer.Serialize(all, JsonOpts));
        }
        catch { }
    }

    public List<SessionRecord> LoadUnuploadedSessions()
        => LoadSessions().Where(s => !s.IsUploaded).ToList();

    public void DeleteSession(DateTime startTime)
    {
        try
        {
            var all = LoadSessions();
            all.RemoveAll(s => s.StartTime == startTime);
            File.WriteAllText(SessionsPath, JsonSerializer.Serialize(all, JsonOpts));
        }
        catch { }
    }

    /// <summary>
    /// Marks the session as "do not retry uploading" — used when Strava
    /// considers it a duplicate of an existing activity. Stored as
    /// StravaActivityId = 0 (a sentinel; real Strava IDs are always > 0).
    /// </summary>
    public void MarkUploadSkipped(DateTime startTime)
    {
        try
        {
            var all = LoadSessions();
            var match = all.FirstOrDefault(s => s.StartTime == startTime);
            if (match == null) return;
            match.StravaActivityId  = 0;
            match.StravaActivityUrl = null;
            File.WriteAllText(SessionsPath, JsonSerializer.Serialize(all, JsonOpts));
        }
        catch { }
    }

    // ── Flags (small bool prefs) ──────────────────────────────────────────────

    private record AppFlags(
        bool      StravaPromptShown,
        int       PauseToleranceSeconds   = 120,
        DateTime? LastGhostedNagDate      = null,
        int       DailyDistanceMetersGoal = 0,    // 0 = no goal set
        int       DailyStepsGoal          = 0,
        DateTime? LastGoalHitDate         = null,
        int       MinWalkSteps            = 1,    // discard walks with fewer steps
        int       MinWalkSeconds          = 60,   // discard walks shorter than this
        double    JogThresholdKmh         = 6.0,  // average speed at/above this = "jog"
        double    RunThresholdKmh         = 9.0,  // average speed at/above this = "run"
        bool      MinimizeToTray          = true,
        double?   WindowLeft              = null,
        double?   WindowTop               = null,
        double?   WindowWidth             = null,
        double?   WindowHeight            = null,
        bool      WindowMaximized         = false,
        bool      AutoConnect             = false,
        int       WalkDistanceMetersGoal  = 0,    // 0 = no per-walk goal
        int       WalkDurationSecondsGoal = 0,
        DateTime? LastGoalSuggestionDate  = null);

    private AppFlags LoadFlags()
    {
        try
        {
            if (!File.Exists(FlagsPath)) return new AppFlags(false);
            return JsonSerializer.Deserialize<AppFlags>(File.ReadAllText(FlagsPath))
                   ?? new AppFlags(false);
        }
        catch { return new AppFlags(false); }
    }

    private void SaveFlags(AppFlags flags)
    {
        try { File.WriteAllText(FlagsPath, JsonSerializer.Serialize(flags, JsonOpts)); }
        catch { }
    }

    public bool HasShownStravaPrompt => LoadFlags().StravaPromptShown;

    public void MarkStravaPromptShown()
    {
        var f = LoadFlags();
        SaveFlags(f with { StravaPromptShown = true });
    }

    public int PauseToleranceSeconds
    {
        get => LoadFlags().PauseToleranceSeconds;
        set
        {
            var f = LoadFlags();
            SaveFlags(f with { PauseToleranceSeconds = Math.Max(10, Math.Min(1800, value)) });
        }
    }

    public DateTime? LastGhostedNagDate => LoadFlags().LastGhostedNagDate;

    public void MarkGhostedNagShown(DateTime when)
    {
        var f = LoadFlags();
        SaveFlags(f with { LastGhostedNagDate = when.Date });
    }

    public int DailyDistanceMetersGoal
    {
        get => LoadFlags().DailyDistanceMetersGoal;
        set
        {
            var f = LoadFlags();
            SaveFlags(f with { DailyDistanceMetersGoal = Math.Max(0, value) });
        }
    }

    public int DailyStepsGoal
    {
        get => LoadFlags().DailyStepsGoal;
        set
        {
            var f = LoadFlags();
            SaveFlags(f with { DailyStepsGoal = Math.Max(0, value) });
        }
    }

    public DateTime? LastGoalHitDate => LoadFlags().LastGoalHitDate;

    public void MarkGoalHit(DateTime when)
    {
        var f = LoadFlags();
        SaveFlags(f with { LastGoalHitDate = when.Date });
    }

    public int MinWalkSteps
    {
        get => LoadFlags().MinWalkSteps;
        set
        {
            var f = LoadFlags();
            SaveFlags(f with { MinWalkSteps = Math.Max(0, value) });
        }
    }

    public int MinWalkSeconds
    {
        get => LoadFlags().MinWalkSeconds;
        set
        {
            var f = LoadFlags();
            SaveFlags(f with { MinWalkSeconds = Math.Max(0, value) });
        }
    }

    public double JogThresholdKmh
    {
        get => LoadFlags().JogThresholdKmh;
        set
        {
            var f = LoadFlags();
            SaveFlags(f with { JogThresholdKmh = Math.Max(0, value) });
        }
    }

    public double RunThresholdKmh
    {
        get => LoadFlags().RunThresholdKmh;
        set
        {
            var f = LoadFlags();
            SaveFlags(f with { RunThresholdKmh = Math.Max(0, value) });
        }
    }

    public bool MinimizeToTray
    {
        get => LoadFlags().MinimizeToTray;
        set
        {
            var f = LoadFlags();
            SaveFlags(f with { MinimizeToTray = value });
        }
    }

    public record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

    public WindowPlacement? SavedWindowPlacement
    {
        get
        {
            var f = LoadFlags();
            if (f.WindowLeft == null || f.WindowTop == null ||
                f.WindowWidth == null || f.WindowHeight == null) return null;
            return new WindowPlacement(f.WindowLeft.Value, f.WindowTop.Value,
                                       f.WindowWidth.Value, f.WindowHeight.Value,
                                       f.WindowMaximized);
        }
    }

    public void SaveWindowPlacement(double left, double top, double width, double height, bool maximized)
    {
        var f = LoadFlags();
        SaveFlags(f with { WindowLeft = left, WindowTop = top,
                           WindowWidth = width, WindowHeight = height,
                           WindowMaximized = maximized });
    }

    public bool AutoConnect
    {
        get => LoadFlags().AutoConnect;
        set
        {
            var f = LoadFlags();
            SaveFlags(f with { AutoConnect = value });
        }
    }

    public int WalkDistanceMetersGoal
    {
        get => LoadFlags().WalkDistanceMetersGoal;
        set
        {
            var f = LoadFlags();
            SaveFlags(f with { WalkDistanceMetersGoal = Math.Max(0, value) });
        }
    }

    public int WalkDurationSecondsGoal
    {
        get => LoadFlags().WalkDurationSecondsGoal;
        set
        {
            var f = LoadFlags();
            SaveFlags(f with { WalkDurationSecondsGoal = Math.Max(0, value) });
        }
    }

    // ── Goal suggestions ──────────────────────────────────────────────────────

    public record GoalSuggestions(
        int DailyDistanceMeters,
        int DailySteps,
        int WalkDistanceMeters,
        int WalkDurationSeconds,
        int WalkCount);

    /// <summary>
    /// Analyses the last 30 days of walks and returns suggested goals set
    /// ~10 % above the median, rounded to friendly step sizes. Returns null
    /// if there are fewer than 5 walks in the window (not enough history).
    /// </summary>
    public GoalSuggestions? SuggestGoals()
    {
        var cutoff  = DateTime.Today.AddDays(-30);
        var recent  = LoadSessions().Where(s => s.StartTime >= cutoff).ToList();
        if (recent.Count < 5) return null;

        // Per-walk medians
        var walkDist = recent.Select(s => (double)s.DistanceMeters).OrderBy(x => x).ToList();
        var walkDur  = recent.Select(s => s.Duration.TotalSeconds).OrderBy(x => x).ToList();

        // Daily medians (only days that had at least one walk)
        var byDay        = recent.GroupBy(s => s.StartTime.Date);
        var dailyDist    = byDay.Select(g => (double)g.Sum(s => s.DistanceMeters)).OrderBy(x => x).ToList();
        var dailySteps   = byDay.Select(g => (double)g.Sum(s => s.Steps)).OrderBy(x => x).ToList();

        return new GoalSuggestions(
            DailyDistanceMeters: RoundUp((int)(Median(dailyDist)  * 1.10), 500),
            DailySteps:          RoundUp((int)(Median(dailySteps) * 1.10), 500),
            WalkDistanceMeters:  RoundUp((int)(Median(walkDist)   * 1.10), 250),
            WalkDurationSeconds: RoundUp((int)(Median(walkDur)    * 1.10), 300),  // 5-min steps
            WalkCount:           recent.Count);
    }

    private static double Median(List<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    private static int RoundUp(int value, int step) =>
        Math.Max(step, (int)Math.Ceiling((double)value / step) * step);

    public DateTime? LastGoalSuggestionDate => LoadFlags().LastGoalSuggestionDate;

    public void MarkGoalSuggestionShown(DateTime when)
    {
        var f = LoadFlags();
        SaveFlags(f with { LastGoalSuggestionDate = when.Date });
    }

    // ── Goal adjustment nudges ────────────────────────────────────────────────

    public enum GoalAdjustmentDirection { Raise, Lower }

    /// <summary>
    /// Represents a suggestion to raise or lower one or more goals based on
    /// recent performance. <see cref="ApplyLabel"/> is the human-readable name
    /// of what will change; the caller passes Apply actions to MainWindow.
    /// </summary>
    public record GoalAdjustment(
        GoalAdjustmentDirection Direction,
        string                  Description,   // shown in the toast body
        int                     ConsecutiveDays);

    /// <summary>
    /// Looks at recent performance and returns up to one raise or lower
    /// suggestion. Checks daily goals against the last 5 distinct walk-days,
    /// and per-walk goals against the last 5 individual sessions.
    /// Returns null when no suggestion is warranted.
    /// </summary>
    public (GoalAdjustment Adj, int NewDailyDist, int NewDailySteps,
                                int NewWalkDist,  int NewWalkDur)?
        CheckGoalAdjustment()
    {
        const int streak  = 5;
        const double beat = 1.10;   // must exceed goal by this factor to count as "beat"
        const double miss = 0.90;   // must be below this factor to count as "missed"

        var sessions  = LoadSessions();
        var dailyDist = DailyDistanceMetersGoal;
        var dailyStp  = DailyStepsGoal;
        var walkDist  = WalkDistanceMetersGoal;
        var walkDur   = WalkDurationSecondsGoal;

        // ── Daily goals: aggregate by day ────────────────────────────────────
        var last5Days = sessions
            .GroupBy(s => s.StartTime.Date)
            .OrderByDescending(g => g.Key)
            .Take(streak)
            .ToList();

        if (last5Days.Count < streak) return null;  // not enough days yet

        double[] dayDist  = last5Days.Select(g => (double)g.Sum(s => s.DistanceMeters)).ToArray();
        double[] daySteps = last5Days.Select(g => (double)g.Sum(s => s.Steps)).ToArray();

        // ── Per-walk goals: individual sessions ──────────────────────────────
        var last5Walks = sessions
            .OrderByDescending(s => s.StartTime)
            .Take(streak)
            .ToList();

        double[] wDist = last5Walks.Select(s => (double)s.DistanceMeters).ToArray();
        double[] wDur  = last5Walks.Select(s => s.Duration.TotalSeconds).ToArray();

        // Check raise conditions (all five beat their goal by the margin)
        bool raiseDailyDist  = dailyDist > 0 && dayDist .All(v => v >= dailyDist  * beat);
        bool raiseDailyStp   = dailyStp  > 0 && daySteps.All(v => v >= dailyStp   * beat);
        bool raiseWalkDist   = walkDist  > 0 && wDist   .All(v => v >= walkDist   * beat);
        bool raiseWalkDur    = walkDur   > 0 && wDur    .All(v => v >= walkDur    * beat);

        // Check lower conditions (all five fell short of their goal by the margin)
        bool lowerDailyDist  = dailyDist > 0 && dayDist .All(v => v <  dailyDist  * miss);
        bool lowerDailyStp   = dailyStp  > 0 && daySteps.All(v => v <  dailyStp   * miss);
        bool lowerWalkDist   = walkDist  > 0 && wDist   .All(v => v <  walkDist   * miss);
        bool lowerWalkDur    = walkDur   > 0 && wDur    .All(v => v <  walkDur    * miss);

        bool anyRaise = raiseDailyDist || raiseDailyStp || raiseWalkDist || raiseWalkDur;
        bool anyLower = lowerDailyDist || lowerDailyStp || lowerWalkDist || lowerWalkDur;
        if (!anyRaise && !anyLower) return null;

        // Prefer raise over lower (more motivating); pick only one direction.
        var dir = anyRaise ? GoalAdjustmentDirection.Raise : GoalAdjustmentDirection.Lower;
        double factor = dir == GoalAdjustmentDirection.Raise ? 1.10 : 1.0;
        int    distStep  = 500;  int durStep = 300;  int stpStep = 500;

        // New daily distance
        int newDailyDist = dailyDist;
        if (dir == GoalAdjustmentDirection.Raise ? raiseDailyDist : lowerDailyDist)
            newDailyDist = RoundUp((int)(dayDist.Average() * factor), distStep);

        // New daily steps
        int newDailySteps = dailyStp;
        if (dir == GoalAdjustmentDirection.Raise ? raiseDailyStp : lowerDailyStp)
            newDailySteps = RoundUp((int)(daySteps.Average() * factor), stpStep);

        // New walk distance
        int newWalkDist = walkDist;
        if (dir == GoalAdjustmentDirection.Raise ? raiseWalkDist : lowerWalkDist)
            newWalkDist = RoundUp((int)(wDist.Average() * factor), 250);

        // New walk duration
        int newWalkDur = walkDur;
        if (dir == GoalAdjustmentDirection.Raise ? raiseWalkDur : lowerWalkDur)
            newWalkDur = RoundUp((int)(wDur.Average() * factor), durStep);

        // Build a readable description of what changed
        var parts = new List<string>();
        if (newDailyDist  != dailyDist && dailyDist  > 0) parts.Add($"daily distance → {newDailyDist  / 1000.0:0.#} km");
        if (newDailySteps != dailyStp  && dailyStp   > 0) parts.Add($"daily steps → {newDailySteps}");
        if (newWalkDist   != walkDist  && walkDist   > 0) parts.Add($"walk distance → {newWalkDist   / 1000.0:0.#} km");
        if (newWalkDur    != walkDur   && walkDur    > 0) parts.Add($"walk time → {newWalkDur / 60} min");

        if (parts.Count == 0) return null;

        var adj = new GoalAdjustment(dir, string.Join(" · ", parts), streak);
        return (adj, newDailyDist, newDailySteps, newWalkDist, newWalkDur);
    }

    // ── Start with Windows (registry) ─────────────────────────────────────────

    private const string RunKey     = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue   = "TreadmillTracker";

    public bool StartWithWindows
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(RunValue) != null;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true)!;
                if (value)
                    key.SetValue(RunValue,
                        $"\"{System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName}\"");
                else
                    key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
            catch { }
        }
    }

    /// <summary>Classify a session by its average speed using the current thresholds.</summary>
    public WalkActivityType ClassifyWalk(double averageSpeedKmh)
    {
        var f = LoadFlags();
        if (averageSpeedKmh >= f.RunThresholdKmh) return WalkActivityType.Run;
        if (averageSpeedKmh >= f.JogThresholdKmh) return WalkActivityType.Jog;
        return WalkActivityType.Walk;
    }

    /// <summary>
    /// Longest-ever consecutive-days streak across the entire history.
    /// </summary>
    public int ComputeLongestStreakDays()
    {
        var sessions = LoadSessions();
        if (sessions.Count == 0) return 0;

        var walkDates = sessions
            .Select(s => s.StartTime.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        int longest = 1;
        int current = 1;
        for (int i = 1; i < walkDates.Count; i++)
        {
            if (walkDates[i] == walkDates[i - 1].AddDays(1))
            {
                current++;
                if (current > longest) longest = current;
            }
            else
            {
                current = 1;
            }
        }
        return longest;
    }

    /// <summary>
    /// Number of consecutive days (ending today, or yesterday if today has
    /// no walk yet) that include at least one logged walk. Used for the
    /// "🔥 N day streak" display.
    /// </summary>
    public int ComputeStreakDays()
    {
        var sessions = LoadSessions();
        if (sessions.Count == 0) return 0;

        var walkDates = sessions
            .Select(s => s.StartTime.Date)
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        var today     = DateTime.Today;
        var yesterday = today.AddDays(-1);

        DateTime expected;
        if (walkDates[0] == today)          expected = today;
        else if (walkDates[0] == yesterday) expected = yesterday;
        else                                return 0;   // streak already broken

        int count = 0;
        foreach (var d in walkDates)
        {
            if (d == expected)         { count++; expected = expected.AddDays(-1); }
            else if (d < expected)     { break; }
            // d > expected shouldn't happen since list is sorted descending
        }
        return count;
    }

    public string DataFolder => AppFolder;
}
