using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Phase 1.1: the run's simulation clock. <see cref="GameSeconds"/> is the monotonic in-run time that
    /// <c>RunSession.WorldTime</c> reports; <see cref="Scale"/> is how many game seconds pass per real second.
    /// Scale 1.0 (the default) makes game time identical to real time. The clock only reports time of day;
    /// nothing derives behaviour from <see cref="HourOfDay"/> yet.
    /// </summary>
    public sealed class WorldClock
    {
        public const double DefaultScale = 1.0;
        public const double SecondsPerHour = 3600.0;
        public const double HoursPerDay = 24.0;
        public const double DayStartHour = 6.0;
        public const double NightStartHour = 20.0;

        /// <summary>Game seconds since the run began.</summary>
        public double GameSeconds;
        /// <summary>Game seconds per real second. Always finite and greater than zero.</summary>
        public double Scale { get; private set; } = DefaultScale;
        /// <summary>Hour of day (0..24) at <see cref="GameSeconds"/> = 0.</summary>
        public double StartHourOfDay { get; private set; } = DayStartHour;

        public void SetScale(double scale) => Scale = ValidScale(scale) ? scale : DefaultScale;
        public void SetStartHour(double hour) => StartHourOfDay = hour >= 0.0 && hour < HoursPerDay ? hour : DayStartHour;

        public static bool ValidScale(double scale) => scale > 0.0 && !double.IsNaN(scale) && !double.IsInfinity(scale);

        /// <summary>Advances game time by <paramref name="realDelta"/> real seconds. Returns the game seconds added.</summary>
        public double Advance(double realDelta)
        {
            double game = realDelta * Scale;
            GameSeconds += game;
            return game;
        }

        double AbsoluteHours => StartHourOfDay + GameSeconds / SecondsPerHour;
        public long DayIndex => (long)Math.Floor(AbsoluteHours / HoursPerDay);
        public double HourOfDay => AbsoluteHours - Math.Floor(AbsoluteHours / HoursPerDay) * HoursPerDay;
        public bool IsNight => HourOfDay >= NightStartHour || HourOfDay < DayStartHour;

        /// <summary>True when the scale and start hour are the defaults, so <see cref="GameSeconds"/> alone describes the clock.</summary>
        public bool IsDefaultConfiguration => Scale == DefaultScale && StartHourOfDay == DayStartHour;

        public GdDict GetSummary() => new GdDict
        {
            { "game_seconds", GameSeconds },
            { "scale", Scale },
            { "start_hour", StartHourOfDay },
        };

        /// <summary>Restores a summary. Returns false (and leaves the clock unchanged) when the dictionary is malformed.</summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            double seconds = V.F64(summary.Get("game_seconds", 0.0)), scale = V.F64(summary.Get("scale", DefaultScale)), hour = V.F64(summary.Get("start_hour", DayStartHour));
            if (seconds < 0.0 || double.IsNaN(seconds) || double.IsInfinity(seconds) || !ValidScale(scale) || double.IsNaN(hour)) return false;
            GameSeconds = seconds;
            SetScale(scale);
            SetStartHour(hour);
            return true;
        }
    }
}
