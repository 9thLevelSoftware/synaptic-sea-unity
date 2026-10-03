using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    // ARTIFACT ONLY. ROOT must explicitly release this file into Tests before compiling/running.
    // Two genuine provisioned-session capacity cases. No frozen-owner import or fabricated authority.
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class PaidCraftSaveCapacityTests : InfraDataTestBase
    {
        const string Recipe = "weld_plating";
        const string Kind = "workbench";
        const string Slot = "world";
        const long SemanticLimit = 100000L;
        const long RawLimit = 100000L;
        const int DepthLimit = 128;
        readonly List<RunSession> _sessions = new List<RunSession>();
        IEngineInfo _oldEngine;

        [SetUp] public void UseMatchingEngine()
        {
            _oldEngine = CoreServices.Engine;
            CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
        }
        [TearDown] public void DisposeCapacitySessions()
        {
            try { foreach (RunSession s in _sessions) s.Dispose(); _sessions.Clear(); }
            finally { CoreServices.Engine = _oldEngine; }
        }

        static void Need(bool pass, string why) => Assert.IsTrue(pass, "CAPACITY_PREREQUISITE: " + why);
        static string Detail(GdDict result) => "reason=" + result.GetString("reason") + "; detail=" + result.GetString("detail");
        static GdDict Paid(GdDict owner) => owner.GetDictOrEmpty("participating_state").GetDictOrEmpty("paid_crafting");
        static GdDict Job(GdDict owner, string id) => Paid(owner).GetDictOrEmpty("jobs").GetDictOrEmpty(id);

        SessionHarness.Rig Boot(bool components)
        {
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.EnableComponentIntegration = components;
            deps.EnablePaidCrafting = true;
            rig.Session = RunSession.Create(deps);
            _sessions.Add(rig.Session);
            RunSession s = rig.Session;
            Need(s.PlayableStarted && s.PaidCraftingEnabled && s.CompleteGenerationEnabled, "actual paid new boot: " + s.LastFailureReason);
            Need(s.ComponentIntegrationEnabled == components, "mode is actual boot capability");
            Need(!s.AwayFromStart && s.HomeShip != null && s.LifeboatShip != null &&
                s.HomeShip.SceneRoot != null && ReferenceEquals(s.CurrentShip, s.HomeShip), "actual original home and retained lifeboat");
            Need(s.CurrentOccupancy != null && s.CurrentOccupancy.ShipId == s.HomeShip.ShipId, "actual original-home occupancy");
            Need(rig.Storage != null, "fresh fixture MemoryStorage");
            s.ThreatManager.Threats.Clear();
            s.InventoryState.Items.Clear();
            foreach (object skill in s.PlayerProgression.Skills.Keys.ToArray()) s.PlayerProgression.Skills[skill] = 4L;
            s.VitalsState.Stamina = s.VitalsState.MaxStamina;
            return rig;
        }

        static GdDict ValidOwner(RunSession s, bool components, string stage)
        {
            var owner = s.CapturePaidCraftingDomain();
            Need(owner.GetInt("schema_version") == 3L, stage + " actual schema3 owner");
            Need(owner.GetString("domain_mode") == (components ? "components_and_craft" : "craft_only"), stage + " actual owner mode");
            Need(DomainBundle.TryCreate(owner, out _, out string reason), stage + " strict DomainBundle/proofs: " + reason);
            Need(s.ValidatePaidCraftingRestore(owner, out reason), stage + " current-session/original-home context: " + reason);
            Need(Paid(owner).GetString("run_id") == s.RunId, stage + " original run identity");
            Need(Paid(owner).GetString("actor_id").Length > 0, stage + " real actor identity");
            Need(ComponentDomainCodec.TryDecode(ComponentDomainCodec.Encode(owner), out GdDict decoded, out reason), stage + " unchanged Core codec: " + reason);
            Equal(owner, decoded, stage + " exact semantic codec roundtrip");
            if (!components)
            {
                Need(owner.GetDictOrEmpty("registry").GetDictOrEmpty("instances").IsEmpty, stage + " ordinary empty component registry");
                foreach (string key in new[] { "holders", "machinery", "physical_slots", "component_work" })
                    Need(owner.GetDictOrEmpty(key).IsEmpty, stage + " ordinary inactive " + key);
            }
            return owner;
        }

        static string Start(RunSession s, int ordinal)
        {
            GdDict recipe = s.CraftingState.GetRecipe(Recipe);
            Need(!recipe.IsEmpty, "real catalog recipe");
            Need(s.CraftingStations.Any(st => st.IsValid && st.StationKind == Kind &&
                ReferenceEquals(st.Parent, s.HomeShip.SceneRoot)), "actual registered original-home wrapper");
            var station = s.CraftingState.GetStation(Kind);
            Need(station != null, "existing actual station model");
            station.SetPower(true);
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
            {
                string item = V.Str(ingredient.Key);
                long amount = V.I64(ingredient.Value);
                Need(amount > 0 && s.InventoryState.GetQuantity(item) == 0L, "one provisioned real ingredient set " + item);
                Need(s.InventoryState.AddItem(item, amount) == amount, "normal inventory provision " + item);
            }
            GdDict started = s.RequestPaidCraft(Kind, Recipe, "capacity-real-start-" + ordinal);
            Need(started.GetBool("ok") && started.GetBool("committed"), "actual paid start " + ordinal + ": " + Detail(started));
            string id = started.GetString("job_id");
            Need(id.Length > 0 && started.GetString("commit_id").Length > 0, "actual paid identities");
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
                Need(s.InventoryState.GetQuantity(V.Str(ingredient.Key)) == 0L, "exactly one paid debit");
            GdDict job = Job(s.CapturePaidCraftingDomain(), id);
            Need(job.GetString("status") == "running" && job.GetString("input_state") == "paid", "genuine paid running job");
            Need(job.GetString("payment_commit_id") == started.GetString("commit_id"), "actual payment receipt binding");
            return id;
        }

#if !SYNAPTIC_DOTNET_TESTS
        [Timeout(360000)]
#endif
        [TestCase(false)]
        [TestCase(true)]
        public void ActualEightyCompletionHistory_SavesSelectsAllOwnerCopies_WithinUnchangedRawBudget(bool components)
        {
            SessionHarness.Rig rig = Boot(components);
            RunSession s = rig.Session;
            GdDict smallOwner = ValidOwner(s, components, "small control");
            // This must succeed BEFORE any large history is generated. A failure here is a fixture/
            // current Save dependency, never the intended high-wire capacity RED.
            Need(s.RequestSaveToSlot(Slot, "world", "Capacity small full-payload control"),
                "SMALL_FULL_PAYLOAD_DEPENDENCY mode=" + components + ": " + Detail(s.LastSaveResult));
            GdDict prior = s.SaveLoadService.SelectGeneration(Slot);
            Need(prior.GetBool("ok"), "actual small full-generation selection: " + Detail(prior));
            GdDict priorRun = SelectedDocument(prior, "run"), priorWorld = SelectedDocument(prior, "world");
            AssertSelected(s, components, prior, smallOwner);
            // Calibrate the source-derived detached DTO projection against an actual accepted payload.
            // These projections never become a commit request or independent admission oracle.
            Project(s, smallOwner, priorRun, priorWorld, out GdDict smallRunProjection, out GdDict smallWorldProjection);
            CompareCounts(Measure(s, smallOwner, components, "run", smallRunProjection, "small-projection"),
                Measure(s, smallOwner, components, "run", priorRun, "small-actual-selected"), "small run projection calibration");
            CompareCounts(Measure(s, smallOwner, components, "world", smallWorldProjection, "small-projection"),
                Measure(s, smallOwner, components, "world", priorWorld, "small-actual-selected"), "small world projection calibration");

            var completed = new Dictionary<string, GdDict>(StringComparer.Ordinal);
            for (int i = 0; i < 80; i++)
            {
                string id = Start(s, i);
                GdDict initial = Job(s.CapturePaidCraftingDomain(), id);
                s.AdvanceCrafting(100.0); // Same actual public progression as frozen performance Programs.
                GdDict owner = s.CapturePaidCraftingDomain(), job = Job(owner, id);
                Need(job.GetString("status") == "completed_delivered", "actual completion " + i);
                foreach (string field in new[] { "payment_commit_id", "recipe_hash", "consumed", "required_seconds", "quality_score", "quality_tier", "quality_multiplier" })
                    Equal(initial.Get(field), job.Get(field), "completion retains paid " + field);
                Need(owner.GetDictOrEmpty("receipts").Has(job.GetString("payment_commit_id")), "payment receipt retained");
                Need(owner.GetDictOrEmpty("receipts").Has(job.GetString("completion_commit_id")), "completion receipt retained");
                Need(s.InventoryState.RemoveItem(s.CraftingState.GetProduces(Recipe).GetString("item_id"), 1L) == 1L,
                    "real completed output removed through normal inventory API");
                completed.Add(id, job.DeepCopy());
            }
            string running = Start(s, 80);
            GdDict largeOwner = ValidOwner(s, components, "80 complete plus one running");
            GdDict jobs = Paid(largeOwner).GetDictOrEmpty("jobs");
            Need(jobs.Count == 81 && jobs.Values.OfType<GdDict>().Count(j => j.GetString("status") == "completed_delivered") == 80 &&
                Job(largeOwner, running).GetString("status") == "running", "exact 80+1 actual retained history");
            foreach (var entry in completed) Equal(entry.Value, jobs.Get(entry.Key), "unpruned real completed job " + entry.Key);
            Need(largeOwner.GetDictOrEmpty("receipts").Values.OfType<GdDict>().Count(r =>
                r.GetDictOrEmpty("result").GetString("operation") == "craft_complete") == 80, "all 80 original completion proofs retained");
            Need(Paid(largeOwner).GetString("actor_id") == Paid(smallOwner).GetString("actor_id") &&
                Paid(largeOwner).GetString("run_id") == Paid(smallOwner).GetString("run_id"), "no reassigned run/actor/context");
            Need(ReferenceEquals(s.CurrentShip, s.HomeShip) && !s.AwayFromStart, "no fabricated replacement home");

            Project(s, largeOwner, priorRun, priorWorld, out GdDict runProjection, out GdDict worldProjection);
            Counts projectedRun = Measure(s, largeOwner, components, "run", runProjection, "large-detached-projection");
            Counts projectedWorld = Measure(s, largeOwner, components, "world", worldProjection, "large-detached-projection");
            Need(projectedRun.Raw <= RawLimit && projectedWorld.Raw <= RawLimit &&
                projectedRun.RawDepth <= DepthLimit && projectedWorld.RawDepth <= DepthLimit,
                "RAW_REMAINDER_DEPENDENCY: R/depth must remain <=100000/128; do not relax a raw limit");
            Need(projectedRun.Copies.All(c => c.Wire > 100000L) && projectedWorld.Copies.All(c => c.Wire > 100000L),
                "HISTORY_SIZE_DEPENDENCY: actual 80+1 owner must exceed old physical transport budget");
            Equal(largeOwner, ValidOwner(s, components, "immediately before public save"), "measurement never alters current owner");
            GdDict liveBefore = LiveParticipants(s);
            object homeBefore = s.HomeShip, rootBefore = s.HomeShip.SceneRoot;
            GdDict selectedBefore = s.SaveLoadService.SelectGeneration(Slot);
            Equal(prior, selectedBefore, "growth never rewrites prior successful selection");
            Dictionary<string, string> bytesBefore = StorageTree(rig.Storage);
            bool saved = s.RequestSaveToSlot(Slot, "world", "Capacity actual 80-completion history");
            GdDict result = s.LastSaveResult.DeepCopy();
            Console.WriteLine("CAPACITY_ACTUAL_SAVE components=" + components + " saved=" + saved + " " + Detail(result));
            Equal(largeOwner, ValidOwner(s, components, "after actual save attempt"), "save attempt retains all owner/proof bytes and participants");
            Equal(liveBefore, LiveParticipants(s), "actual save does not debit/output/XP/progress or mutate raw participants");
            Need(ReferenceEquals(homeBefore, s.HomeShip) && ReferenceEquals(rootBefore, s.HomeShip.SceneRoot), "original context/root unchanged");
            if (!saved)
            {
                EqualStorage(bytesBefore, rig.Storage);
                Equal(selectedBefore, s.SaveLoadService.SelectGeneration(Slot), "refused actual save retains prior exact selected generation");
                // Classify only the actually reached public save result. Any earlier/different failure
                // is a dependency, not a manufactured passing capacity result.
                Need(result.GetString("reason") == "capture_failed" && result.GetString("detail").EndsWith(":node_limit", StringComparison.Ordinal),
                    "DIFFERENT_FIRST_FAILURE_DEPENDENCY: " + Detail(result));
                Assert.IsTrue(saved, "MEANINGFUL_CAPACITY_RED: valid actual " + (components ? "combined" : "ordinary") +
                    " 80+1 session, prior full save/select, Core/context/mirrors and projected R passed; public RequestSaveToSlot returned " + Detail(result));
                return;
            }

            // GREEN is actual selected bytes, not the detached projection or frozen-owner codec probe.
            GdDict selected = s.SaveLoadService.SelectGeneration(Slot);
            Need(selected.GetBool("ok"), "large actual full-payload selection: " + Detail(selected));
            Need(selected.GetString("generation_id") != prior.GetString("generation_id"), "new actual generation published");
            AssertSelected(s, components, selected, largeOwner);
            GdDict actualRun = SelectedDocument(selected, "run"), actualWorld = SelectedDocument(selected, "world");
            CompareCounts(projectedRun, Measure(s, largeOwner, components, "run", actualRun, "large-actual-selected"), "actual run counts match projection");
            CompareCounts(projectedWorld, Measure(s, largeOwner, components, "world", actualWorld, "large-actual-selected"), "actual world counts match projection");
            Dictionary<string, string> selectedBytes = StorageTree(rig.Storage);
            GdDict retained = s.SaveLoadService.ReadGeneration(prior.GetString("run_id"), Slot, prior.GetString("generation_id"), prior.GetString("manifest_sha256"));
            Need(retained.GetBool("ok"), "previous valid generation remains independently readable");
            Equal(prior.Get("payloads"), retained.Get("payloads"), "prior payload remains exact");
            WorldSnapshot loaded = s.SaveLoadService.LoadWorld();
            Need(loaded != null, "actual read-only LoadWorld on large selected payload");
            Measure(s, largeOwner, components, "world", loaded.ToDict(), "large-readonly-LoadWorld");
            AssertSnapshotMirrors(s, largeOwner, loaded.HomeShip, "loaded home");
            EqualStorage(selectedBytes, rig.Storage);
            Equal(largeOwner, ValidOwner(s, components, "after read-only selection/load"), "read-only selection retains unpruned owner/proofs");
            Equal(liveBefore, LiveParticipants(s), "selection/load cannot advance or restore gameplay");
        }

        static GdDict SelectedDocument(GdDict selected, string role)
        {
            // Assertion observation only: exact-number parse of actual selected text, no regeneration.
            string text = selected.GetDictOrEmpty("payloads").GetString(role + "_text");
            Need(text.Length > 0, "actual selected " + role + " text");
            var result = GdJson.Parse(text, true) as GdDict;
            Need(result != null, "actual selected text parses " + role);
            return result;
        }
        static void AssertSelected(RunSession s, bool components, GdDict selected, GdDict owner)
        {
            Need(selected.GetBool("ok") && selected.GetString("run_id") == s.RunId && selected.GetString("slot_id") == Slot &&
                selected.GetString("generation_id").Length > 0 && selected.GetString("manifest_sha256").Length > 0,
                "actual selected complete generation identity");
            Need(selected.GetString("save_mode") == (components ? PaidSnapshotCodec.DiagnosticMode : PaidSnapshotCodec.OrdinaryMode),
                "truthful selected save mode");
            GdDict payload = selected.GetDictOrEmpty("payloads"), binding = payload.GetDictOrEmpty("binding");
            Need(binding.GetString("home_ship_id") == s.HomeShip.ShipId && binding.GetString("lifeboat_ship_id") == s.LifeboatShip.ShipId &&
                binding.GetString("player_pose_owner_id") == s.HomeShip.ShipId, "actual original-home/lifeboat/pose binding");
            Need(payload.GetArrayOrEmpty("artifacts").Count > 0 &&
                binding.GetDictOrEmpty("ship_references").Has(s.HomeShip.ShipId) &&
                binding.GetDictOrEmpty("ship_references").Has(s.LifeboatShip.ShipId), "full real retained document closure");
            GdDict run = SelectedDocument(selected, "run"), world = SelectedDocument(selected, "world"), home = world.GetDictOrEmpty("home_ship");
            Need(run.GetString("slice_version") == (components ? RunSnapshot.ComponentIntegrationVersion : SaveLoadService.CURRENT_SLICE_VERSION) &&
                world.GetString("slice_version") == (components ? WorldSnapshot.ComponentIntegrationVersion : WorldSnapshot.WorldSliceVersion),
                "actual ordinary/diagnostic outer versions");
            AssertSnapshotMirrors(s, owner, run, "actual selected run");
            AssertSnapshotMirrors(s, owner, home, "actual selected home");
            Measure(s, owner, components, "run", run, "admitted-actual-selection");
            Measure(s, owner, components, "world", world, "admitted-actual-selection");
            Equal(payload, s.SaveLoadService.ReadGeneration(s.RunId, Slot, selected.GetString("generation_id"),
                selected.GetString("manifest_sha256")).Get("payloads"), "public exact generation read revalidates full payload");
        }
        static void AssertSnapshotMirrors(RunSession s, GdDict owner, GdDict snapshot, string label)
        {
            GdDict participants = owner.GetDictOrEmpty("participating_state");
            GdDict crafting = snapshot.GetDictOrEmpty("crafting_summary").DeepCopy();
            GdDict envelope = crafting.GetDictOrEmpty("paid_craft");
            Need(envelope.Count == 3 && envelope.Get("schema_version") is long && envelope.GetInt("schema_version") == 1 &&
                envelope.GetString("save_mode") == (s.ComponentIntegrationEnabled ? PaidSnapshotCodec.DiagnosticMode : PaidSnapshotCodec.OrdinaryMode), label + " literal paid envelope");
            Need(ComponentDomainCodec.TryDecode(envelope.GetDictOrEmpty("domain"), out GdDict decoded, out string reason), label + " owner decode: " + reason);
            Equal(owner, decoded, label + " exact owner, receipts and proofs");
            Need(s.ValidatePaidCraftingRestore(decoded, out reason), label + " actual current context: " + reason);
            Equal(participants.GetDictOrEmpty("field_crafting").Get("field_crafting"), crafting.Get("field_crafting"), label + " field mirror");
            crafting.Erase("field_crafting"); crafting.Erase("paid_craft");
            Equal(participants.Get("crafting"), crafting, label + " complete crafting mirror");
            GdDict inventory = snapshot.GetDictOrEmpty("inventory_summary").DeepCopy();
            inventory.Erase("combat_hotbar_text"); inventory.Erase("threat_summary");
            Equal(participants.Get("inventory"), inventory, label + " complete inventory mirror");
            Equal(participants.Get("progression"), snapshot.Get("player_progression_summary"), label + " exact progression mirror");
            Equal(participants.Get("spoilage"), snapshot.Get("spoilage_summary"), label + " exact spoilage mirror");
        }

        // Source-derived projection of current DTO shapes ONLY. Never stringify/commit this graph.
        // Scalar generation/archive metadata comes from a genuine accepted small payload, and has no
        // capacity exemption. Current raw model DTOs supply every participant/world collection.
        static readonly HashSet<string> PreserveScalarMetadata = new HashSet<string>(new[] {
            "slice_version", "generation_id", "capture_revision", "run_id", "slot_id", "slot_kind",
            "is_autosave", "is_quicksave", "layout_path", "gameplay_slice_path", "kit_path",
            "current_location", "aboard_ship_id", "player_position", "player_position_in_ship"
        }, StringComparer.Ordinal);
        static GdDict Overlay(GdDict prior, GdDict raw)
        {
            var result = prior.DeepCopy();
            foreach (var pair in raw)
                if (!PreserveScalarMetadata.Contains(V.Str(pair.Key))) result[pair.Key] = V.DeepCopy(pair.Value);
            return result;
        }
        static GdDict ObjectiveProjection(GdDict input)
        {
            var result = new GdDict();
            foreach (var pair in input)
            {
                string key;
                if (pair.Key is long n && n > 0) key = n.ToString(CultureInfo.InvariantCulture);
                else if (pair.Key is string text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) &&
                    parsed > 0 && text == parsed.ToString(CultureInfo.InvariantCulture)) key = text;
                else { Assert.Fail("PROJECTION_DEPENDENCY: unexpected objective key"); return null; }
                Need(!result.Has(key), "projection objective key collision");
                result[key] = V.DeepCopy(pair.Value);
            }
            return result;
        }
        static GdDict PlacementProjection(GdDict input)
        {
            var result = input.DeepCopy();
            foreach (GdDict row in result.GetArrayOrEmpty("placed").OfType<GdDict>())
                if (row.Get("local_position") is Vec3 v)
                    row["local_position"] = GdArray.Of((double)v.X, (double)v.Y, (double)v.Z);
            return result;
        }
        static void RetainedPlacementProjection(GdDict ships)
        {
            foreach (GdDict row in ships.Values.OfType<GdDict>())
                if (row.Get("component_placement") is GdDict placement) row["component_placement"] = PlacementProjection(placement);
        }
        static void CompleteConsentProjection(GdDict mirror, GdDict canonical)
        {
            foreach (var pair in mirror.GetDictOrEmpty("station_summaries"))
                if (pair.Value is GdDict station && !station.Has("resume_required") &&
                    canonical.GetDictOrEmpty("station_summaries").Get(pair.Key) is GdDict owned && owned.Has("resume_required"))
                {
                    Need(owned.Get("resume_required") is bool, "existing DTO consent adapter has canonical boolean");
                    station["resume_required"] = owned.Get("resume_required");
                }
        }
        static void AdaptSnapshot(GdDict snapshot, GdDict owner, bool components)
        {
            GdDict participants = owner.GetDictOrEmpty("participating_state");
            snapshot["objective_progress_summary"] = ObjectiveProjection(snapshot.GetDictOrEmpty("objective_progress_summary"));
            var crafting = snapshot.GetDictOrEmpty("crafting_summary").DeepCopy();
            CompleteConsentProjection(crafting, participants.GetDictOrEmpty("crafting"));
            CompleteConsentProjection(crafting.GetDictOrEmpty("field_crafting"),
                participants.GetDictOrEmpty("field_crafting").GetDictOrEmpty("field_crafting"));
            crafting["paid_craft"] = PaidSnapshotCodec.Envelope(owner, components);
            snapshot["crafting_summary"] = crafting;
            snapshot["component_placement_summary"] = PlacementProjection(snapshot.GetDictOrEmpty("component_placement_summary"));
            RetainedPlacementProjection(snapshot.GetDictOrEmpty("visited_ships"));
            if (components) snapshot["component_domain"] = ComponentDomainCodec.Encode(owner);
            else Need(!snapshot.Has("component_domain"), "ordinary DTO has no fabricated component copy");
        }
        static void Project(RunSession s, GdDict owner, GdDict priorRun, GdDict priorWorld, out GdDict run, out GdDict world)
        {
            Need(!s.AwayFromStart && ReferenceEquals(s.CurrentShip, s.HomeShip), "projection is bounded to genuine original-home fixture");
            WorldSnapshot w = WorldSnapshotAssembler.Build(s);
            RunSnapshot r = RunSnapshotAssembler.Build(s);
            Need(w != null && r != null, "actual current raw snapshot assemblers");
            GdDict rawWorld = w.ToDict();
            run = Overlay(priorRun, r.ToDict());
            world = Overlay(priorWorld, rawWorld);
            var home = Overlay(priorWorld.GetDictOrEmpty("home_ship"), rawWorld.GetDictOrEmpty("home_ship"));
            if (!s.HomeShip.ComponentPlacementSummary.IsEmpty)
                home["component_placement_summary"] = s.HomeShip.ComponentPlacementSummary.DeepCopy();
            AdaptSnapshot(run, owner, s.ComponentIntegrationEnabled);
            AdaptSnapshot(home, owner, s.ComponentIntegrationEnabled);
            if (s.HomeShip.SystemsManager != null)
                foreach (var pair in s.HomeShip.SystemsManager.GetSummary()) home.GetDictOrEmpty("ship_systems_summary")[pair.Key] = V.DeepCopy(pair.Value);
            world["home_ship"] = home;
            RetainedPlacementProjection(world.GetDictOrEmpty("visited_ships"));
            run["visited_ships"] = world.GetDictOrEmpty("visited_ships").DeepCopy();
            GdDict lifeboat = world.GetDictOrEmpty("mobile_home_state").GetDictOrEmpty("lifeboat");
            if (lifeboat.Get("component_placement") is GdDict placement) lifeboat["component_placement"] = PlacementProjection(placement);
            if (s.ComponentIntegrationEnabled) world["component_domain"] = ComponentDomainCodec.Encode(owner);
            else Need(!world.Has("component_domain"), "ordinary world has no fabricated component copy");
            AssertSnapshotMirrors(s, owner, run, "detached run projection");
            AssertSnapshotMirrors(s, owner, home, "detached home projection");
        }

        sealed class Semantic
        {
            public long Nodes, Nulls, Vectors, Pairs;
            public int Depth;
        }
        sealed class CopyCount
        {
            public string Path;
            public long Nodes, Wire;
            public int Depth;
        }
        sealed class Counts
        {
            public long Total, Raw;
            public int RawDepth;
            public readonly List<CopyCount> Copies = new List<CopyCount>();
        }
        static void CountSemantic(object value, Semantic count, int depth = 0)
        {
            checked { count.Nodes++; }
            count.Depth = Math.Max(count.Depth, depth);
            if (value == null) { checked { count.Nulls++; } return; }
            if (value is Vec3) { checked { count.Vectors++; } return; }
            if (value is GdDict dict)
                foreach (var pair in dict)
                {
                    checked { count.Pairs++; }
                    CountSemantic(pair.Key, count, depth + 1);
                    CountSemantic(pair.Value, count, depth + 1);
                }
            else if (value is GdArray array) foreach (object child in array) CountSemantic(child, count, depth + 1);
        }
        static long Wire(object value)
        {
            long count = 1L;
            checked
            {
                if (value is GdDict dict) foreach (var pair in dict) count += Wire(pair.Value); // Names are not JSON value nodes.
                else if (value is GdArray array) foreach (object item in array) count += Wire(item);
            }
            return count;
        }
        static Counts Measure(RunSession s, GdDict owner, bool components, string role, GdDict snapshot, string label)
        {
            string[][] slots = role == "run"
                ? (components ? new[] { new[] { "crafting_summary", "paid_craft", "domain" }, new[] { "component_domain" } }
                    : new[] { new[] { "crafting_summary", "paid_craft", "domain" } })
                : (components ? new[] { new[] { "home_ship", "crafting_summary", "paid_craft", "domain" },
                    new[] { "home_ship", "component_domain" }, new[] { "component_domain" } }
                    : new[] { new[] { "home_ship", "crafting_summary", "paid_craft", "domain" } });
            Need(role == "run" || role == "world", "explicit count role");
            var count = new Counts { Total = Wire(snapshot) };
            var path = new List<string>();
            void Walk(object value, int depth, bool throughArray)
            {
                checked { count.Raw++; }
                count.RawDepth = Math.Max(count.RawDepth, depth);
                if (!throughArray && slots.Any(slot => slot.SequenceEqual(path)))
                {
                    Need(value is GdDict, "actual known owner slot is an envelope");
                    Need(ComponentDomainCodec.TryDecode((GdDict)value, out GdDict decoded, out string reason), "count slot Core decode: " + reason);
                    Equal(owner, decoded, "count every physical owner occurrence independently");
                    Need(DomainBundle.TryCreate(decoded, out _, out reason) && s.ValidatePaidCraftingRestore(decoded, out reason),
                        "count slot strict Core/current context: " + reason);
                    var semantic = new Semantic();
                    CountSemantic(decoded, semantic);
                    long wire = Wire(value), formula;
                    checked { formula = 2L + 3L * semantic.Nodes - semantic.Nulls + 2L * semantic.Vectors + semantic.Pairs; }
                    Need(wire == formula, "independent actual wire count agrees with N/Z/V/P derivation");
                    Need(semantic.Nodes <= SemanticLimit && semantic.Depth <= DepthLimit, "unchanged Core semantic100000/depth128");
                    var copy = new CopyCount { Path = string.Join(".", path), Nodes = semantic.Nodes, Wire = wire, Depth = semantic.Depth };
                    count.Copies.Add(copy);
                    Console.WriteLine("CAPACITY_COPY label=" + label + " components=" + components + " role=" + role +
                        " slot=" + copy.Path + " N=" + copy.Nodes + " W=" + copy.Wire + " semanticDepth=" + copy.Depth);
                    return; // One leaf charged to R; all physical nodes were counted separately above.
                }
                if (value is GdDict dict)
                    foreach (var pair in dict)
                    {
                        Need(pair.Key is string, "PROJECTION_DEPENDENCY: raw DTO string key");
                        path.Add((string)pair.Key); Walk(pair.Value, depth + 1, throughArray); path.RemoveAt(path.Count - 1);
                    }
                else if (value is GdArray array) foreach (object child in array) Walk(child, depth + 1, true);
                else Need(value == null || value is string || value is bool || value is long ||
                    value is double d && !double.IsNaN(d) && !double.IsInfinity(d), "PROJECTION_DEPENDENCY: supported finite raw DTO leaf");
            }
            Walk(snapshot, 0, false);
            Need(count.Copies.Count == slots.Length, "exact fixed owner-slot multiplicity");
            long reconstructed = count.Raw;
            checked { foreach (CopyCount copy in count.Copies) reconstructed += copy.Wire - 1L; }
            Need(count.Total == reconstructed, "physical = R + sum(W-1)");
            Console.WriteLine("CAPACITY_COUNTS label=" + label + " components=" + components + " role=" + role +
                " copies=" + count.Copies.Count + " physical=" + count.Total + " R=" + count.Raw + " rawDepth=" + count.RawDepth);
            return count;
        }
        static void CompareCounts(Counts projected, Counts actual, string label)
        {
            Need(projected.Total == actual.Total && projected.Raw == actual.Raw && projected.RawDepth == actual.RawDepth &&
                projected.Copies.Count == actual.Copies.Count, "PROJECTION_DEPENDENCY: " + label + " full counts");
            foreach (CopyCount p in projected.Copies)
            {
                CopyCount a = actual.Copies.SingleOrDefault(c => c.Path == p.Path);
                Need(a != null && a.Nodes == p.Nodes && a.Wire == p.Wire && a.Depth == p.Depth,
                    "PROJECTION_DEPENDENCY: " + label + " slot " + p.Path);
            }
        }
        static GdDict LiveParticipants(RunSession s) => new GdDict {
            { "inventory", s.InventoryState.GetSummary() }, { "progression", s.PlayerProgression.GetSummary() },
            { "crafting", s.CraftingState.GetSummary() }, { "field", s.FieldCraftingState.GetSummary() },
            { "training", s.TrainingEventBus.ToDict() }, { "spoilage", s.SpoilageState.GetSummary() },
            { "equipment", s.EquipmentState.GetSummary() }
        };
        static Dictionary<string, string> StorageTree(IStorage storage)
        {
            var tree = new Dictionary<string, string>(StringComparer.Ordinal);
            void Visit(string dir)
            {
                tree.Add("D:" + dir, "");
                string Child(string name) => dir.EndsWith("://", StringComparison.Ordinal) ? dir + name : dir + "/" + name;
                foreach (string name in storage.ListFiles(dir)) tree.Add("F:" + Child(name), storage.ReadText(Child(name)));
                foreach (string name in storage.ListDirectories(dir)) Visit(Child(name));
            }
            Visit("user://"); return tree;
        }
        static void EqualStorage(Dictionary<string, string> before, IStorage storage)
        {
            Dictionary<string, string> after = StorageTree(storage);
            Assert.AreEqual(before.Count, after.Count, "Refusal/read preserves every MemoryStorage file and directory");
            foreach (var pair in before) Assert.IsTrue(after.TryGetValue(pair.Key, out string text) && text == pair.Value, "Storage changed: " + pair.Key);
        }
        static void Equal(object expected, object actual, string label)
        {
            if (expected == null) { Assert.IsNull(actual, label); return; }
            Assert.IsNotNull(actual, label);
            Assert.AreEqual(expected.GetType(), actual.GetType(), label + " literal type");
            if (expected is GdDict left)
            {
                var right = (GdDict)actual;
                Assert.AreEqual(left.Count, right.Count, label + " keys");
                foreach (var pair in left)
                {
                    Assert.IsTrue(right.Has(pair.Key), label + " missing key " + V.Str(pair.Key));
                    Equal(pair.Value, right.Get(pair.Key), label + "/" + V.Str(pair.Key));
                }
            }
            else if (expected is GdArray array)
            {
                var other = (GdArray)actual;
                Assert.AreEqual(array.Count, other.Count, label + " length");
                for (int i = 0; i < array.Count; i++) Equal(array[i], other[i], label + "[" + i + "]");
            }
            else if (expected is double real)
            {
                double other = (double)actual;
                Assert.IsFalse(double.IsNaN(real) || double.IsInfinity(real) || double.IsNaN(other) || double.IsInfinity(other), label + " finite");
                Assert.IsTrue(real == other, label + " exact real (existing codec zero behavior)");
            }
            else Assert.AreEqual(expected, actual, label);
        }
    }
}
