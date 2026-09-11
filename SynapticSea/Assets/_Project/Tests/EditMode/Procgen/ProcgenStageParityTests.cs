using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// Replays the Godot 4.7.1 stage exports in fixtures/godot/procgen_stages/ (see its README) against the Wave 2
    /// procgen ports: same inputs, type-aware bit-exact comparison of the canonical outputs (Vector2i -&gt; [x, y],
    /// Vector3 -&gt; [x, y, z]). Set PROCGEN_STAGES_DIR to replay a <c>--full</c> export instead.
    /// The structural compiler is additionally checked against the <c>structural_plan</c> embedded in every
    /// fixtures/godot/procgen layout capture.
    /// </summary>
    public class ProcgenStageParityTests
    {
        const string LayoutDir = "godot/procgen";

        static readonly string[] LayoutTags =
        {
            "s17_medium_pristine",
            "s42_abyssal_synaptic_sea_deep_dive", "s42_abyssal_synaptic_sea_hardened", "s42_abyssal_synaptic_sea_standard",
            "s42_breach_field_deep_dive", "s42_breach_field_hardened", "s42_breach_field_standard",
            "s42_dead_fleet_deep_dive", "s42_dead_fleet_hardened", "s42_dead_fleet_standard",
            "s42_small_wrecked_ext", "s7777_small_wrecked_ext", "s777_small_wrecked_ext", "s999_small_wrecked_ext",
        };

        static readonly string[] Archetypes = { "", "derelict", "small_freighter", "medium_cruiser", "life_boat" };

        readonly List<string> _report = new List<string>();
        int _failures;

        [SetUp]
        public void SetUp()
        {
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
            StructuralPlacer.ResetSharedKitCatalog();
            _report.Clear();
            _failures = 0;
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            EncounterInjector.ClearTableCache();
            StructuralPlacer.ResetSharedKitCatalog();
        }

        // ------------------------------------------------------------------ helpers

        static GdDict Expected(string stage)
        {
            string overrideDir = Environment.GetEnvironmentVariable("PROCGEN_STAGES_DIR");
            if (!string.IsNullOrEmpty(overrideDir))
                return (GdDict)GdJson.Parse(File.ReadAllText(Path.Combine(overrideDir, stage + ".json"), new UTF8Encoding(false)), exactNumbers: true);
            string rel = $"godot/procgen_stages/{stage}.json";
            Fixtures.Require(rel);
            return Fixtures.ReadDict(rel);
        }

        /// <summary>The exporter's canonical form: Vec2i -&gt; [x, y] ints, Vec3 -&gt; [x, y, z] floats.</summary>
        static object Canon(object v)
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

        void Cmp(object expected, object actual, string where)
        {
            var diffs = TreeDiff.Compare(expected, Canon(V.Normalize(actual)));
            if (diffs.Count == 0) return;
            _failures++;
            if (_report.Count < 20) _report.Add(where + ": " + TreeDiff.Format(diffs));
        }

        void Finish(int cases)
        {
            string msg = $"{cases} cases, {_failures} mismatching\n" + string.Join("\n", _report);
            TestContext.WriteLine(msg);
            Assert.AreEqual(0, _failures, msg);
            Assert.That(cases, Is.GreaterThan(0));
        }

        static GdDict Archetype(string name) =>
            name.Length == 0 ? new GdDict() : CatalogRegistry.LoadDict("res://data/procgen/archetypes/" + name + ".json");

        static string LayoutPath(string tag)
        {
            string fp = $"{LayoutDir}/layout_{tag}.fullprec.json";
            return Fixtures.Exists(fp) ? fp : $"{LayoutDir}/layout_{tag}.json";
        }

        /// <summary><c>JSON.parse_string</c> of a capture layout (every number a double, as in Godot).</summary>
        static GdDict ParseLayout(string tag) => GdJson.ParseDict(Fixtures.ReadText(LayoutPath(tag)));

        static void VecCells(GdDict layout)
        {
            foreach (GdDict room in layout.GetArray("rooms"))
            {
                var cells = new GdArray();
                foreach (GdArray c in room.GetArray("cells")) cells.Append(new Vec2i(V.I32(c[0]), V.I32(c[1])));
                room["cells"] = cells;
            }
        }

        static string Flag(bool b) => b ? "true" : "false";

        // ------------------------------------------------------------------ stages

        [Test]
        public void TemplateSelector_MatchesGodot()
        {
            GdDict exp = Expected("template_selector");
            var sel = new TemplateSelector();
            int n = 0;
            foreach (GdDict c in exp.GetArray("cases"))
            {
                long seed = c.GetInt("seed");
                var bp = new ShipBlueprint(1, 1, seed);
                var got = new GdDict { { "seed", seed }, { "select", sel.Select(bp, new GdDict()).Id } };
                foreach (var (d, e) in new[] { (false, false), (true, false), (false, true), (true, true) })
                    got["opts_" + Flag(d) + "_" + Flag(e)] = sel.SelectWithOptions(bp, new GdDict(), d, e).Id;
                got["explicit"] = sel.SelectWithOptions(bp, new GdDict { { "template", "ring" } }).Id;
                Cmp(c, got, "seed " + seed);
                n++;
            }
            var templates = new GdDict();
            foreach (string tid in TemplateSelector.EXTENDED_TEMPLATES)
            {
                TopologyTemplate t = sel.Select(new ShipBlueprint(), new GdDict { { "template", tid } });
                templates[tid] = new GdDict
                {
                    { "id", t.Id }, { "zones", new GdArray(t.Zones) }, { "connections", new GdArray(t.Connections) },
                    { "deck_config", t.DeckConfig },
                };
            }
            Cmp(exp["templates"], templates, "templates");
            Cmp(exp["available"], new GdArray
            {
                GdString.ToGdArray(sel.AvailableTemplates(false, false)), GdString.ToGdArray(sel.AvailableTemplates(true, false)),
                GdString.ToGdArray(sel.AvailableTemplates(false, true)), GdString.ToGdArray(sel.AvailableTemplates(true, true)),
            }, "available_templates");
            Cmp(exp["catalog_size_on_disk"], sel.CatalogSizeOnDisk(), "catalog_size_on_disk");
            Finish(n);
        }

        [Test]
        public void RoomAssigner_MatchesGodot()
        {
            GdDict exp = Expected("room_assigner");
            var sel = new TemplateSelector();
            var templates = new Dictionary<string, TopologyTemplate>();
            var archetypes = new Dictionary<string, GdDict>();
            int n = 0;
            foreach (GdDict c in exp.GetArray("cases"))
            {
                string tid = c.GetString("template");
                if (!templates.TryGetValue(tid, out TopologyTemplate template))
                    templates[tid] = template = sel.Select(new ShipBlueprint(), new GdDict { { "template", tid } });
                string archName = c.GetString("archetype");
                if (!archetypes.TryGetValue(archName, out GdDict arch)) archetypes[archName] = arch = Archetype(archName);
                var bp = new ShipBlueprint(c.GetInt("size"), 1, c.GetInt("seed"));
                var assigner = new RoomAssigner();
                string biome = c.GetString("biome");
                List<GdDict> plan = biome == "<none>"
                    ? assigner.Assign(template, bp, arch.DeepCopy())
                    : assigner.AssignWithSelector(template, bp, arch.DeepCopy(), new RoomVariantSelector(), biome);
                Cmp(c["plan"], new GdArray(plan), $"{tid}/{archName}/size{c.GetInt("size")}/seed{c.GetInt("seed")}/{biome}");
                foreach (GdDict room in plan) Cmp(exp["key_order"], new GdArray(room.Keys), "room key order");
                n++;
            }
            var normalized = new GdDict();
            foreach (string a in Archetypes) normalized[a] = RoomAssigner.NormalizeArchetype(Archetype(a));
            Cmp(exp["normalized_archetypes"], normalized, "normalize_archetype");
            Finish(n);
        }

        static RoomGraph Graph(GdDict c) =>
            new RoomGraphGenerator().Generate(
                new ShipBlueprint(c.GetInt("size"), 0, c.GetInt("seed")), Archetype(c.GetString("archetype")).DeepCopy());

        [Test]
        public void RoomGraphGenerator_MatchesGodot()
        {
            GdDict exp = Expected("room_graph_generator");
            int n = 0;
            foreach (GdDict c in exp.GetArray("cases"))
            {
                Cmp(c["graph"], Graph(c).ToDict(), $"{c.GetString("archetype")}/size{c.GetInt("size")}/seed{c.GetInt("seed")}");
                n++;
            }
            Finish(n);
        }

        [Test]
        public void StructuralPlacer_MatchesGodot()
        {
            GdDict exp = Expected("structural_placer");
            int n = 0;
            foreach (GdDict c in exp.GetArray("cases"))
            {
                var placer = new StructuralPlacer();
                StructuralPlacer.Placement result = placer.PlaceStructure(Graph(c), c.GetInt("seed"), c.GetString("biome"));
                var rooms = new GdArray();
                if (result != null)
                {
                    foreach (var room in result.Rooms)
                    {
                        rooms.Append(new GdDict
                        {
                            { "name", room.RoomId }, { "position", room.Position },
                            { "modules", GdString.ToGdArray(placer.ModulesForRole(room.Role)) },
                        });
                        // The records carry the same module list the room node was built from.
                        var ids = new List<string>();
                        foreach (var m in room.Modules) ids.Add(m.ModuleId);
                        Assert.AreEqual(placer.ModulesForRole(room.Role), ids);
                    }
                }
                string where = $"{c.GetString("archetype")}/size{c.GetInt("size")}/seed{c.GetInt("seed")}/{c.GetString("biome")}";
                Cmp(c["root"], result != null, where + " root");
                Cmp(c["rooms"], rooms, where);
                n++;
            }
            Finish(n);
        }

        [Test]
        public void EncounterInjector_MatchesGodot()
        {
            GdDict exp = Expected("encounter_injector");
            var texts = new Dictionary<string, string>();
            int n = 0;
            foreach (GdDict c in exp.GetArray("cases"))
            {
                string tag = c.GetString("layout");
                if (!texts.TryGetValue(tag, out string text)) texts[tag] = text = Fixtures.ReadText(LayoutPath(tag));
                GdDict layout = GdJson.ParseDict(text);
                layout.Erase("encounters");
                layout.Erase("encounter_pacing");
                string variant = c.GetString("variant");
                if (variant == "no_critical_path") layout.Erase("critical_path");
                else if (variant == "no_cells") foreach (GdDict room in layout.GetArray("rooms")) room.Erase("cells");
                else if (variant == "vec_cells") VecCells(layout);
                string b = c.GetString("biome"), d = c.GetString("difficulty");
                BiomeProfile biome = b.Length == 0 ? null : BiomeProfile.FromFile("res://data/procgen/biomes/" + b + ".json");
                DifficultyProfile difficulty = d.Length == 0 ? null : DifficultyProfile.ForId(d);
                GdDict result = new EncounterInjector().Inject(layout, biome, difficulty, c.GetInt("seed"));
                Assert.AreSame(layout, result);
                string where = $"{tag}/{variant}/{b}/{d}/seed{c.GetInt("seed")}";
                Cmp(c["encounters"], result.Get("encounters"), where + " encounters");
                Cmp(c["encounter_pacing"], result.Get("encounter_pacing", null), where + " encounter_pacing");
                Cmp(c["validate"], EncounterInjector.Validate(result), where + " validate");
                n++;
            }
            Finish(n);
        }

        [Test]
        public void StructuralEdgeCompiler_MatchesGodotVariantsAndErrorPaths()
        {
            GdDict exp = Expected("structural_edge_compiler");
            GdArray bad = exp.GetArray("bad_layouts");
            int n = 0;
            foreach (GdDict c in exp.GetArray("cases"))
            {
                string tag = c.GetString("layout");
                string variant = c.GetString("variant");
                GdDict layout;
                if (tag.StartsWith("bad_", StringComparison.Ordinal))
                {
                    layout = ((GdDict)bad[int.Parse(tag.Substring(4))]).DeepCopy();
                }
                else
                {
                    layout = ParseLayout(tag);
                    layout.Erase("structural_plan");
                    if (variant == "vec_cells") VecCells(layout);
                    else if (variant == "hatch_breach")
                    {
                        GdArray portals = layout.GetArrayOrEmpty("portals");
                        for (int i = 0; i < portals.Count; i++)
                        {
                            var p = (GdDict)portals[i];
                            switch (i % 4)
                            {
                                case 0: p["state"] = "hatch"; break;
                                case 1: p["state"] = "BREACH"; break;
                                case 2: p["state"] = "open"; break;
                                default: p["module_id"] = "bulkhead_portal_2x1"; break;
                            }
                        }
                        layout["kit_id"] = "ship_structural_industrial";
                    }
                }
                GdDict plan = new StructuralEdgeCompiler().Compile(layout);
                string where = tag + "/" + variant;
                Cmp(c["plan"], plan, where);
                Cmp(c["occupancy_keys"], new GdArray(((GdDict)plan["occupancy"]).Keys), where + " occupancy key order");
                Cmp(c["edge_keys"], new GdArray(((GdDict)plan["edges"]).Keys), where + " edge key order");
                n++;
            }
            Finish(n);
        }

        /// <summary>
        /// Every capture layout's embedded structural_plan is Godot's compile of that layout (the generator compiles
        /// last). Recompiling from the JSON cells and from Vector2i cells must reproduce it exactly.
        /// </summary>
        [Test]
        public void StructuralEdgeCompiler_ReproducesCapturedStructuralPlans()
        {
            int n = 0;
            foreach (string tag in LayoutTags)
            {
                if (!Fixtures.Exists(LayoutPath(tag))) continue;
                GdDict captured = Fixtures.ReadDict(LayoutPath(tag)).GetDict("structural_plan");
                Assert.IsNotNull(captured, tag + " has no structural_plan");
                foreach (bool vec in new[] { false, true })
                {
                    GdDict layout = ParseLayout(tag);
                    layout.Erase("structural_plan");
                    if (vec) VecCells(layout);
                    Cmp(captured, new StructuralEdgeCompiler().Compile(layout), tag + (vec ? " (Vector2i cells)" : " (json cells)"));
                    n++;
                }
            }
            Finish(n);
        }

        /// <summary>
        /// Stages 1-2 of the capture pipeline (ShipLayoutGenerator: TemplateSelector then RoomAssigner with the
        /// variant selector) reproduce the template and the room ids / roles / variants of every capture layout.
        /// </summary>
        [Test]
        public void TemplateSelectorAndRoomAssigner_ReproduceCapturedPipelineRooms()
        {
            int n = 0;
            foreach (string tag in LayoutTags)
            {
                string recipePath = $"{LayoutDir}/recipe_{tag}.json";
                if (!Fixtures.Exists(recipePath) || !Fixtures.Exists(LayoutPath(tag))) continue;
                GdDict recipe = Fixtures.ReadDict(recipePath);
                GdDict layout = ParseLayout(tag);
                GdDict bpData = recipe.GetDict("blueprint");
                var bp = new ShipBlueprint(bpData.GetInt("size"), bpData.GetInt("condition"), bpData.GetInt("seed_value"));
                GdDict archetype = GdJson.ParseDict(GdJson.Stringify(recipe.GetDictOrEmpty("archetype")));
                string biome = recipe.GetString("biome_id");

                var selector = new TemplateSelector();
                TopologyTemplate template = recipe.GetBool("extended_templates")
                    ? selector.SelectWithOptions(bp, archetype, true, true)
                    : selector.Select(bp, archetype);
                Cmp(layout["template_id"], template.Id, tag + " template_id");

                List<GdDict> plan = new RoomAssigner().AssignWithSelector(
                    template, bp, archetype, biome.Length > 0 ? new RoomVariantSelector() : null, biome);
                var got = new List<string>();
                foreach (GdDict room in plan) got.Add(room.GetString("id") + "|" + room.GetString("role") + "|" + room.GetString("variant"));
                var want = new List<string>();
                foreach (GdDict room in layout.GetArray("rooms"))
                    want.Add(room.GetString("id") + "|" + room.GetString("role") + "|" + room.GetString("variant"));
                GdString.SortStrings(got);
                GdString.SortStrings(want);
                Cmp(GdString.ToGdArray(want), GdString.ToGdArray(got), tag + " rooms");
                n++;
            }
            Finish(n);
        }

        [Test]
        public void FirstRunContract_MatchesGodot()
        {
            GdDict exp = Expected("first_run_contract");
            var frc = new FirstRunContract();
            bool loaded = frc.LoadContract();
            MethodInfo hazard = typeof(FirstRunContract).GetMethod("HasAnyRequiredHazard", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(hazard);
            var cases = new GdArray();
            var candidates = new GdDict();
            foreach (GdDict c in exp.GetArray("cases"))
            {
                string tag = c.GetString("layout");
                GdDict layout = ParseLayout(tag);
                GdDict slice = GdJson.ParseDict(Fixtures.ReadText($"{LayoutDir}/gameplay_slice_{tag}.json"));
                cases.Append(new GdDict
                {
                    { "layout", tag },
                    { "valid", frc.Validate(layout, slice) },
                    { "valid_no_slice", frc.Validate(layout, new GdDict()) },
                    { "hazard", hazard.Invoke(frc, new object[] { layout, slice, GdArray.Of("fire_zone", "breach_zone") }) },
                });
                if (tag.EndsWith("small_wrecked_ext", StringComparison.Ordinal))
                    candidates[long.Parse(tag.Substring(1, tag.IndexOf('_') - 1))] = new GdDict { { "layout", layout }, { "gameplay_slice", slice } };
            }
            var forced = new GdDict();
            foreach (string tag in new[] { "s42_breach_field_standard", "s42_dead_fleet_standard" })
            {
                GdDict layout = ParseLayout(tag);
                layout["encounters"] = GdArray.Of(new GdDict { { "id", "x" } });
                layout["breach_zones"] = GdArray.Of(new GdDict { { "id", "b" } });
                forced[tag] = frc.Validate(layout, GdJson.ParseDict(Fixtures.ReadText($"{LayoutDir}/gameplay_slice_{tag}.json")));
            }
            var got = new GdDict
            {
                { "loaded", loaded }, { "contract", frc.Contract }, { "cases", cases }, { "forced", forced },
                { "pick_seed_null", frc.PickSeed(null) }, { "pick_seed_candidates", frc.PickSeed(candidates) },
                { "pick_seed_empty", frc.PickSeed(new GdDict()) },
                { "engine_version", exp["engine_version"] },
            };
            Cmp(exp, got, "first_run_contract");
            Finish(exp.GetArray("cases").Count);
        }
    }
}
