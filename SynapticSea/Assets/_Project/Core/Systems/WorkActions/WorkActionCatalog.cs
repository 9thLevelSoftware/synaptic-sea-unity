// Ported from scripts/systems/work_action_catalog.gd @ 96ecb2b0

using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>PKG-B2.2a: data-driven WorkAction definitions.</summary>
    public class WorkActionCatalog
    {
        public const string DEFAULT_PATH = "res://data/work_actions/work_action_catalog.json";

        readonly GdDict _actions = new GdDict();
        string _loadedPath = "";

        public bool LoadDefault() => LoadFile(DEFAULT_PATH);

        public bool LoadFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !CatalogRegistry.Exists(path))
                return false;
            if (!(CatalogRegistry.Load(path) is GdDict root))
                return false;
            object actionsV = root.Get("actions", new GdDict());
            if (!(actionsV is GdDict actions))
                return false;
            _actions.Clear();
            foreach (object actionId in actions.Keys)
            {
                if (!(actions[actionId] is GdDict row))
                    continue;
                _actions[V.Str(actionId)] = row.DeepCopy();
            }
            _loadedPath = path;
            return !_actions.IsEmpty;
        }

        public bool HasAction(string actionId) => _actions.Has(actionId);

        public GdDict GetAction(string actionId)
        {
            if (!_actions.Has(actionId))
                return new GdDict();
            return ((GdDict)_actions[actionId]).DeepCopy();
        }

        public List<string> ActionIds()
        {
            var output = new List<string>();
            foreach (object k in _actions.Keys)
                output.Add(V.Str(k));
            ShipCompat.SortStrings(output);
            return output;
        }

        public long ActionCount() => _actions.Count;

        /// <summary>The path of the last successful load (GDScript <c>_loaded_path</c>).</summary>
        public string LoadedPath => _loadedPath;
    }
}
