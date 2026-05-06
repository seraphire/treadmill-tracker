using System;
using System.Collections.Generic;
using System.Linq;
using TreadmillApp.Services;

namespace TreadmillApp.Models;

public class TreadmillSession
{
    public DateTime  StartTime { get; set; }
    public DateTime? EndTime   { get; set; }
    public bool      IsActive  => EndTime == null;
    public TimeSpan  Duration  => (EndTime ?? DateTime.Now) - StartTime;

    private readonly List<double> _speedSamples = new();
    private double? _avgSpeedOverride;
    private double? _maxSpeedOverride;

    public void AddSpeedSample(double kmh)
    {
        _speedSamples.Add(kmh);
        _avgSpeedOverride = null;
        _maxSpeedOverride = null;
    }

    public double AverageSpeedKmh
    {
        get => _avgSpeedOverride ?? (_speedSamples.Count > 0 ? Math.Round(_speedSamples.Average(), 1) : 0);
        set => _avgSpeedOverride = value;
    }

    public double MaxSpeedKmh
    {
        get => _maxSpeedOverride ?? (_speedSamples.Count > 0 ? Math.Round(_speedSamples.Max(), 1) : 0);
        set => _maxSpeedOverride = value;
    }

    public uint   DistanceMeters { get; set; }
    public ushort Steps          { get; set; }
    public uint   Calories       { get; set; }
    public double DistanceKm     => DistanceMeters / 1000.0;

    public static TreadmillSession FromRecord(SessionRecord r) => new()
    {
        StartTime      = r.StartTime,
        EndTime        = r.EndTime,
        DistanceMeters = r.DistanceMeters,
        Steps          = r.Steps,
        Calories       = r.Calories,
        AverageSpeedKmh = r.AverageSpeedKmh,
        MaxSpeedKmh     = r.MaxSpeedKmh,
    };
}
