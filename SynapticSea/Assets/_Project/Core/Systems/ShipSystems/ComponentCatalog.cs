// Ported from scripts/systems/component_catalog.gd @ 96ecb2b0

using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>PKG-B2.3a: component definitions + per-role weighted placement sets.</summary>
    public class ComponentCatalog
    {
        public const string DEFAULT_PATH = "res://data/components/component_catalog.json";

        GdDict _components = new GdDict();
        GdDict _roleSets = new GdDict();
        GdDict _roleSystemLinks = new GdDict();

        public bool LoadDefault() => LoadFile(DEFAULT_PATH);

        public bool LoadFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !CatalogRegistry.Exists(path))
                return false;
            if (!(CatalogRegistry.Load(path) is GdDict root))
                return false;
            object comps = root.Get("components", new GdDict());
            object roles = root.Get("role_sets", new GdDict());
            object links = root.Get("role_system_links", new GdDict());
            if (!(comps is GdDict compsDict) || !(roles is GdDict rolesDict))
                return false;
            _components = compsDict.DeepCopy();
            _roleSets = rolesDict.DeepCopy();
            if (links is GdDict linksDict)
                _roleSystemLinks = linksDict.DeepCopy();
            return !_components.IsEmpty;
        }

        public bool HasComponent(string componentId) => _components.Has(componentId);

        public GdDict GetComponent(string componentId)
        {
            if (!_components.Has(componentId))
                return new GdDict();
            return (_components[componentId] as GdDict)?.DeepCopy() ?? new GdDict();
        }

        public long ComponentCount() => _components.Count;

        public GdArray RoleSet(string role, string slotKind)
        {
            string roleKey = _roleSets.Has(role) ? role : "default";
            object setDict = _roleSets.Get(roleKey, new GdDict());
            if (!(setDict is GdDict set))
                return new GdArray();
            object entries = set.Get(slotKind, new GdArray());
            if (!(entries is GdArray arr))
                return new GdArray();
            return arr.DeepCopy();
        }

        public GdArray SystemsForRole(string role)
        {
            object raw = _roleSystemLinks.Get(role, new GdArray());
            if (!(raw is GdArray arr))
                return new GdArray();
            return arr.DeepCopy();
        }

        /// <summary>PKG-B2.3b: reverse lookup for remount / inventory item_form -> component_id.</summary>
        public string ComponentIdForItemForm(string itemForm)
        {
            if (string.IsNullOrEmpty(itemForm))
                return "";
            foreach (object cid in _components.Keys)
            {
                if (!(_components[cid] is GdDict def))
                    continue;
                string form = V.Str(def.Get("item_form", cid));
                if (form == itemForm || V.Str(cid) == itemForm)
                    return V.Str(cid);
            }
            return "";
        }
    }
}
