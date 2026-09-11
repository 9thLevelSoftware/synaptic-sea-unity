// Ported from scripts/systems/crash_report_bundle.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-RL-007 crash report bundle. Captures <c>{message, context, stack}</c> entries (FIFO cap 256) and
    /// flushes them to a JSON bundle through an injected <see cref="IStorage"/>.
    /// </summary>
    public class CrashReportBundle
    {
        public const long MaxBundleEntries = 256;

        readonly GdArray _entries = new GdArray();
        string _capturedAt = "";
        IStorage _storage;
        IClock _clock;

        public CrashReportBundle(IStorage storage = null, IClock clock = null)
        {
            _storage = storage;
            _clock = clock;
        }

        public IStorage Storage
        {
            get => _storage ?? CoreServices.UserStorage;
            set => _storage = value;
        }

        public IClock Clock
        {
            get => _clock ?? CoreServices.Clock;
            set => _clock = value;
        }

        public void Capture(string message, GdDict context = null, GdArray stack = null)
        {
            if (_entries.Count >= MaxBundleEntries) _entries.PopFront();
            _entries.Add(new GdDict
            {
                { "message", message },
                { "context", (context ?? new GdDict()).DeepCopy() },
                { "stack", (stack ?? new GdArray()).ShallowCopy() },
                { "captured_at", Clock.DateTimeString(true) },
            });
            _capturedAt = Clock.DateTimeString(true);
        }

        public int Size() => _entries.Count;

        public GdArray GetEntries() => _entries.ShallowCopy();

        public void Clear()
        {
            _entries.Clear();
            _capturedAt = "";
        }

        public bool Flush(string targetPath)
        {
            if (string.IsNullOrEmpty(targetPath)) return false;
            string dirPath = ResPath.GetBaseDir(targetPath);
            if (dirPath.Length != 0)
            {
                if (!Storage.DirExists(dirPath))
                {
                    try
                    {
                        Storage.MakeDirRecursive(dirPath);
                    }
                    catch (Exception e)
                    {
                        CoreServices.Log.Warning("CrashReportBundle: failed to create crash dir, error=" + e.Message);
                        return false;
                    }
                }
            }
            var payload = new GdDict
            {
                { "captured_at", _capturedAt },
                { "entry_count", _entries.Count },
                { "max_entries", MaxBundleEntries },
                { "entries", _entries.DeepCopy() },
            };
            try
            {
                Storage.WriteText(targetPath, GdJson.Stringify(payload, "\t"));
            }
            catch (Exception e)
            {
                CoreServices.Log.Warning("CrashReportBundle: cannot open target for writing, error=" + e.Message);
                return false;
            }
            return true;
        }

        public bool LoadFromDisk(string sourcePath)
        {
            if (!Storage.FileExists(sourcePath)) return false;
            string jsonText = Storage.ReadText(sourcePath);
            if (jsonText == null) return false;
            object parsed = GdJson.ParseString(jsonText);
            if (parsed == null || !(parsed is GdDict dict)) return false;
            _capturedAt = V.Str(dict.Get("captured_at", ""));
            object entriesVariant = dict.Get("entries", new GdArray());
            if (!(entriesVariant is GdArray entries)) return false;
            _entries.Clear();
            foreach (var entry in entries)
            {
                if (!(entry is GdDict)) continue;
                _entries.Add(entry);
            }
            // Trim to MaxBundleEntries in case the on-disk bundle had more.
            while (_entries.Count > MaxBundleEntries) _entries.PopFront();
            return true;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "entry_count", _entries.Count },
                { "max_entries", MaxBundleEntries },
                { "captured_at", _capturedAt },
            };
        }
    }
}
