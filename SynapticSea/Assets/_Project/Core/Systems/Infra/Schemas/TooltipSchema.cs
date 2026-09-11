// Ported from scripts/schemas/tooltip_schema.gd @ 96ecb2b0
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Static validation for <see cref="TooltipPresenter"/> catalogs (REQ-UI-004 / ADR-0033).
    /// Rejects a missing / non-Dictionary root, wrong <c>version</c>, non-Array <c>entries</c>, entries missing
    /// <c>id</c> / <c>subject_kind</c> / <c>subject_id</c> / <c>title</c>, and duplicate ids. Extra fields are ignored.
    /// </summary>
    public static class TooltipSchema
    {
        public const string SCHEMA_VERSION = "tooltip-catalog-1";

        public static bool Validate(object catalog)
        {
            if (catalog == null || !(catalog is GdDict dict))
            {
                CoreServices.Log.Error("TooltipSchema: catalog must be a Dictionary; got " + InfraCompat.TypeOf(catalog));
                return false;
            }
            if (V.Str(dict.Get("version", "")) != SCHEMA_VERSION)
            {
                CoreServices.Log.Error("TooltipSchema: version mismatch (expected " + SCHEMA_VERSION + ")");
                return false;
            }
            object entriesVariant = dict.Get("entries", null);
            if (!(entriesVariant is GdArray entries))
            {
                CoreServices.Log.Error("TooltipSchema: 'entries' must be an Array");
                return false;
            }
            var seenIds = new GdDict();
            foreach (object entry in entries)
            {
                if (!(entry is GdDict entryDict))
                {
                    CoreServices.Log.Error("TooltipSchema: entry must be a Dictionary");
                    return false;
                }
                string idStr = V.Str(entryDict.Get("id", ""));
                if (idStr.Length == 0)
                {
                    CoreServices.Log.Error("TooltipSchema: entry missing 'id'");
                    return false;
                }
                if (seenIds.Has(idStr))
                {
                    CoreServices.Log.Error("TooltipSchema: duplicate entry id '" + idStr + "'");
                    return false;
                }
                seenIds[idStr] = true;
                string subjectKind = V.Str(entryDict.Get("subject_kind", ""));
                if (subjectKind.Length == 0)
                {
                    CoreServices.Log.Error("TooltipSchema: entry '" + idStr + "' missing 'subject_kind'");
                    return false;
                }
                string subjectId = V.Str(entryDict.Get("subject_id", ""));
                if (subjectId.Length == 0)
                {
                    CoreServices.Log.Error("TooltipSchema: entry '" + idStr + "' missing 'subject_id'");
                    return false;
                }
                string title = V.Str(entryDict.Get("title", ""));
                if (title.Length == 0)
                {
                    CoreServices.Log.Error("TooltipSchema: entry '" + idStr + "' missing 'title'");
                    return false;
                }
                // body / footer are optional; default to empty strings.
            }
            return true;
        }
    }
}
