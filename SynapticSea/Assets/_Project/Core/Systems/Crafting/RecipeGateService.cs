using System.Linq;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Phase-specific recipe policy. Session contexts bind actual owners and independent payment receipts.</summary>
    public sealed class RecipeGateService
    {
        public GdDict Evaluate(string recipeId, string phase, GdDict context, PaidHashContext hashContext = null)
        {
            string reason = EvaluateReason(recipeId, phase, context, hashContext ?? PaidHashContext.Legacy);
            return new GdDict { { "ok", reason == "ready" }, { "reason", reason }, { "phase", phase }, { "recipe_id", recipeId } };
        }
        string EvaluateReason(string recipeId, string phase, GdDict c, PaidHashContext hashContext)
        {
            if (c == null || !new[] { "preview", "start", "advance", "complete" }.Contains(phase)) return "invalid_phase";
            GdDict recipe = c.GetDictOrEmpty("recipe");
            if (recipe.IsEmpty || recipe.GetString("recipe_id") != recipeId) return "recipe_missing";
            if (!PaidCraftingState.TryRecipeIngredients(recipe, out GdDict ingredients) || !PaidCraftingState.Finite(recipe.GetFloat("craft_time_seconds")) || recipe.GetFloat("craft_time_seconds") <= 0 ||
                recipe.GetDictOrEmpty("produces").GetString("item_id").Length == 0 || !PaidCraftingState.TryPositiveWholeQuantity(recipe.GetDictOrEmpty("produces").Get("quantity"), out _)) return "invalid_recipe";
            if (c.GetString("equipment_reason").Length > 0) return c.GetString("equipment_reason");
            if (!c.GetBool("station_exists")) return "station_missing";
            if (recipe.GetString("station_kind") != c.GetString("station_kind")) return "wrong_station";
            if (phase == "preview" || phase == "start")
            {
                if (c.GetBool("busy")) return "busy";
                if (c.GetString("station_kind") != "field_crafting" && c.GetInt("skill") < recipe.GetInt("required_skill_level")) return "insufficient_skill";
                if (c.GetInt("tier") < recipe.GetInt("station_tier_min")) return "insufficient_tier";
                if (!c.GetDictOrEmpty("knowledge").GetDictOrEmpty("known").GetBool(recipeId)) return "unknown_recipe";
                GdDict items = c.GetDictOrEmpty("inventory").GetDictOrEmpty("items");
                foreach (var pair in ingredients)
                    if (items.GetInt(pair.Key) < (long)pair.Value) return "missing_ingredients";
                if (!c.GetBool("output_capacity")) return "output_full";
                return "ready";
            }
            GdDict job = c.GetDictOrEmpty("job"), receipt = c.GetDictOrEmpty("payment_receipt");
            if (job.GetString("input_state") != "paid" || job.GetString("recipe_id") != recipeId ||
                job.GetString("recipe_hash") != hashContext.Hash(recipe) ||
                !hashContext.Equal(PaidCraftingState.Payment(job), receipt.GetDictOrEmpty("result").Get("payment"))) return "invalid_paid_receipt";
            if (job.GetString("run_id") != c.GetString("run_id") || job.GetString("actor_id") != c.GetString("actor_id") ||
                job.GetString("station_owner_id") != c.GetString("station_owner_id") || job.GetString("station_id") != c.GetString("station_id")) return "owner_mismatch";
            if (job.GetBool("resume_required")) return "explicit_resume_required";
            if (!c.GetBool("powered") && c.GetString("station_kind") != "field_crafting") return "no_power";
            if (phase == "complete")
            {
                if (job.GetFloat("progress_seconds") != job.GetFloat("required_seconds")) return "incomplete";
                if (!c.GetBool("output_capacity")) return "output_full";
            }
            return "ready";
        }
    }
}
