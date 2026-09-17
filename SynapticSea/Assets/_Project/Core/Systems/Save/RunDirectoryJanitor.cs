// Unity port (no Godot source): Godot wrote its start scenario to fixed user://start_scenario files; the port writes
// one generated home ship per New Run under user://runs/<id>/ and needs to remove finished ones.
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Deletes generated run directories (<c>user://runs/&lt;id&gt;/</c> holding layout.json, gameplay_slice.json and
    /// blueprint.json) that no save on disk references any more. A directory stays while any slot payload or the world
    /// save's embedded home ship names a layout inside it, and while it is the active run's. Nothing outside
    /// <see cref="RunsDir"/> is ever touched.
    /// </summary>
    public sealed class RunDirectoryJanitor
    {
        public const string RunsDir = "user://runs";

        readonly IStorage _storage;
        readonly SaveLoadService _saves;
        readonly ILog _log;

        public RunDirectoryJanitor(IStorage storage, SaveLoadService saves, ILog log = null)
        {
            _storage = storage ?? CoreServices.UserStorage;
            _saves = saves;
            _log = log ?? CoreServices.Log;
        }

        /// <summary>The run directory name a <c>user://runs/&lt;id&gt;/...</c> path lives in, or "" for any other path.</summary>
        public static string RunDirectoryName(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith(RunsDir + "/", StringComparison.Ordinal))
                return "";
            string rest = path.Substring(RunsDir.Length + 1);
            int slash = rest.IndexOf('/');
            return slash < 0 ? rest : rest.Substring(0, slash);
        }

        /// <summary>Names of the run directories a save (or the active run's layout) still references.</summary>
        public HashSet<string> ReferencedRunDirectories(string activeLayoutPath = "")
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (_saves != null)
            {
                foreach (string layout in _saves.ReferencedLayoutPaths())
                {
                    string name = RunDirectoryName(layout);
                    if (name.Length > 0)
                        result.Add(name);
                }
            }
            string active = RunDirectoryName(activeLayoutPath);
            if (active.Length > 0)
                result.Add(active);
            return result;
        }

        /// <summary>Deletes every unreferenced run directory; returns the deleted names in ordinal order.</summary>
        public GdArray Sweep(string activeLayoutPath = "")
        {
            var deleted = new GdArray();
            if (!_storage.DirExists(RunsDir))
                return deleted;
            HashSet<string> keep = ReferencedRunDirectories(activeLayoutPath);
            foreach (string name in _storage.ListDirectories(RunsDir))
            {
                if (keep.Contains(name))
                    continue;
                if (_storage.DeleteDirectory(RunsDir + "/" + name))
                    deleted.Add(name);
            }
            if (deleted.Count > 0)
                _log.Info("[RunDirectoryJanitor] deleted " + deleted.Count + " finished run director" + (deleted.Count == 1 ? "y" : "ies"));
            return deleted;
        }
    }
}
