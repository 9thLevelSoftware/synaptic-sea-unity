// Ported from scripts/schemas/tutorial_state_schema.gd @ 96ecb2b0
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Static validation for <see cref="TutorialState"/> catalogs (REQ-UI-005 / ADR-0033).</summary>
    public static class TutorialStateSchema
    {
        public const string SchemaVersion = "tutorial-triggers-1";

        public static bool Validate(object catalog)
        {
            if (catalog == null || !(catalog is GdDict dict))
            {
                CoreServices.Log.Error("TutorialStateSchema: catalog must be a Dictionary; got " + InfraCompat.TypeOf(catalog));
                return false;
            }
            if (V.Str(dict.Get("version", "")) != SchemaVersion)
            {
                CoreServices.Log.Error("TutorialStateSchema: version mismatch (expected " + SchemaVersion + ")");
                return false;
            }
            object tutorialsVariant = dict.Get("tutorials", null);
            if (!(tutorialsVariant is GdArray tutorials))
            {
                CoreServices.Log.Error("TutorialStateSchema: 'tutorials' must be an Array");
                return false;
            }
            var seenIds = new GdDict();
            var seenTriggers = new GdDict();
            foreach (var tutorial in tutorials)
            {
                if (!(tutorial is GdDict tDict))
                {
                    CoreServices.Log.Error("TutorialStateSchema: tutorial must be a Dictionary");
                    return false;
                }
                string idStr = V.Str(tDict.Get("id", ""));
                if (idStr.Length == 0)
                {
                    CoreServices.Log.Error("TutorialStateSchema: tutorial missing 'id'");
                    return false;
                }
                if (seenIds.Has(idStr))
                {
                    CoreServices.Log.Error("TutorialStateSchema: duplicate tutorial id '" + idStr + "'");
                    return false;
                }
                seenIds[idStr] = true;
                string eventStr = V.Str(tDict.Get("trigger_event", ""));
                if (eventStr.Length == 0)
                {
                    CoreServices.Log.Error("TutorialStateSchema: tutorial '" + idStr + "' missing 'trigger_event'");
                    return false;
                }
                string targetStr = V.Str(tDict.Get("trigger_target", ""));
                if (targetStr.Length == 0)
                {
                    CoreServices.Log.Error("TutorialStateSchema: tutorial '" + idStr + "' missing 'trigger_target'");
                    return false;
                }
                string triggerKey = eventStr + "|" + targetStr;
                if (seenTriggers.Has(triggerKey))
                {
                    CoreServices.Log.Error("TutorialStateSchema: duplicate trigger (event, target)=(" + eventStr + ", " + targetStr + ")");
                    return false;
                }
                seenTriggers[triggerKey] = idStr;
                string title = V.Str(tDict.Get("title", ""));
                if (title.Length == 0)
                {
                    CoreServices.Log.Error("TutorialStateSchema: tutorial '" + idStr + "' missing 'title'");
                    return false;
                }
                string body = V.Str(tDict.Get("body", ""));
                if (body.Length == 0)
                {
                    CoreServices.Log.Error("TutorialStateSchema: tutorial '" + idStr + "' missing 'body'");
                    return false;
                }
            }
            return true;
        }
    }
}
