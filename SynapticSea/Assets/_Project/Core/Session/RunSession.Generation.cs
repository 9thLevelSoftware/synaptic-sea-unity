using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        readonly Dictionary<IShipSceneRoot, GdDict> _generationRootDocuments = new Dictionary<IShipSceneRoot, GdDict>();
        readonly Dictionary<string, GdDict> _generationShipDocuments = new Dictionary<string, GdDict>(StringComparer.Ordinal);
        GdDict _selectedGeneration;
        readonly Dictionary<string, IShipSceneRoot> _generationStagedRoots = new Dictionary<string, IShipSceneRoot>(StringComparer.Ordinal);
        long _captureRevision;
        internal bool ComponentGenerationRestoreInProgress { get; private set; }
        internal bool ComponentTerminalPending { get; private set; }
        public GdDict LastSaveResult { get; private set; } = new GdDict();
        public bool CompleteGenerationEnabled => ComponentIntegrationEnabled;

        internal T WithSelectedArtifactReader<T>(Func<T> work)
        {
            GdDict selected = _selectedGeneration ?? Deps.SelectedSaveGeneration;
            if (!CompleteGenerationEnabled || selected == null) return work();
            if (!SaveGenerationArtifacts.TryCreateReader(selected, CoreServices.Resources, out IResourceReader reader, out string reason)) throw new InvalidOperationException(reason);
            return WithArtifactReader(reader, work);
        }
        bool PrepareGenerationBoot()
        {
            if (!CompleteGenerationEnabled || Deps.SelectedSaveGeneration == null) return true;
            GdDict requested = Deps.SelectedSaveGeneration.DeepCopy();
            var service = new SaveLoadService(Storage, Clock, ComponentIntegrationEnabled);
            GdDict exact = service.ReadGeneration(requested.GetString("run_id"), requested.GetString("slot_id"), requested.GetString("generation_id"), requested.GetString("manifest_sha256"));
            if (!exact.GetBool("ok") || !V.VariantEquals(requested.Get("payloads"), exact.Get("payloads")))
            {
                LastSaveResult = exact.GetBool("ok") ? new GdDict { { "ok", false }, { "reason", "selection_mismatch" } } : exact;
                LastFailureReason = LastSaveResult.GetString("reason"); PlayableFailed?.Invoke(LastFailureReason); return false;
            }
            _selectedGeneration = exact.DeepCopy(); Deps.SelectedSaveGeneration = exact.DeepCopy();
            GdDict payload = exact.GetDictOrEmpty("payloads"); RestoreGenerationDocuments(payload);
            GdDict home = payload.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty("ship_start");
            LayoutPath = home.GetString("layout_path"); KitPath = home.GetString("kit_path"); GameplaySlicePath = home.GetString("gameplay_slice_path"); BlueprintPath = home.GetString("blueprint_path");
            return true;
        }
        static T WithArtifactReader<T>(IResourceReader reader, Func<T> work)
        {
            IResourceReader previous = CoreServices.Resources; CatalogRegistry.Clear(); CoreServices.Resources = reader;
            try { return work(); }
            finally { CatalogRegistry.Clear(); CoreServices.Resources = previous; }
        }
        internal long NextCaptureRevision(long selectedRevision)
        {
            long current = Math.Max(_captureRevision, selectedRevision);
            if (current == long.MaxValue) throw new InvalidOperationException("capture_revision_exhausted");
            return _captureRevision = current + 1;
        }
        void RememberHomeGenerationDocuments(IShipLoaderView root, string layout, string kit, string slice)
        {
            if (!CompleteGenerationEnabled || root == null) return;
            string layoutText = CoreServices.Resources?.ReadText(layout), sliceText = CoreServices.Resources?.ReadText(slice), kitText = CoreServices.Resources?.ReadText(kit), blueprintText = CoreServices.Resources?.ReadText(BlueprintPath);
            if (layoutText == null || sliceText == null || kitText == null || blueprintText == null) return;
            _generationRootDocuments[root] = new GdDict { { "layout_text", layoutText }, { "slice_text", sliceText }, { "kit_text", kitText }, { "kit_path", kit }, { "blueprint_text", blueprintText }, { "fixed_lifeboat", false } };
        }
        void RememberGeneratedDocuments(IShipLoaderView root, ShipDocuments docs)
        {
            if (!CompleteGenerationEnabled || root == null || docs == null) return;
            string kitText = CoreServices.Resources?.ReadText(docs.KitPath);
            if (kitText == null) return;
            _generationRootDocuments[root] = new GdDict { { "layout_text", docs.LayoutJson ?? GdJson.Stringify(docs.Layout, "  ") },
                { "slice_text", docs.GameplaySliceJson ?? GdJson.Stringify(docs.GameplaySlice, "  ") }, { "runtime_gameplay", docs.RuntimeGeneratedGameplay }, { "kit_text", kitText }, { "kit_path", docs.KitPath }, { "blueprint_text", "" }, { "fixed_lifeboat", false } };
        }
        void RememberShipGenerationDocuments(ShipInstance ship)
        {
            if (!CompleteGenerationEnabled || ship == null || ship.SceneRoot == null || !_generationRootDocuments.TryGetValue(ship.SceneRoot, out GdDict document)) return;
            GdDict copy = document.DeepCopy();
            if (!copy.GetBool("fixed_lifeboat") && copy.GetString("blueprint_text").Length == 0 && ship.Blueprint != null) copy["blueprint_text"] = GdJson.Stringify(ship.Blueprint.ToDict(), "  ");
            _generationShipDocuments[ship.ShipId] = copy;
        }
        void RememberLifeboatGenerationDocuments(IShipSceneRoot root, LifeBoatBuilder.BuildResult built)
        {
            if (!CompleteGenerationEnabled || root == null || built == null) return;
            string kitText = CoreServices.Resources?.ReadText(built.KitPath); if (kitText == null) return;
            _generationRootDocuments[root] = new GdDict { { "layout_text", GdJson.Stringify(built.Layout, "  ") }, { "slice_text", "" }, { "kit_text", kitText },
                { "kit_path", built.KitPath }, { "blueprint_text", "" }, { "fixed_lifeboat", true } };
        }
        internal bool BuildGenerationDocumentSet(WorldSnapshot world, out GdDict references, out GdArray artifacts, out string reason)
        {
            references = new GdDict(); artifacts = new GdArray(); reason = "reference_missing";
            var owners = new List<string> { "ship_start", "lifeboat" };
            foreach (GdDict row in world.VisitedShips.Values.OfType<GdDict>()) owners.Add(row.GetString("ship_id"));
            var archive = new Dictionary<string, GdDict>(StringComparer.Ordinal);
            foreach (string owner in owners)
            {
                ShipInstance live = FindShipByIdInternal(owner);
                RememberShipGenerationDocuments(live);
                if (!_generationShipDocuments.TryGetValue(owner, out GdDict docs)) return false;
                bool fixedBoat = docs.GetBool("fixed_lifeboat");
                string dir = "user://component-artifacts/" + SaveGenerationArtifacts.Hash(owner) + "/";
                string layout = dir + "layout.json", slice = fixedBoat ? "" : dir + "gameplay_slice.json", blueprint = fixedBoat ? "" : dir + "blueprint.json", kit = docs.GetString("kit_path");
                if (kit.Length == 0 || docs.GetString("layout_text").Length == 0 || docs.GetString("kit_text").Length == 0 ||
                    !fixedBoat && (docs.GetString("slice_text").Length == 0 || docs.GetString("blueprint_text").Length == 0)) return false;
                string profile = live?.Blueprint?.GenerationProfile ?? "";
                if (live == null && owner != "ship_start" && owner != "lifeboat") profile = world.VisitedShips.Values.OfType<GdDict>().Single(s => s.GetString("ship_id") == owner).GetDictOrEmpty("blueprint").GetString("generation_profile");
                references[owner] = new GdDict { { "present", true }, { "reference_kind", fixedBoat ? "fixed_lifeboat" : "generated_ship" }, { "layout_path", layout },
                    { "gameplay_slice_path", slice }, { "kit_path", kit }, { "blueprint_path", blueprint }, { "profile_id", profile } };
                bool Add(string path, string kind, string version, string text)
                {
                    if (archive.TryGetValue(path, out GdDict existing)) return existing.GetString("text") == text && existing.GetString("document_kind") == kind;
                    archive[path] = new GdDict { { "logical_path", path }, { "document_kind", kind }, { "schema_version", version }, { "text", text } }; return true;
                }
                if (!Add(layout, "ship_layout", "1.2.0", docs.GetString("layout_text")) || !Add(kit, "ship_structural_catalog", "1.0.0", docs.GetString("kit_text"))) { reason = "artifact_identity_conflict"; return false; }
                if (!fixedBoat && (!Add(slice, docs.GetBool("runtime_gameplay") ? "runtime_generated_gameplay_slice" : "ship_gameplay_slice", docs.GetBool("runtime_gameplay") ? "component-runtime-gameplay-1" : "1.1.0", docs.GetString("slice_text")) || !Add(blueprint, "ship_blueprint", "component-blueprint-1", docs.GetString("blueprint_text")))) return false;
            }
            foreach (string path in archive.Keys.OrderBy(p => p, StringComparer.Ordinal)) artifacts.Add(archive[path]); reason = ""; return true;
        }

        public bool RequestSaveToSlot(string slotId, string slotKind, string displayName)
        {
            if (ComponentGenerationRestoreInProgress) return false;
            if (ComponentTerminalPending) return false;
            if (!CompleteGenerationEnabled) { LastSaveResult = new GdDict { { "ok", false }, { "reason", "component_integration_not_enabled" } }; return false; }
            if (DemoSaveRefused()) { LastSaveResult = new GdDict { { "ok", false }, { "reason", "demo_save_refused" } }; return false; }
            GdDict assembled = SavePayloadAssembler.Build(this, slotId, slotKind);
            if (!assembled.GetBool("ok")) { LastSaveResult = assembled.DeepCopy(); return false; }
            GdDict payload = assembled.GetDictOrEmpty("payloads");
            bool firstForSlot = payload.GetString("parent_generation_id").Length == 0;
            LastSaveResult = SaveLoadService.CommitComponentGeneration(payload, firstForSlot);
            if (!LastSaveResult.GetBool("ok")) return false;
            LastSavedSnapshot = RunSnapshot.FromDict(GdJson.ParseString(payload.GetString("run_text")),
                ComponentIntegrationEnabled ? RunSnapshot.ComponentIntegrationVersion : SaveLoadService.CURRENT_SLICE_VERSION, Deps.Engine.VersionString);
            Events.RaiseLoadAvailable(true); return true;
        }

        public bool ApplySelectedGeneration(GdDict selection)
        {
            if (ComponentGenerationRestoreInProgress) return false;
            if (ComponentTerminalPending) return false;
            if (!ComponentIntegrationEnabled || selection == null || !selection.GetBool("ok") || SaveLoadService == null) return false;
            GdDict owned = selection.DeepCopy();
            GdDict exact = SaveLoadService.ReadGeneration(owned.GetString("run_id"), owned.GetString("slot_id"), owned.GetString("generation_id"), owned.GetString("manifest_sha256"));
            if (!exact.GetBool("ok") || !V.VariantEquals(owned.Get("payloads"), exact.Get("payloads"))) { LastSaveResult = exact.GetBool("ok") ? new GdDict { { "ok", false }, { "reason", "selection_mismatch" } } : exact; return false; }
            if (!SaveGenerationArtifacts.TryCreateReader(exact, CoreServices.Resources, out IResourceReader reader, out string reason)) return false;
            GdDict payload = exact.GetDictOrEmpty("payloads"), worldDict = GdJson.ParseString(payload.GetString("world_text")) as GdDict;
            if (worldDict == null || !ComponentDomainCodec.TryDecode(worldDict.GetDictOrEmpty("component_domain"), out GdDict domain, out reason) ||
                !ValidateComponentDomainRestore(domain, out reason)) { LastSaveResult = new GdDict { { "ok", false }, { "reason", reason ?? "invalid_world" } }; return false; }
            WorldSnapshot world = WorldSnapshot.FromDict(worldDict, WorldSnapshot.ComponentIntegrationVersion, Deps.Engine.VersionString);
            if (world == null || !WorldSnapshotAssembler.ValidateConnectionSnapshot(world, "ship_start", "lifeboat", out _, out reason)) return false;
            foreach (var pair in world.VisitedShips)
            {
                ShipInstance candidate = ShipInstance.Create("", "", new ShipBlueprint(), null, null);
                if (!(pair.Value is GdDict row) || !candidate.ApplySummary(row) || candidate.Blueprint == null || candidate.MarkerId != V.Str(pair.Key))
                { LastSaveResult = new GdDict { { "ok", false }, { "reason", "invalid_retained_ship" } }; return false; }
            }
            // Build every required retained root while the current world is still intact. The same detached roots
            // are consumed during apply, so a persistent host refusal cannot become a destructive rollback loop.
            if (!WithArtifactReader(reader, () => StageGenerationRoots(payload, world)))
            { LastSaveResult = new GdDict { { "ok", false }, { "reason", "required_ship_host_failed" } }; return false; }
            // Capture a complete before-image for exceptional host failures after validated model preflight.
            GdDict beforeCapture = SavePayloadAssembler.Build(this, owned.GetString("slot_id"), SaveLoadService.ComponentSlotKind(owned.GetString("slot_id")));
            if (!beforeCapture.GetBool("ok")) { ClearStagedGenerationRoots(); LastSaveResult = beforeCapture; return false; }
            GdDict beforePayload = beforeCapture.GetDictOrEmpty("payloads"), beforeWorldDict = GdJson.ParseString(beforePayload.GetString("world_text")) as GdDict;
            if (!ComponentDomainCodec.TryDecode(beforeWorldDict.GetDictOrEmpty("component_domain"), out GdDict beforeDomain, out reason)) { ClearStagedGenerationRoots(); return false; }
            var beforeDocs = _generationShipDocuments.ToDictionary(p => p.Key, p => p.Value.DeepCopy(), StringComparer.Ordinal);
            GdDict beforeSelection = new GdDict { { "ok", true }, { "payloads", beforePayload }, { "payloads_sha256", SaveGenerationArtifacts.Hash(GdJson.Stringify(beforePayload)) } };
            string oldRun = _runId, oldBlueprint = BlueprintPath; GdDict previousSelection = _selectedGeneration;
            ComponentGenerationRestoreInProgress = true;
            try
            {
                bool applied = WithArtifactReader(reader, () =>
                {
                    RestoreGenerationDocuments(payload); BlueprintPath = payload.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty("ship_start").GetString("blueprint_path");
                    _selectedGeneration = exact.DeepCopy();
                    if (!WorldSnapshotAssembler.Apply(this, world)) return false;
                    if (!SaveLoadService.ReadGeneration(exact.GetString("run_id"), exact.GetString("slot_id"), exact.GetString("generation_id"), exact.GetString("manifest_sha256")).GetBool("ok")) return false;
                    return RestoreComponentDomainOwned(domain) && SaveLoadService.ReadGeneration(exact.GetString("run_id"), exact.GetString("slot_id"), exact.GetString("generation_id"), exact.GetString("manifest_sha256")).GetBool("ok");
                });
                if (!applied) throw new InvalidOperationException("generation_apply_failed");
                _runId = world.RunId; SaveLoadService.SetActiveRunId(_runId); LastSaveResult = new GdDict { { "ok", true }, { "reason", "generation_loaded" } }; return true;
            }
            catch (Exception e)
            {
                ClearStagedGenerationRoots();
                bool rolledBack = false;
                try
                {
                    if (SaveGenerationArtifacts.TryCreateReader(beforeSelection, CoreServices.Resources, out IResourceReader beforeReader, out _))
                        rolledBack = WithArtifactReader(beforeReader, () =>
                        {
                            _generationShipDocuments.Clear(); foreach (var pair in beforeDocs) _generationShipDocuments[pair.Key] = pair.Value;
                            BlueprintPath = beforePayload.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty("ship_start").GetString("blueprint_path");
                            WorldSnapshot beforeWorld = WorldSnapshot.FromDict(beforeWorldDict, WorldSnapshot.ComponentIntegrationVersion, Deps.Engine.VersionString);
                            return WorldSnapshotAssembler.Apply(this, beforeWorld) && RestoreComponentDomainOwned(beforeDomain);
                        });
                }
                catch (Exception) { }
                _runId = oldRun; SaveLoadService.SetActiveRunId(oldRun); BlueprintPath = oldBlueprint; _selectedGeneration = previousSelection;
                LastSaveResult = new GdDict { { "ok", false }, { "reason", rolledBack ? "generation_apply_failed" : "restore_rollback_failed" }, { "detail", e.GetType().Name } }; return false;
            }
            finally { ClearStagedGenerationRoots(); ComponentGenerationRestoreInProgress = false; }
        }
        bool StageGenerationRoots(GdDict payload, WorldSnapshot world, Dictionary<string, IShipSceneRoot> staged = null)
        {
            if (staged == null) { ClearStagedGenerationRoots(); staged = _generationStagedRoots; }
            var needed = new HashSet<string>(StringComparer.Ordinal) { "lifeboat", world.AboardShipId, world.PilotedShipId };
            if (world.CurrentLocation.Length > 0) needed.Add(world.VisitedShips.GetDictOrEmpty(world.CurrentLocation).GetString("ship_id"));
            foreach (GdDict edge in world.DockEdges.OfType<GdDict>())
            {
                needed.Add(edge.GetString("mobile"));
                needed.Add(edge.GetString("host_ship_id", edge.GetString("host").Length == 0 ? "ship_start" : world.VisitedShips.GetDictOrEmpty(edge.GetString("host")).GetString("ship_id")));
            }
            needed.Remove(""); needed.Remove("ship_start");
            var archive = payload.GetArrayOrEmpty("artifacts").OfType<GdDict>().ToDictionary(a => a.GetString("logical_path"), StringComparer.Ordinal);
            try
            {
                foreach (string owner in needed.OrderBy(id => id, StringComparer.Ordinal))
                {
                    GdDict r = payload.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty(owner);
                    bool boat = r.GetString("reference_kind") == "fixed_lifeboat";
                    GdDict slice = boat ? null : archive[r.GetString("gameplay_slice_path")];
                    var docs = new ShipDocuments { Layout = ParseGenerationDocument(archive[r.GetString("layout_path")]), Kit = ParseGenerationDocument(archive[r.GetString("kit_path")]),
                        KitPath = r.GetString("kit_path"), LayoutJson = archive[r.GetString("layout_path")].GetString("text"), GameplaySlice = boat ? new GdDict() : ParseGenerationDocument(slice),
                        GameplaySliceJson = boat ? "" : slice.GetString("text"), IsAway = !boat, Name = boat ? "LifeBoat" : "GeneratedDerelict",
                        RuntimeGeneratedGameplay = !boat && slice.GetString("document_kind") == "runtime_generated_gameplay_slice" };
                    if (!boat && owner != "ship_start")
                    {
                        var row = world.VisitedShips
                            .Single(pair => pair.Value is GdDict ship && ship.GetString("ship_id") == owner);
                        var summary = (GdDict)row.Value;
                        ShipBlueprint blueprint = ShipBlueprint.FromDict(summary.GetDictOrEmpty("blueprint"));
                        if (!RetainedDocumentsMatchBlueprint(docs, blueprint)) return false;
                    }
                    IShipSceneRoot root = boat ? ShipHost?.BuildLifeboatScene(RetainedLifeboatBuild(docs.Layout, docs.KitPath)) : ShipHost?.BuildShipScene(docs);
                    if (root == null || !RootValid(root))
                    {
                        if (root != null) ShipHost?.FreeShipRoot(root);
                        if (ReferenceEquals(staged, _generationStagedRoots)) ClearStagedGenerationRoots();
                        return false;
                    }
                    staged.Add(owner, root);
                }
                return true;
            }
            catch (Exception) { if (ReferenceEquals(staged, _generationStagedRoots)) ClearStagedGenerationRoots(); return false; }
        }
        static GdDict ParseGenerationDocument(GdDict artifact) => GdJson.ParseString(artifact.GetString("text")) as GdDict;
        internal IShipSceneRoot TakeStagedGenerationRoot(string owner)
        {
            if (!_generationStagedRoots.TryGetValue(owner, out IShipSceneRoot root)) return null;
            _generationStagedRoots.Remove(owner); return root;
        }
        void ClearStagedGenerationRoots()
        {
            foreach (IShipSceneRoot root in _generationStagedRoots.Values) try { ShipHost?.FreeShipRoot(root); } catch (Exception) { }
            _generationStagedRoots.Clear();
        }
        static LifeBoatBuilder.BuildResult RetainedLifeboatBuild(GdDict layout, string kitPath)
        {
            if (layout == null || layout.GetArrayOrEmpty("rooms").Count != 3) return null;
            var result = new LifeBoatBuilder.BuildResult { Layout = layout.DeepCopy(), KitPath = kitPath };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in layout.GetArrayOrEmpty("rooms"))
            {
                if (!(value is GdDict room) || !ids.Add(room.GetString("id")) || !(room.Get("world_origin") is GdArray origin) || origin.Count != 3 ||
                    origin.Any(v => !V.IsNumber(v) || double.IsNaN(V.F64(v)) || double.IsInfinity(V.F64(v)))) return null;
                result.Rooms.Add(new LifeBoatBuilder.RoomNode { RoomId = room.GetString("id"), Position = Vec3.FromArray(origin) });
            }
            return ids.SetEquals(new[] { "airlock_01", "cockpit_01", "engine_bay_01" }) ? result : null;
        }
        void RestoreGenerationDocuments(GdDict payload)
        {
            var archive = payload.GetArrayOrEmpty("artifacts").OfType<GdDict>().ToDictionary(a => a.GetString("logical_path"), a => a.GetString("text"), StringComparer.Ordinal);
            _generationShipDocuments.Clear();
            foreach (var pair in payload.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references"))
            {
                GdDict r = (GdDict)pair.Value; bool boat = r.GetString("reference_kind") == "fixed_lifeboat";
                bool runtime = !boat && payload.GetArrayOrEmpty("artifacts").OfType<GdDict>().Single(a => a.GetString("logical_path") == r.GetString("gameplay_slice_path")).GetString("document_kind") == "runtime_generated_gameplay_slice";
                _generationShipDocuments[(string)pair.Key] = new GdDict { { "runtime_gameplay", runtime }, { "layout_text", archive[r.GetString("layout_path")] }, { "kit_text", archive[r.GetString("kit_path")] }, { "kit_path", r.Get("kit_path") },
                    { "slice_text", boat ? "" : archive[r.GetString("gameplay_slice_path")] }, { "blueprint_text", boat ? "" : archive[r.GetString("blueprint_path")] }, { "fixed_lifeboat", boat } };
            }
        }
        /// <summary>A retained wreck's layout must declare the generation profile its blueprint records.</summary>
        static bool RetainedDocumentsMatchBlueprint(ShipDocuments docs, ShipBlueprint blueprint)
        {
            if (docs == null) return false;
            if (docs.Layout == null && !string.IsNullOrEmpty(docs.LayoutJson)) docs.Layout = GdJson.ParseString(docs.LayoutJson) as GdDict;
            return blueprint == null || (docs.Layout?.GetString("generation_profile") ?? "") == blueprint.GenerationProfile;
        }
        IShipLoaderView BuildRetainedGenerationShip(ShipInstance ship)
        {
            if (!_generationShipDocuments.TryGetValue(ship.ShipId, out GdDict d) || d.GetBool("fixed_lifeboat")) return null;
            var docs = new ShipDocuments { Layout = GdJson.ParseString(d.GetString("layout_text")) as GdDict, GameplaySlice = GdJson.ParseString(d.GetString("slice_text")) as GdDict,
                Kit = GdJson.ParseString(d.GetString("kit_text")) as GdDict, KitPath = d.GetString("kit_path"), LayoutJson = d.GetString("layout_text"), GameplaySliceJson = d.GetString("slice_text"), IsAway = true, RuntimeGeneratedGameplay = d.GetBool("runtime_gameplay") };
            if (!RetainedDocumentsMatchBlueprint(docs, ship.Blueprint)) return null;
            IShipLoaderView staged = TakeStagedGenerationRoot(ship.ShipId) as IShipLoaderView;
            if (staged == null) return BuildShipSceneFromDocuments(docs);
            RecordKitPath(staged, docs.KitPath); RememberGeneratedDocuments(staged, docs); return staged;
        }
        internal void RestoreGenerationPlayerPose(WorldSnapshot world)
        {
            ShipInstance ship = FindShipByIdInternal(world.AboardShipId);
            if (ship?.SceneRoot == null) throw new InvalidOperationException("player_pose_owner_missing");
            CurrentOccupancy = ship;
            SetPlayerPosition(ship.SceneRoot.GlobalTransform * Vec3.FromArray(world.PlayerPositionInShip));
        }
    }
}
