using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TreadmillApp.Services;

namespace TreadmillApp;

public partial class StatsWindow : Window
{
    private readonly AppDataService _appData;

    public StatsWindow(AppDataService appData)
    {
        InitializeComponent();
        _appData = appData;
        Loaded += (_, _) => Populate();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // =========================================================================
    // Computation + binding
    // =========================================================================

    private void Populate()
    {
        var sessions = _appData.LoadSessions();

        PopulateToday(sessions);
        PopulateThisWeek(sessions);
        PopulateStreak();
        PopulateAllTime(sessions);
    }

    // ── Today ─────────────────────────────────────────────────────────────────

    private void PopulateToday(List<SessionRecord> all)
    {
        var today = all.Where(s => s.StartTime.Date == DateTime.Today).ToList();
        uint     dist  = (uint)today.Sum(s => (long)s.DistanceMeters);
        int      steps = today.Sum(s => s.Steps);
        uint     cal   = (uint)today.Sum(s => (long)s.Calories);
        TimeSpan time  = today.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration);

        TodayDistanceValue.Text = $"{dist / 1000.0:F2} km";
        TodayStepsValue.Text    = steps.ToString();
        TodayCaloriesValue.Text = cal.ToString();
        TodayTimeValue.Text     = FormatDuration(time);
        TodayWalksValue.Text    = today.Count == 1 ? "1 walk" : $"{today.Count} walks";

        // Goal progress
        int distGoal  = _appData.DailyDistanceMetersGoal;
        int stepsGoal = _appData.DailyStepsGoal;

        if (distGoal > 0)
        {
            TodayDistanceGoal.Text = $"of {distGoal / 1000.0:0.##} km goal";
            SetProgress(TodayDistanceFill, TodayDistanceRest, TodayDistanceFillBorder, dist, (uint)distGoal);
        }
        else
        {
            TodayDistanceGoal.Text = "";
            SetProgress(TodayDistanceFill, TodayDistanceRest, TodayDistanceFillBorder, 0, 1); // empty
        }

        if (stepsGoal > 0)
        {
            TodayStepsGoal.Text = $"of {stepsGoal} goal";
            SetProgress(TodayStepsFill, TodayStepsRest, TodayStepsFillBorder, (uint)steps, (uint)stepsGoal);
        }
        else
        {
            TodayStepsGoal.Text = "";
            SetProgress(TodayStepsFill, TodayStepsRest, TodayStepsFillBorder, 0, 1);
        }
    }

    private static void SetProgress(ColumnDefinition fill, ColumnDefinition rest,
                                    System.Windows.Controls.Border fillBorder,
                                    uint value, uint target)
    {
        if (target == 0)
        {
            fill.Width = new GridLength(0, GridUnitType.Star);
            rest.Width = new GridLength(1, GridUnitType.Star);
            return;
        }

        double pct = Math.Min(1.0, (double)value / target);
        fill.Width = new GridLength(Math.Max(0.0001, pct), GridUnitType.Star);
        rest.Width = new GridLength(Math.Max(0.0001, 1 - pct), GridUnitType.Star);

        // Color shift: at 100% the bar turns gold-ish
        fillBorder.Background = pct >= 1.0
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEA, 0xB3, 0x08))  // gold
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E)); // green
    }

    // ── This Week ─────────────────────────────────────────────────────────────

    private void PopulateThisWeek(List<SessionRecord> all)
    {
        // Mon–Sun week containing today.
        var today = DateTime.Today;
        int offset = ((int)today.DayOfWeek + 6) % 7; // Monday = 0
        var weekStart = today.AddDays(-offset);

        // bucket index 0 = Mon, 6 = Sun
        var byDay = new uint[7];
        int weekWalks = 0;
        foreach (var s in all)
        {
            if (s.StartTime.Date < weekStart) continue;
            int idx = ((int)s.StartTime.DayOfWeek + 6) % 7;
            byDay[idx] += s.DistanceMeters;
            weekWalks++;
        }

        uint weekTotal = (uint)byDay.Sum(d => (long)d);
        WeekTotalValue.Text = weekTotal == 0
            ? "no walks yet this week"
            : $"{weekTotal / 1000.0:F2} km · {weekWalks} walk{(weekWalks == 1 ? "" : "s")}";

        // Bars + per-day labels
        var bars   = new[] { WeekBar0, WeekBar1, WeekBar2, WeekBar3, WeekBar4, WeekBar5, WeekBar6 };
        var labels = new[] { WeekVal0, WeekVal1, WeekVal2, WeekVal3, WeekVal4, WeekVal5, WeekVal6 };
        uint max = byDay.Max();

        for (int i = 0; i < 7; i++)
        {
            // Heights are computed against the bar grid's allotted row (~120 px
            // total minus the value+day label rows). Cap usable area at 96 px.
            const double barAreaMax = 96.0;
            double height = max == 0 ? 0 : (byDay[i] / (double)max) * barAreaMax;
            bars[i].Height = Math.Max(0, height);

            labels[i].Text = byDay[i] == 0 ? "" : $"{byDay[i] / 1000.0:0.##}";

            // Highlight today's bar in green
            int todayIdx = ((int)today.DayOfWeek + 6) % 7;
            bars[i].Background = i == todayIdx
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6));
        }
    }

    // ── Streak ────────────────────────────────────────────────────────────────

    private void PopulateStreak()
    {
        int current = _appData.ComputeStreakDays();
        int longest = _appData.ComputeLongestStreakDays();

        CurrentStreakValue.Text = current == 1 ? "1 day" : $"{current} days";
        LongestStreakValue.Text = longest == 1 ? "1 day" : $"{longest} days";
    }

    // ── All Time ──────────────────────────────────────────────────────────────

    private void PopulateAllTime(List<SessionRecord> all)
    {
        if (all.Count == 0)
        {
            AllTimeWalksValue.Text    = "0";
            AllTimeDistanceValue.Text = "0.00 km";
            AllTimeCaloriesValue.Text = "0";
            AllTimeTimeValue.Text     = "0 min";
            LongestWalkValue.Text     = "—";
            FastestPaceValue.Text     = "—";
            FirstWalkValue.Text       = "—";
            return;
        }

        uint     totalDist = (uint)all.Sum(s => (long)s.DistanceMeters);
        uint     totalCal  = (uint)all.Sum(s => (long)s.Calories);
        TimeSpan totalTime = all.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration);

        AllTimeWalksValue.Text    = all.Count.ToString();
        AllTimeDistanceValue.Text = $"{totalDist / 1000.0:F2} km";
        AllTimeCaloriesValue.Text = totalCal.ToString();
        AllTimeTimeValue.Text     = FormatDurationCoarse(totalTime);

        var longestWalk = all.OrderByDescending(s => s.DistanceMeters).First();
        LongestWalkValue.Text = $"{longestWalk.DistanceMeters / 1000.0:F2} km on {longestWalk.StartTime:yyyy-MM-dd}";

        var fastest = all.OrderByDescending(s => s.AverageSpeedKmh).First();
        FastestPaceValue.Text = fastest.AverageSpeedKmh > 0
            ? $"{fastest.AverageSpeedKmh:F1} km/h on {fastest.StartTime:yyyy-MM-dd}"
            : "—";

        var first = all.OrderBy(s => s.StartTime).First();
        FirstWalkValue.Text = $"{first.StartTime:yyyy-MM-dd}";
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return "< 1 min";
        if (d.TotalHours  < 1)  return $"{(int)d.TotalMinutes} min";
        return d.ToString(@"h\:mm");
    }

    private static string FormatDurationCoarse(TimeSpan d)
    {
        // For all-time totals: "3 d 5 h", "8 h 23 m", etc.
        if (d.TotalDays >= 1)
            return $"{(int)d.TotalDays} d {d.Hours} h";
        if (d.TotalHours >= 1)
            return $"{d.Hours} h {d.Minutes} m";
        return $"{(int)d.TotalMinutes} min";
    }
}
