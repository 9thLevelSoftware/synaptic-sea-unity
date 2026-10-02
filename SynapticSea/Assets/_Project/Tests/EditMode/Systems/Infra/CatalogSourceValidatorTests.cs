using System.IO;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Session;

namespace SynapticSea.Tests.Systems
{
    public class CatalogSourceValidatorTests : InfraDataTestBase
    {
        static GdDict Catalog(params string[] ids)
        {
            var items = new GdDict();
            foreach (string id in ids) items[id] = new GdDict { { "category", "part" }, { "weight", 1.0 }, { "max_stack", 10L } };
            return new GdDict { { "items", items }, { "recipes", new GdArray() }, { "consumers", new GdArray() }, { "components", new GdDict() }, { "skills", new GdDict() }, { "books", new GdDict() } };
        }

        static GdDict Edge(string id, GdDict inputs, GdDict outputs, bool diagnostic = false) => new GdDict
        {
            { "edge_id", id }, { "producer_id", id }, { "consumer_id", id }, { "kind", "loot" },
            { "inputs", inputs }, { "outputs", outputs }, { "tool_ids", new GdArray() },
            { "registration_path", "live.cs:Producer" }, { "diagnostic", diagnostic }, { "finite", true },
        };

        static GdDict Graph(params GdDict[] edges) => new GdDict { { "edges", new GdArray(edges) } };
        static GdDict Quant(string id, long count = 1) => new GdDict { { id, count } };

        static void Require(GdDict catalog, string consumer, string item, string path, string kind = "item")
        {
            catalog.GetArrayOrEmpty("consumers").Add(new GdDict
            {
                { "consumer_id", consumer }, { "item_id", item }, { "reference_kind", kind }, { "source_path", path },
            });
        }

        static GdDict Find(GdDict result, string code, string consumer, string id)
        {
            foreach (object value in result.GetArrayOrEmpty("errors"))
                if (value is GdDict row && row.GetString("code") == code && row.GetString("consumer_id") == consumer && row.GetString("item_id") == id) return row;
            var relevant = new GdArray();
            foreach (object value in result.GetArrayOrEmpty("errors"))
                if (value is GdDict row && row.GetString("consumer_id") == consumer && relevant.Count < 8) relevant.Add(row);
            Assert.Fail("Expected " + code + " for " + consumer + " / " + id + ". Relevant errors: " + GdJson.Stringify(relevant));
            return new GdDict();
        }

        [Test]
        public void ProductionReferencesRequireDefinitionsWithExactCallerPath()
        {
            GdDict catalog = Catalog("gauze");
            Require(catalog, "unbolt", "wrench", "work.json:actions.unbolt.tool_class", "tool");
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(), new GdDict());
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual("work.json:actions.unbolt.tool_class", Find(result, "missing_definition", "unbolt", "wrench").GetString("source_path"));
        }

        [Test]
        public void ProductionReferencesRequireRegisteredProducer()
        {
            GdDict catalog = Catalog("gauze");
            Require(catalog, "surgery", "gauze", "surgery.cs:Precheck");
            GdDict unbound = Edge("future_gauze", new GdDict(), Quant("gauze"));
            unbound["registration_path"] = "";
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(unbound), new GdDict());
            Assert.IsFalse(result.GetBool("ok"));
            Find(result, "missing_source", "surgery", "gauze");
            Find(result, "unregistered_producer", "future_gauze", "gauze");
        }

        [Test]
        public void DiagnosticEdgesDoNotSatisfyProduction()
        {
            GdDict catalog = Catalog("gauze");
            Require(catalog, "surgery", "gauze", "surgery.cs:Precheck");
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(Edge("grant", new GdDict(), Quant("gauze"), true)), new GdDict());
            Find(result, "missing_source", "surgery", "gauze");
            Assert.IsFalse(result.GetBool("ok"));
        }

        [Test]
        public void SeedlessAndInputCycleRejects()
        {
            GdDict catalog = Catalog("coolant", "reagent", "part");
            Require(catalog, "repair", "part", "systems.json:required_parts");
            GdDict inputs = Quant("coolant"); inputs["reagent"] = 1L;
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(
                Edge("coolant_seed", new GdDict(), Quant("coolant")),
                Edge("craft", inputs, Quant("part")), Edge("deconstruct", Quant("part"), Quant("reagent"))), new GdDict());
            Find(result, "missing_source", "repair", "part");
            Assert.IsFalse(result.GetBool("ok"), "A seed for only one AND input cannot bootstrap the cycle.");
        }

        [Test]
        public void SeededReagentSpendingCycleIsPotentialSourceWithFiniteCaveat()
        {
            GdDict catalog = Catalog("reagent", "part");
            Require(catalog, "repair", "part", "systems.json:required_parts");
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(
                Edge("seed", new GdDict(), Quant("reagent", 2)),
                Edge("craft", Quant("reagent", 2), Quant("part")), Edge("deconstruct", Quant("part"), Quant("reagent"))), new GdDict());
            Assert.IsTrue(result.GetBool("ok"));
            Assert.IsTrue(result.GetArrayOrEmpty("potential_item_ids").Contains("part"));
            Assert.IsNotEmpty(result.GetArrayOrEmpty("warnings"), "Potential closure must not certify sustainable quantities.");
        }

        [Test]
        public void InvalidQuantitiesRejectBeforeClosure()
        {
            GdDict catalog = Catalog("part");
            Require(catalog, "repair", "part", "systems.json:required_parts");
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(Edge("bad", new GdDict(), Quant("part", 0))), new GdDict());
            Find(result, "invalid_quantity", "bad", "part");
            Find(result, "missing_source", "repair", "part");
        }

        [Test]
        public void MissingToolSourceDoesNotEnableSalvage()
        {
            GdDict catalog = Catalog("wrench", "form");
            Require(catalog, "install", "form", "components.json:item_form");
            GdDict salvage = Edge("salvage", new GdDict(), Quant("form")); salvage["tool_ids"] = GdArray.Of("wrench");
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(salvage), new GdDict());
            Find(result, "missing_source", "install", "form");
        }

        [Test]
        public void MissingRecipeStationTierBookAndSkillProduceExactDiagnostics()
        {
            GdDict catalog = Catalog("part", "input");
            catalog.GetArrayOrEmpty("recipes").Add(new GdDict
            {
                { "recipe_id", "advanced" }, { "source_path", "recipes.json:recipes[0]" }, { "ingredients", Quant("input") },
                { "produces", new GdDict { { "item_id", "part" }, { "quantity", 1L } } },
                { "station_kind", "fabricator" }, { "station_tier_min", 2L }, { "required_skill", "fabrication" },
                { "required_skill_level", 1L }, { "knowledge_source", "book" },
            });
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(Edge("seed", new GdDict(), Quant("input"))), new GdDict());
            Find(result, "missing_station", "advanced", "fabricator");
            Find(result, "missing_tier_source", "advanced", "fabricator");
            Find(result, "missing_book_mapping", "advanced", "");
            Find(result, "missing_skill_definition", "advanced", "fabrication");
        }

        [Test]
        public void DeferredRecipeRequiresAnExplicitReasonAndOwner()
        {
            GdDict catalog = Catalog("part");
            Require(catalog, "future", "part", "recipes.json:future");
            GdDict invalidManifest = new GdDict { { "future", new GdDict { { "state", "deferred" } } } };
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(), invalidManifest);
            Find(result, "invalid_disposition", "future", "");
            Find(result, "missing_source", "future", "part");
        }

        [Test]
        public void RealCatalogNormalizationPreservesLiveMergeAndEveryRecipeOrigin()
        {
            GdDict catalog = CatalogSourceValidator.LoadProductionCatalog();
            GdDict items = catalog.GetDictOrEmpty("items");
            Assert.IsTrue(V.VariantEquals(ItemDefs.LoadDefinitions(), items));
            Assert.AreEqual(62, catalog.GetArrayOrEmpty("recipes").Count);
            Assert.AreEqual(11, catalog.GetDictOrEmpty("components").Count);
            foreach (object value in catalog.GetArrayOrEmpty("recipes"))
                StringAssert.StartsWith(CraftingState.RECIPE_DEFINITIONS_PATH + ":recipes[", ((GdDict)value).GetString("source_path"));
            Assert.IsFalse(items.Has("wrench"));
            Assert.IsFalse(items.Has("reactor_console"));
        }

        [Test]
        public void RealGeneratedAuthoredContentsOverrideRandomTableSources()
        {
            GdDict catalog = CatalogSourceValidator.LoadProductionCatalog();
            var generator = new ShipGenerator { RichExpeditions = true };
            generator.ConfigureRunContext("breach_field", "standard");
            ShipDocuments documents = generator.GenerateFromSeed(17, 1, 0);
            Assert.IsNotNull(documents);
            GdDict slice = documents.GameplaySlice;
            string medicalProducer = "";
            foreach (object value in slice.GetArrayOrEmpty("loot_containers"))
                if (value is GdDict container)
                    foreach (object stackValue in container.GetArrayOrEmpty("contents"))
                        if (stackValue is GdDict stack && stack.GetString("item_id") == "field_medkit") medicalProducer = container.GetString("id");
            Assert.IsNotEmpty(medicalProducer, "Actual generator must register the finite medical supplies.");
            GdDict graph = CatalogSourceValidator.NormalizeSources(catalog, new GdDict { { "slices", GdArray.Of(slice) } });
            var ids = new GdArray();
            foreach (object value in graph.GetArrayOrEmpty("edges"))
                if (value is GdDict edge && edge.GetString("producer_id") == medicalProducer)
                    foreach (object id in edge.GetDictOrEmpty("outputs").Keys) ids.Add(id);
            Assert.IsTrue(ids.Contains("field_medkit"));
            Assert.IsTrue(ids.Contains("bandage_kit"));
            Assert.IsFalse(ids.Contains("scrap_metal"), "Authored contents suppress random table rolls in LootContainer.");
        }

        [Test]
        public void RealProductionCatalogRetainsExactKnownInvalidReferences()
        {
            GdDict catalog = CatalogSourceValidator.LoadProductionCatalog();
            GdDict result = new CatalogSourceValidator().Validate(catalog, CatalogSourceValidator.NormalizeSources(catalog, new GdDict()), new GdDict());
            Find(result, "missing_definition", "unbolt_component", "wrench");
            Find(result, "missing_definition", "reactor_console", "reactor_console");
            Find(result, "missing_source", "medbay_surgery", "medical_gauze");
            Find(result, "missing_book_mapping", "craft_thruster_nozzle", "");
            Assert.AreEqual(62, result.GetDictOrEmpty("denominators").GetInt("recipes"));
        }

        [Test]
        public void ValidationDoesNotMutateInputs()
        {
            GdDict catalog = Catalog("part"); Require(catalog, "repair", "part", "systems.json:required_parts");
            GdDict graph = Graph(Edge("seed", new GdDict(), Quant("part")));
            string before = GdJson.Stringify(catalog) + GdJson.Stringify(graph);
            GdDict result = new CatalogSourceValidator().Validate(catalog, graph, new GdDict());
            Assert.IsTrue(result.GetBool("ok"));
            Assert.AreEqual(before, GdJson.Stringify(catalog) + GdJson.Stringify(graph));
            Assert.AreEqual(1, result.GetArrayOrEmpty("coverage").Count);
        }

        [Test]
        public void OrdinaryGoldenBootExposesActualLiveRegistrationsAndRetainsInvalidDiagnostics()
        {
            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            Assert.IsTrue(rig.Session.GetPlayableSummary().GetBool("loaded"));
            Assert.IsTrue(rig.Scene.HasPlayer);
            GdDict registrations = rig.Session.GetCatalogSourceRegistrations();
            Assert.AreEqual(2, registrations.GetArrayOrEmpty("pickups").Count);
            Assert.AreEqual(rig.Session.LootContainers.Count, registrations.GetArrayOrEmpty("containers").Count);
            Assert.IsTrue(registrations.GetDictOrEmpty("stations").Has("workbench"));
            Assert.IsTrue(registrations.GetDictOrEmpty("stations").Has("field_crafting"));
            GdDict report = rig.Session.GetCatalogValidationReport();
            Assert.IsFalse(report.GetBool("ok"));
            Find(report, "missing_definition", "unbolt_component", "wrench");
            Find(report, "missing_source", "medbay_surgery", "medical_gauze");
            string directory = System.Environment.GetEnvironmentVariable("SYNAPTIC_CATALOG_EVIDENCE_DIR");
            if (string.IsNullOrEmpty(directory)) directory = Path.Combine(Fixtures.RepoRoot, "artifacts", "foundation-bounded");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "catalog-validation.json"), GdJson.Stringify(report, "  "));
        }

        [Test]
        public void DeletingActualOxygenPumpPickupRemovesItsOnlyLiveSource()
        {
            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            GdDict catalog = CatalogSourceValidator.LoadProductionCatalog();
            Require(catalog, "oxygen_install", "portable_oxygen_pump", "RunSession.Objectives.cs:BuildToolPickup");
            GdDict registrations = rig.Session.GetCatalogSourceRegistrations();
            GdDict goodGraph = CatalogSourceValidator.NormalizeSources(catalog, registrations);
            GdDict good = new CatalogSourceValidator().Validate(catalog, goodGraph, new GdDict());
            Assert.IsTrue(good.GetArrayOrEmpty("potential_item_ids").Contains("portable_oxygen_pump"));
            GdArray pickups = registrations.GetArrayOrEmpty("pickups");
            for (int i = pickups.Count - 1; i >= 0; i--)
                if (((GdDict)pickups[i]).GetString("item_id") == "portable_oxygen_pump") pickups.RemoveAt(i);
            GdDict bad = new CatalogSourceValidator().Validate(catalog, CatalogSourceValidator.NormalizeSources(catalog, registrations), new GdDict());
            Find(bad, "missing_source", "oxygen_install", "portable_oxygen_pump");
        }

        [Test]
        public void MalformedSourceGraphRejectsInsteadOfSilentlyPassing()
        {
            GdDict result = new CatalogSourceValidator().Validate(Catalog("part"), new GdDict { { "edges", "broken" } }, new GdDict());
            Assert.IsFalse(result.GetBool("ok"));
            Find(result, "invalid_document", "catalog", "edges");
        }

        [Test]
        public void RecipeQuantityRejectsEvenWithoutItsProducerEdge()
        {
            GdDict catalog = Catalog("part", "input");
            catalog.GetArrayOrEmpty("recipes").Add(new GdDict
            {
                { "recipe_id", "bad_recipe" }, { "station_kind", "workbench" }, { "ingredients", Quant("input", 0) },
                { "produces", new GdDict { { "item_id", "part" }, { "quantity", 1L } } },
            });
            GdDict graph = Graph(); graph["stations"] = new GdDict { { "workbench", new GdDict { { "registration_path", "live.cs:BuildStation" } } } };
            GdDict result = new CatalogSourceValidator().Validate(catalog, graph, new GdDict());
            Find(result, "invalid_quantity", "bad_recipe", "input");
        }

        [Test]
        public void EmptyStationRegistrationCannotSatisfyRecipeGate()
        {
            GdDict catalog = Catalog("part", "input");
            catalog.GetArrayOrEmpty("recipes").Add(new GdDict
            {
                { "recipe_id", "recipe" }, { "station_kind", "workbench" }, { "ingredients", Quant("input") },
                { "produces", new GdDict { { "item_id", "part" }, { "quantity", 1L } } },
            });
            GdDict graph = Graph(); graph["stations"] = new GdDict { { "workbench", new GdDict() } };
            GdDict result = new CatalogSourceValidator().Validate(catalog, graph, new GdDict());
            Find(result, "missing_station", "recipe", "workbench");
        }

        [Test]
        public void LiveSpatialPrecheckForwardsTierAndDeliberateOmissionIsStillDiagnosed()
        {
            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            rig.Session.CraftingState.GetOrCreateStation("fabricator").ApplyComponentTier(2);
            GdDict report = rig.Session.GetCatalogValidationReport();
            foreach (object value in report.GetArrayOrEmpty("errors"))
                if (value is GdDict row)
                    Assert.IsFalse(row.GetString("code") == "tier_not_forwarded" && row.GetString("item_id") == "fabricator", "Actual bound station now forwards its model tier.");

            GdDict catalog = CatalogSourceValidator.LoadProductionCatalog();
            GdDict sources = CatalogSourceValidator.NormalizeSources(catalog, rig.Session.GetCatalogSourceRegistrations());
            sources.GetDictOrEmpty("stations").GetDictOrEmpty("fabricator")["precheck_tier"] = 0L;
            GdDict deliberateFailure = new CatalogSourceValidator().Validate(catalog, sources, new GdDict());
            Find(deliberateFailure, "tier_not_forwarded", "craft_sensor_module", "fabricator");
            Find(deliberateFailure, "tier_not_forwarded", "craft_thruster_nozzle", "fabricator");
        }

        [Test]
        public void ActualRecyclerRegistrationRetainsItsConsumedInputAndModelOutput()
        {
            SessionHarness.Rig rig = SessionHarness.CreateGolden();
            GdDict graph = CatalogSourceValidator.NormalizeSources(CatalogSourceValidator.LoadProductionCatalog(), rig.Session.GetCatalogSourceRegistrations());
            GdDict recycler = null;
            foreach (object value in graph.GetArrayOrEmpty("edges"))
                if (value is GdDict edge && edge.GetString("producer_id") == "water_recycler") recycler = edge;
            Assert.IsNotNull(recycler, "The actual bound production station must have a source edge.");
            Assert.AreEqual(1L, recycler.GetDictOrEmpty("inputs").GetInt("contaminated_water"));
            Assert.AreEqual(1L, recycler.GetDictOrEmpty("outputs").GetInt(rig.Session.WaterRecyclerState.OutputItemId));
            StringAssert.Contains("ProductionStation", recycler.GetString("registration_path"));
        }

        [TestCase("diagnostic")]
        [TestCase("deferred")]
        public void ReviewProducerDispositionCannotSupplyAnotherProductionConsumer(string state)
        {
            GdDict catalog = Catalog("part"); Require(catalog, "repair", "part", "repair.cs:required_parts");
            GdDict edge = Edge("future_source", new GdDict(), Quant("part")); edge["consumer_id"] = "source_caller";
            GdDict manifest = new GdDict { { "future_source", new GdDict { { "state", state }, { "owner", "content" }, { "reason", "unshipped" } } } };
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(edge), manifest);
            Find(result, "missing_source", "repair", "part");
            Assert.IsFalse(result.GetArrayOrEmpty("potential_item_ids").Contains("part"));
            bool dispositionRetained = false;
            foreach (object value in result.GetArrayOrEmpty("dispositions"))
                if (value is GdDict row && row.GetString("consumer_id") == "future_source" && row.GetString("state") == state) dispositionRetained = true;
            Assert.IsTrue(dispositionRetained);
        }

        [Test]
        public void ReviewMalformedProducerDeferralReportsRequiredOwnerAndReason()
        {
            GdDict catalog = Catalog("part"); Require(catalog, "repair", "part", "repair.cs:required_parts");
            GdDict manifest = new GdDict { { "future_source", new GdDict { { "state", "deferred" } } } };
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(Edge("future_source", new GdDict(), Quant("part"))), manifest);
            Find(result, "invalid_disposition", "future_source", "");
            Assert.IsFalse(result.GetBool("ok"));
        }

        [TestCase("string")]
        [TestCase("dictionary")]
        [TestCase("null")]
        public void ReviewMalformedPresentToolListCannotBecomeAnUngatedSeed(string shape)
        {
            GdDict catalog = Catalog("part"); Require(catalog, "repair", "part", "repair.cs:required_parts");
            GdDict edge = Edge("salvage", new GdDict(), Quant("part"));
            edge["tool_ids"] = shape == "string" ? (object)"wrench" : shape == "dictionary" ? new GdDict { { "wrench", 1L } } : null;
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(edge), new GdDict());
            Find(result, "invalid_tool_ids", "salvage", "");
            Find(result, "missing_source", "repair", "part");
            Assert.IsFalse(result.GetArrayOrEmpty("potential_item_ids").Contains("part"));
        }

        [Test]
        public void ReviewValidEmptyToolListPreservesAnUngatedRegisteredProducer()
        {
            GdDict catalog = Catalog("part"); Require(catalog, "repair", "part", "repair.cs:required_parts");
            GdDict result = new CatalogSourceValidator().Validate(catalog, Graph(Edge("source", new GdDict(), Quant("part"))), new GdDict());
            Assert.IsTrue(result.GetBool("ok"));
            Assert.IsTrue(result.GetArrayOrEmpty("potential_item_ids").Contains("part"));
        }

        [TestCase("salvage", true)]
        [TestCase("workbench", false)]
        public void ReviewActualDeconstructionRecipeUsesItsLiveSalvageCaller(string stationKind, bool sourceExpected)
        {
            GdDict actual = null;
            foreach (object value in CatalogSourceValidator.LoadProductionCatalog().GetArrayOrEmpty("recipes"))
                if (value is GdDict recipe && recipe.GetString("recipe_id") == "deconstruct_scrap") actual = recipe;
            Assert.IsNotNull(actual);
            Assert.AreEqual("workbench", actual.GetString("station_kind"), "Authored content is retained.");
            var inventory = new InventoryState(); inventory.AddItem("scrap_metal", 1L);
            Assert.IsTrue(new DeconstructionResolver().CanDeconstruct("deconstruct_scrap", inventory), "Actual salvage resolver accepts this seeded input.");
            GdDict catalog = Catalog("scrap_metal", "ferrous_shard");
            catalog.GetArrayOrEmpty("recipes").Add(actual);
            Require(catalog, "salvage_result", "ferrous_shard", "salvage.cs:output");
            GdDict registrations = new GdDict
            {
                { "stations", new GdDict { { stationKind, new GdDict { { "registration_path", "live.cs:BuildStation" } } } } },
                { "pickups", GdArray.Of(new GdDict { { "item_id", "scrap_metal" }, { "producer_id", "scrap_seed" }, { "registration_path", "live.cs:Pickup" } }) },
            };
            GdDict result = new CatalogSourceValidator().Validate(catalog, CatalogSourceValidator.NormalizeSources(catalog, registrations), new GdDict());
            if (sourceExpected)
            {
                Assert.IsTrue(result.GetBool("ok"), GdJson.Stringify(result.GetArrayOrEmpty("errors")));
                Assert.IsTrue(result.GetArrayOrEmpty("potential_item_ids").Contains("ferrous_shard"));
            }
            else
            {
                Find(result, "missing_station", "deconstruct_scrap", "salvage");
                Find(result, "missing_source", "salvage_result", "ferrous_shard");
            }
            StringAssert.StartsWith(CraftingState.RECIPE_DEFINITIONS_PATH + ":recipes[", actual.GetString("source_path"));
        }

        sealed class OverrideResourceReader : IResourceReader
        {
            readonly IResourceReader _inner;
            readonly string _path;
            readonly string _replacement;
            public OverrideResourceReader(IResourceReader inner, string path, string replacement) { _inner = inner; _path = path; _replacement = replacement; }
            public bool Exists(string path) => path == _path ? _replacement != null : _inner.Exists(path);
            public string ReadText(string path) => path == _path ? _replacement : _inner.ReadText(path);
        }

        static GdDict NormalizeWithResourceOverride(string path, string replacement)
        {
            IResourceReader previous = CoreServices.Resources;
            try
            {
                CatalogRegistry.Clear();
                CoreServices.Resources = new OverrideResourceReader(previous, path, replacement);
                return CatalogSourceValidator.LoadProductionCatalog();
            }
            finally { CoreServices.Resources = previous; CatalogRegistry.Clear(); }
        }

        [TestCase(CraftingState.RECIPE_DEFINITIONS_PATH)]
        [TestCase(ComponentCatalog.DEFAULT_PATH)]
        [TestCase(CatalogSourceValidator.SYSTEMS_PATH)]
        [TestCase(CatalogSourceValidator.LOOT_PATH)]
        [TestCase(CatalogSourceValidator.SKILL_TREE_PATH)]
        [TestCase(RunSession.HYDROPONICS_CROPS_CONFIG_PATH)]
        [TestCase(PlayerProgressionState.DEFAULT_SKILLS_PATH)]
        [TestCase(PlayerProgressionState.DEFAULT_BOOKS_PATH)]
        [TestCase(WorkActionCatalog.DEFAULT_PATH)]
        public void ReviewMissingProductionResourceRetainsExactDiagnosticPath(string path)
        {
            GdDict catalog = NormalizeWithResourceOverride(path, null);
            GdDict result = new CatalogSourceValidator().Validate(catalog, CatalogSourceValidator.NormalizeSources(catalog, new GdDict()), new GdDict());
            Assert.AreEqual(path, Find(result, "missing_catalog", "catalog", "").GetString("source_path"));
            Assert.IsFalse(result.GetBool("ok"));
        }

        [TestCase("{", "malformed_catalog", "", "")]
        [TestCase("[]", "malformed_catalog", "", "")]
        [TestCase("{\"recipes\":\"broken\"}", "invalid_catalog_shape", "recipes", ":recipes")]
        [TestCase("{\"recipes\":[\"bad\"]}", "invalid_catalog_row", "recipes[0]", ":recipes[0]")]
        public void ReviewMalformedRecipeResourceRetainsShapeOrRowOrigin(string json, string code, string id, string suffix)
        {
            GdDict catalog = NormalizeWithResourceOverride(CraftingState.RECIPE_DEFINITIONS_PATH, json);
            GdDict result = new CatalogSourceValidator().Validate(catalog, CatalogSourceValidator.NormalizeSources(catalog, new GdDict()), new GdDict());
            Assert.AreEqual(CraftingState.RECIPE_DEFINITIONS_PATH + suffix, Find(result, code, "catalog", id).GetString("source_path"));
            Assert.IsFalse(result.GetBool("ok"));
        }

        [TestCase(ComponentCatalog.DEFAULT_PATH, "{\"components\":\"bad\",\"role_sets\":{}}", "components", "invalid_catalog_shape")]
        [TestCase(ComponentCatalog.DEFAULT_PATH, "{\"components\":{\"broken\":\"bad\"},\"role_sets\":{}}", "components.broken", "invalid_catalog_row")]
        [TestCase(CatalogSourceValidator.SYSTEMS_PATH, "{\"systems\":\"bad\"}", "systems", "invalid_catalog_shape")]
        [TestCase(CatalogSourceValidator.SYSTEMS_PATH, "{\"systems\":[\"bad\"]}", "systems[0]", "invalid_catalog_row")]
        [TestCase(CatalogSourceValidator.SKILL_TREE_PATH, "{\"skill_prerequisites\":\"bad\"}", "skill_prerequisites", "invalid_catalog_shape")]
        [TestCase(RunSession.HYDROPONICS_CROPS_CONFIG_PATH, "{\"crops\":\"bad\"}", "crops", "invalid_catalog_shape")]
        [TestCase(WorkActionCatalog.DEFAULT_PATH, "{\"actions\":\"bad\"}", "actions", "invalid_catalog_shape")]
        public void ReviewMalformedNonRecipeResourceRetainsExactMemberOrigin(string path, string json, string member, string code)
        {
            GdDict catalog = NormalizeWithResourceOverride(path, json);
            GdDict result = new CatalogSourceValidator().Validate(catalog, CatalogSourceValidator.NormalizeSources(catalog, new GdDict()), new GdDict());
            Assert.AreEqual(path + ":" + member, Find(result, code, "catalog", member).GetString("source_path"));
        }
    }
}
