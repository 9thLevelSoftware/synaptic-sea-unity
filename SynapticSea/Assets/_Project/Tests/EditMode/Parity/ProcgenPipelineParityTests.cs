using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Parity
{
    /// <summary>
    /// End-to-end procgen parity against the Godot 4.7.1 captures in fixtures/godot/procgen/: every recipe is
    /// regenerated through the C# <see cref="ShipLayoutGenerator"/> + <see cref="GameplaySliceBuilder"/> (with the
    /// capture's production slice post-step) and compared type-aware and bit-exact with the canonical layout and
    /// gameplay slice. Also checks the raw <c>JSON.stringify(layout, "  ")</c> FNV-1a fingerprint each recipe records.
    /// </summary>
    public class ProcgenPipelineParityTests
    {
        const string Dir = "godot/procgen";

        public static IEnumerable<string> Tags()
        {
            string dir = Path.Combine(Fixtures.FixturesDir, "godot", "procgen");
            if (!Directory.Exists(Fixtures.FixturesDir)) yield break; // stripped checkout: nothing to replay
            foreach (string f in Directory.GetFiles(dir, "recipe_*.json").OrderBy(x => x, StringComparer.Ordinal))
                yield return Path.GetFileNameWithoutExtension(f).Substring("recipe_".Length);
        }

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
        }

        /// <summary>The capture canonical form: Vector2i -&gt; [x, y] ints, Vector3 -&gt; [x, y, z] floats.</summary>
        internal static object Canon(object v)
        {
            switch (v)
            {
                case Vec2i c: return GdArray.Of((long)c.X, (long)c.Y);
                case Vec3 p: return GdArray.Of((double)p.X, (double)p.Y, (double)p.Z);
                case GdDict d:
                    {
                        var o = new GdDict();
                        foreach (var kv in d) o[V.Str(kv.Key)] = Canon(kv.Value);
                        return o;
                    }
                case GdArray a:
                    {
                        var o = new GdArray();
                        foreach (var x in a) o.Append(Canon(x));
                        return o;
                    }
                default: return v;
            }
        }

        internal static ShipBlueprint BlueprintFromRecipe(GdDict recipe)
        {
            GdDict bp = recipe.GetDict("blueprint");
            var blueprint = new ShipBlueprint(bp.GetInt("size"), bp.GetInt("condition"), bp.GetInt("seed_value"));
            GdArray range = bp.GetArray("room_count_range");
            blueprint.RoomCountRange = new Vec2i(V.I32(range[0]), V.I32(range[1]));
            return blueprint;
        }

        /// <summary>Runs a capture recipe: generate_with_options, then the ShipGenerator slice post-step.</summary>
        internal static (GdDict Layout, GdDict Slice, long RawHash) Regenerate(GdDict recipe)
        {
            ShipBlueprint blueprint = BlueprintFromRecipe(recipe);
            GdDict archetype = recipe.GetDictOrEmpty("archetype").DeepCopy();
            GdDict layout = new ShipLayoutGenerator().GenerateWithOptions(
                blueprint, archetype, recipe.GetString("biome_id"), recipe.GetString("difficulty_id"), recipe.GetBool("extended_templates"));

            // Production slice step (ShipGenerator._load_layout_as_scene data path).
            bool planReady = layout.Get("structural_plan") is GdDict plan && !plan.IsEmpty && layout.GetBool("structural_plan_validated");
            Assert.IsTrue(planReady, "generator did not stamp a validated structural plan");
            GdDict slice = new GameplaySliceBuilder().Build(layout);
            if ((!(layout.Get("arc_zones") is GdArray layoutArcs) || layoutArcs.IsEmpty) &&
                slice.Get("arc_zones") is GdArray sliceArcs && !sliceArcs.IsEmpty)
                layout["arc_zones"] = sliceArcs.DeepCopy();
            // The recipes fingerprint the raw JSON.stringify(layout, "  ") text after this step.
            long rawHash = SeedDeterminismContract.Fnv1a64(GdJson.Stringify(layout, "  "));
            return (layout, slice, rawHash);
        }

        static string LayoutPath(string tag)
        {
            string fp = $"{Dir}/layout_{tag}.fullprec.json";
            return Fixtures.Exists(fp) ? fp : $"{Dir}/layout_{tag}.json";
        }

        [TestCaseSource(nameof(Tags))]
        public void LayoutAndGameplaySliceMatchGodot(string tag)
        {
            GdDict recipe = Fixtures.ReadDict($"{Dir}/recipe_{tag}.json");
            var (layout, slice, _) = Regenerate(recipe);

            var layoutDiffs = TreeDiff.Compare(Fixtures.ReadDict(LayoutPath(tag)), Canon(layout));
            Assert.IsEmpty(layoutDiffs, "layout: " + TreeDiff.Format(layoutDiffs));

            var sliceDiffs = TreeDiff.Compare(Fixtures.ReadDict($"{Dir}/gameplay_slice_{tag}.json"), Canon(slice));
            Assert.IsEmpty(sliceDiffs, "gameplay_slice: " + TreeDiff.Format(sliceDiffs));
        }

        /// <summary>The recipe's FNV-1a over the raw <c>JSON.stringify(layout, "  ")</c> text (vectors as strings).</summary>
        [TestCaseSource(nameof(Tags))]
        public void RawStringifyFingerprintMatchesGodot(string tag)
        {
            GdDict recipe = Fixtures.ReadDict($"{Dir}/recipe_{tag}.json");
            GdDict fingerprints = recipe.GetDictOrEmpty("fingerprints");
            Assert.IsTrue(fingerprints.Has("layout_fnv1a_64_raw_stringify_2space"), $"recipe_{tag} has no raw fingerprint (every captured recipe records one)");
            long rawHash = Regenerate(recipe).RawHash;
            long expected = fingerprints.GetInt("layout_fnv1a_64_raw_stringify_2space");
            Assert.AreEqual(expected, rawHash, "raw JSON.stringify(layout, \"  \") fnv1a_64");
        }
    }
}
