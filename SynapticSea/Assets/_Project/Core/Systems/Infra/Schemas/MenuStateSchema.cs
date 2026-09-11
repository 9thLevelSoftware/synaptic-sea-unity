// Ported from scripts/schemas/menu_state_schema.gd @ 96ecb2b0
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Static validation for <see cref="MenuState"/> catalog payloads.</summary>
    public static class MenuStateSchema
    {
        public static readonly GdArray ValidKinds = GdArray.Of("command", "submenu", "toggle", "slider");

        public static bool ValidateCatalog(object catalog)
        {
            if (catalog == null || !(catalog is GdDict dict))
            {
                CoreServices.Log.Error("MenuStateSchema: catalog must be a Dictionary; got " + InfraCompat.TypeOf(catalog));
                return false;
            }
            object menusVariant = dict.Get("menus", null);
            if (!(menusVariant is GdArray menus))
            {
                CoreServices.Log.Error("MenuStateSchema: 'menus' must be an Array");
                return false;
            }
            var seenMenuIds = new GdDict();
            foreach (var menu in menus)
            {
                if (!(menu is GdDict menuDict))
                {
                    CoreServices.Log.Error("MenuStateSchema: menu entry must be a Dictionary");
                    return false;
                }
                string menuId = V.Str(menuDict.Get("id", ""));
                if (menuId.Length == 0)
                {
                    CoreServices.Log.Error("MenuStateSchema: menu missing 'id'");
                    return false;
                }
                if (seenMenuIds.Has(menuId))
                {
                    CoreServices.Log.Error("MenuStateSchema: duplicate menu id '" + menuId + "'");
                    return false;
                }
                seenMenuIds[menuId] = true;
                string title = V.Str(menuDict.Get("title", ""));
                if (title.Length == 0)
                {
                    CoreServices.Log.Error("MenuStateSchema: menu '" + menuId + "' missing 'title'");
                    return false;
                }
                object itemsVariant = menuDict.Get("items", null);
                if (!(itemsVariant is GdArray items))
                {
                    CoreServices.Log.Error("MenuStateSchema: menu '" + menuId + "' 'items' must be an Array");
                    return false;
                }
                if (items.IsEmpty)
                {
                    CoreServices.Log.Error("MenuStateSchema: menu '" + menuId + "' has empty 'items'");
                    return false;
                }
                var seenItemIds = new GdDict();
                foreach (var item in items)
                {
                    if (!(item is GdDict itemDict))
                    {
                        CoreServices.Log.Error("MenuStateSchema: item in menu '" + menuId + "' must be a Dictionary");
                        return false;
                    }
                    string itemId = V.Str(itemDict.Get("id", ""));
                    if (itemId.Length == 0)
                    {
                        CoreServices.Log.Error("MenuStateSchema: item in menu '" + menuId + "' missing 'id'");
                        return false;
                    }
                    if (seenItemIds.Has(itemId))
                    {
                        CoreServices.Log.Error("MenuStateSchema: duplicate item id '" + itemId + "' in menu '" + menuId + "'");
                        return false;
                    }
                    seenItemIds[itemId] = true;
                    string label = V.Str(itemDict.Get("label", ""));
                    if (label.Length == 0)
                    {
                        CoreServices.Log.Error("MenuStateSchema: item '" + itemId + "' in menu '" + menuId + "' missing 'label'");
                        return false;
                    }
                    string kind = V.Str(itemDict.Get("kind", ""));
                    if (!ValidKinds.Contains(kind))
                    {
                        CoreServices.Log.Error("MenuStateSchema: item '" + itemId + "' in menu '" + menuId + "' has invalid kind '" + kind + "'");
                        return false;
                    }
                }
            }
            return true;
        }

        public static bool IsValidKind(string kind) => ValidKinds.Contains(kind);
    }
}
