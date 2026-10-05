using System;
using System.Collections.Generic;

namespace SynapticSea.Core.Services
{
    /// <summary>Replaces GDScript <c>Time.*</c>. Injected so tests control time.</summary>
    public interface IClock
    {
        /// <summary><c>Time.get_unix_time_from_system()</c>.</summary>
        double UnixTime();

        /// <summary><c>Time.get_ticks_msec()</c>.</summary>
        long TicksMsec();

        /// <summary><c>Time.get_ticks_usec()</c>.</summary>
        long TicksUsec();

        /// <summary><c>Time.get_datetime_string_from_system(utc)</c>, e.g. <c>2026-09-11T00:47:12</c>.</summary>
        string DateTimeString(bool utc = false);
    }

    /// <summary>Real clock backed by the OS.</summary>
    public sealed class SystemClock : IClock
    {
        readonly System.Diagnostics.Stopwatch _sinceStart = System.Diagnostics.Stopwatch.StartNew();

        public double UnixTime() => (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        public long TicksMsec() => _sinceStart.ElapsedMilliseconds;
        public long TicksUsec() => _sinceStart.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;

        public string DateTimeString(bool utc = false) =>
            (utc ? DateTime.UtcNow : DateTime.Now).ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Deterministic clock for tests.</summary>
    public sealed class ManualClock : IClock
    {
        public double Unix = 1_788_000_000.0;
        public long Usec;

        public double UnixTime() => Unix;
        public long TicksMsec() => Usec / 1000;
        public long TicksUsec() => Usec;

        public string DateTimeString(bool utc = false) =>
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Unix)
                .ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        public void Advance(double seconds)
        {
            Unix += seconds;
            Usec += (long)(seconds * 1_000_000.0);
        }
    }

    /// <summary>Replaces GDScript <c>push_warning</c> / <c>push_error</c> / <c>print</c>.</summary>
    public interface ILog
    {
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }

    /// <summary>Collects log lines; tests assert on it like the Godot harness's "no unexpected ERROR/WARNING" rule.</summary>
    public sealed class CollectingLog : ILog
    {
        public readonly List<string> Infos = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Errors = new List<string>();

        public void Info(string message) => Infos.Add(message);
        public void Warning(string message) => Warnings.Add(message);
        public void Error(string message) => Errors.Add(message);
    }

    /// <summary>Discards everything.</summary>
    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }

    /// <summary>Replaces <c>Engine.get_version_info()</c>; the save schema keeps its <c>godot_version</c> key.</summary>
    public interface IEngineInfo
    {
        /// <summary>The string stamped into saves under <c>godot_version</c>.</summary>
        string VersionString { get; }
    }

    public sealed class FixedEngineInfo : IEngineInfo
    {
        public FixedEngineInfo(string version) => VersionString = version;
        public string VersionString { get; }
    }

    /// <summary>
    /// Process-wide service locator for the handful of services the ported static GDScript helpers need
    /// (catalog loaders were <c>static func</c> in Godot). Set once by the Runtime bootstrap or test setup.
    /// Instance code should take services through constructors instead.
    /// </summary>
    public static class CoreServices
    {
        static IClock _clock = new SystemClock();
        public static IClock Clock
        {
            get { PinnedAdmissionResourceScope.RefusePort("Clock"); return _clock; }
            set { PinnedAdmissionResourceScope.RefusePort("Clock"); _clock = value; }
        }
        static ILog _log = NullLog.Instance;
        public static ILog Log
        {
            get { PinnedAdmissionResourceScope.RefusePort("Log"); return _log; }
            set { PinnedAdmissionResourceScope.RefusePort("Log"); _log = value; }
        }
        public static IResourceReader Resources
        {
            get => PinnedAdmissionResourceScope.ReaderOrNull ?? ResourceAuthorityPublication.Reader;
            set { PinnedAdmissionResourceScope.RefusePort("Resources.set"); ResourceAuthorityPublication.ReplaceReader(value); }
        }
        static IStorage _userStorage = new MemoryStorage();
        public static IStorage UserStorage
        {
            get { PinnedAdmissionResourceScope.RefusePort("UserStorage"); return _userStorage; }
            set { PinnedAdmissionResourceScope.RefusePort("UserStorage"); _userStorage = value; }
        }
        static IEngineInfo _engine = new FixedEngineInfo("unity-port");
        public static IEngineInfo Engine
        {
            get { PinnedAdmissionResourceScope.RefusePort("Engine"); return _engine; }
            set { PinnedAdmissionResourceScope.RefusePort("Engine"); _engine = value; }
        }

        /// <summary>
        /// Godot <c>ProjectSettings application/config/version</c> (stamped into cloud manifests as <c>build_id</c>). The
        /// composition root sets it from the build stamp or <c>Application.version</c>; "" (Godot's default) until then.
        /// </summary>
        public static string ProjectVersion
        {
            get { PinnedAdmissionResourceScope.RefusePort("ProjectVersion"); return Systems.InfraCompat.ProjectVersion; }
            set { PinnedAdmissionResourceScope.RefusePort("ProjectVersion"); Systems.InfraCompat.ProjectVersion = value ?? ""; }
        }
    }
}
