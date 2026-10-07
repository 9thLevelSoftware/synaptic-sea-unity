using System;
using System.Globalization;
using CritterCrafter;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>Production-only adapter. AI, navigation, hitboxes and mechanical drones remain game-owned.</summary>
    public sealed class ThreatCreatureFactory
    {
        readonly CritterLibrary _library;
        public ThreatCreatureFactory(CritterLibrary library) => _library = library;

        public static string PoolFor(string archetype) => archetype == "biomatter_swarm" ||
            archetype == "puppet_corpse" || archetype == "stalker" ? archetype : "";

        // Store as a decimal string: the game's JSON parser converts numeric values to doubles.
        public static string SeedFor(string instanceId)
        {
            ulong hash = 14695981039346656037UL;
            unchecked { foreach (char c in instanceId ?? "") { hash ^= c; hash *= 1099511628211UL; } }
            return (hash & 0x7fffffffffffffffUL).ToString(CultureInfo.InvariantCulture);
        }

        public GameObject Build(ThreatAIState threat, Transform parent, int layer)
        {
            var node = ThreatPlaceholderFactory.Build(threat.ArchetypeId, threat.Tags, parent, layer);
            var status = node.AddComponent<ThreatCreatureVisualStatus>();
            string pool = PoolFor(threat.ArchetypeId);
            if (pool.Length == 0) { status.Reason = "Game visual (no organic pool)"; return node; }
            bool saved = threat.CreatureVisual.Count > 0;
            if (!saved)
                threat.CreatureVisual = new GdDict { { "pool_id", pool }, { "seed", SeedFor(threat.InstanceId) },
                    { "recipe_json", "" }, { "library_id", "" }, { "library_version", "" } };
            if (_library == null) { status.Reason = "Missing production library"; return node; }
            GameObject staging = null;
            try
            {
                if (V.Str(threat.CreatureVisual.Get("pool_id", "")) != pool)
                    throw new AssemblyException("Saved pool does not match archetype");
                if (!long.TryParse(V.Str(threat.CreatureVisual.Get("seed", "")), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long seed)) throw new AssemblyException("Invalid saved seed");
                string json = V.Str(threat.CreatureVisual.Get("recipe_json", ""));
                CritterRecipe recipe;
                if (saved)
                {
                    if (string.IsNullOrWhiteSpace(json)) throw new AssemblyException("Missing saved recipe; no reroll");
                    recipe = JsonUtility.FromJson<CritterRecipe>(json);
                    if (recipe == null || recipe.seed != seed || recipe.pool_id != pool ||
                        recipe.library_id != V.Str(threat.CreatureVisual.Get("library_id", "")) ||
                        recipe.library_version != V.Str(threat.CreatureVisual.Get("library_version", "")))
                        throw new AssemblyException("Saved recipe identity mismatch");
                }
                else
                {
                    threat.CreatureVisual["library_id"] = _library.LibraryId ?? "";
                    threat.CreatureVisual["library_version"] = _library.Version ?? "";
                    recipe = _library.Generate(pool, seed);
                    // Persist before assembly: missing Unity assets must not cause a new recipe on load.
                    threat.CreatureVisual["recipe_json"] = JsonUtility.ToJson(recipe);
                }
                var diagnostics = RecipeValidator.Validate(_library.Catalog, recipe, allowReview: false);
                if (diagnostics.Count > 0) throw new AssemblyException(string.Join("; ", diagnostics));
                if (_library.FindSkeleton(recipe.skeleton_id)?.model == null)
                    throw new AssemblyException("Missing skeleton model: " + recipe.skeleton_id);
                foreach (var fill in recipe.fills)
                {
                    if (_library.FindPart(fill.part_id)?.model == null)
                        throw new AssemblyException("Missing part model: " + fill.part_id);
                    if (!string.IsNullOrEmpty(fill.connector_part_id) && _library.FindPart(fill.connector_part_id)?.model == null)
                        throw new AssemblyException("Missing connector model: " + fill.connector_part_id);
                }
                staging = new GameObject("CreatureAssembly");
                staging.transform.SetParent(node.transform, false);
                var options = AssemblyOptions.Default;
                options.collision = CreatureCollision.None;
                options.allowReview = false;
                options.fallbackOnInvalid = false;
                options.groundMask = 1 << PhysicsLayers.Structure;
                ICreatureVisualFactory factory = new DefaultCreatureVisualFactory(_library, options);
                var visual = factory.Build(new CreatureSpawnRequest { archetypeId = threat.ArchetypeId, poolId = pool,
                    seed = seed, recipe = recipe, parent = staging.transform, layer = layer });
                // Imported colliders never become an additional combat or navigation authority.
                foreach (var c in visual.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                var placeholder = node.transform.Find("Mesh");
                placeholder.name = "PlaceholderCollider";
                foreach (var renderer in placeholder.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
                staging.name = "Mesh"; // procedural feedback owns this child, not the navigation root
                var motion = visual.GetComponent<CreatureMotion>();
                motion.faceVelocity = true;
                node.AddComponent<ThreatCreatureMotion>().Bind(threat, motion);
                status.IsCreature = true;
                status.Reason = "Validated production recipe";
                var locomotion = visual.GetComponent<AssembledCreature>().Locomotion;
                if (locomotion != null && locomotion.move_speed_mps > 0 &&
                    !double.IsNaN(locomotion.move_speed_mps) && !double.IsInfinity(locomotion.move_speed_mps))
                    threat.MoveSpeed = locomotion.move_speed_mps;
            }
            catch (Exception error)
            {
                if (staging != null) ViewObjects.Destroy(staging);
                status.Reason = error.Message;
                status.Details = error.ToString();
            }
            return node;
        }
    }

    public sealed class ThreatCreatureVisualStatus : MonoBehaviour
    {
        public bool IsCreature { get; internal set; }
        public string Reason { get; internal set; }
        public string Details { get; internal set; }
    }
}
