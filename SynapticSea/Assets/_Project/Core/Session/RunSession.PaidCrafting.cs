using System;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        bool _paidInitializing;
        GdDict _paidPublicationContext;
        GdDict _paidMaterialInputs;
        bool _paidRewardContext;
        Func<string, string, bool> _paidRewardFilter;
        Func<string, bool> _paidRewardGate;
        public bool PaidCraftingEnabled => Deps.EnablePaidCrafting;
        public bool DomainPublicationInProgress => _componentPublishing || _componentMutating || ComponentGenerationRestoreInProgress;
        public RecipeKnowledgeState RecipeKnowledge { get; private set; }
        static GdDict PaidState(GdDict domain) => PaidCraftingState.State(domain);
        static string CraftChannel(string kind) => kind == "field_crafting" ? "field" : "station";
        static GdDict PaidFailure(string reason) => new GdDict { { "ok", false }, { "committed", false }, { "reason", reason } };

        GdDict RecipeCatalog()
        {
            var recipes = new GdDict();
            foreach (object id in CraftingState.GetAllRecipeIds()) recipes[id] = CraftingState.GetRecipe(V.Str(id)).DeepCopy();
            return recipes;
        }
        GdDict NewPaidState()
        {
            var knowledge = new RecipeKnowledgeState(); knowledge.SeedFromRecipes(RecipeCatalog());
            foreach (var book in PlayerProgression.BooksRead)
                if (book.Value is bool read && read && PlayerProgression.GetBooksCatalog().Has(book.Key)) knowledge.LearnFromBook(V.Str(book.Key), RecipeCatalog());
            return new GdDict { { "schema_version", 1L }, { "run_id", RunId }, { "actor_id", PLAYER_LOCAL_ID },
                { "jobs", new GdDict() }, { "queues", new GdDict { { "station", new GdArray() }, { "field", new GdArray() } } },
                { "legacy", new GdDict() }, { "knowledge", knowledge.GetSummary() }, { "reward_history", PaidCraftRewardProof.NewHistory() } };
        }
        internal void InitializePaidCrafting()
        {
            if (ComponentGenerationRestoreInProgress || !PaidCraftingEnabled || _paidInitializing || CraftingState == null || HomeShip == null || string.IsNullOrEmpty(RunId)) return;
            _paidInitializing = true;
            try
            {
                if (ComponentIntegrationEnabled && _componentDomain == null) InitializeComponentIntegration();
                if (_componentDomain == null)
                {
                    var initial = new GdDict { { "schema_version", 3L }, { "domain_mode", "craft_only" }, { "revision", 0L },
                        { "registry", new GdDict { { "schema_version", 1L }, { "instances", new GdDict() } } }, { "holders", new GdDict() },
                        { "machinery", new GdDict() }, { "receipts", new GdDict() }, { "physical_slots", new GdDict() }, { "component_work", new GdDict() },
                        { "command_sequence", 0L }, { "registered_owners", new GdArray() }, { "participating_state", ReadComponentParticipants() } };
                    initial.GetDictOrEmpty("participating_state")["paid_crafting"] = NewPaidState();
                    SetPaidProjections(initial);
                    _componentDomain = NewComponentOwner(initial);
                }
                else if (_componentDomain.GetSummary().GetInt("schema_version") == 2)
                {
                    GdDict upgraded = _componentDomain.GetSummary();
                    upgraded["schema_version"] = 3L; upgraded["domain_mode"] = "components_and_craft";
                    upgraded.GetDictOrEmpty("participating_state")["paid_crafting"] = NewPaidState();
                    upgraded.GetDictOrEmpty("participating_state")["spoilage"] = SpoilageState?.GetSummary() ?? new GdDict();
                    SetPaidProjections(upgraded); _componentDomain = NewComponentOwner(upgraded);
                }
                if (ManualStudyEnabled && _componentDomain.SchemaVersion == 3)
                {
                    GdDict upgraded = _componentDomain.GetSummary(); upgraded["schema_version"] = 4L;
                    upgraded.GetDictOrEmpty("participating_state")["manual_study"] = ManualStudyState.New(RunId, PLAYER_LOCAL_ID);
                    _componentDomain = NewComponentOwner(upgraded);
                }
                RecipeKnowledge = RecipeKnowledge ?? new RecipeKnowledgeState();
                RecipeKnowledge.ApplySummary(PaidState(_componentDomain.GetSummary()).GetDictOrEmpty("knowledge"));
                BindPaidCraftingModels();
                ApplyPaidProjections(_componentDomain.GetSummary().GetDictOrEmpty("participating_state"));
            }
            finally { _paidInitializing = false; }
        }
        void BindPaidCraftingModels()
        {
            CraftingState.RecipePreflight = PaidRecipePreflight;
            CraftingState.PaidBegin = id => RequestPaidCraft(CraftingState.GetStationKind(id), id);
            CraftingState.PaidAvailability = id => PaidAvailability(CraftingState.GetStationKind(id), id);
            CraftingState.PaidAdvance = delta => AdvancePaidCrafting(delta, "station");
            CraftingState.PaidCancel = () => CancelPaidChannel("station");
            CraftingState.PaidEnqueue = (id, count) => {
                long accepted = 0;
                for (long i = 0; i < count && i < StationState.DEFAULT_MAX_QUEUE; i++)
                    if (EnqueuePaidCraft(CraftingState.GetStationKind(id), id).GetBool("ok")) accepted++; else break;
                return accepted;
            };
            FieldCraftingState.RecipePreflight = PaidRecipePreflight;
            FieldCraftingState.BindPaidOwner(id => RequestPaidCraft("field_crafting", id), id => PaidAvailability("field_crafting", id),
                delta => AdvancePaidCrafting(delta, "field"), () => CancelPaidChannel("field"));
        }
        string PaidRecipePreflight(GdDict recipe)
        {
            if (ComponentGenerationRestoreInProgress) return "restore_in_progress";
            if (ManualStudyRunning) return "study_busy";
            if (ComponentTerminalPending || SliceComplete) return "terminal_pending";
            if (IsComponentForm(recipe.GetDictOrEmpty("produces").GetString("item_id")) || recipe.GetDictOrEmpty("ingredients").Keys.Any(id => IsComponentForm(V.Str(id))))
                return ComponentIntegrationEnabled ? "diagnostic_component_crafting_unavailable" : "component_crafting_unavailable";
            return "";
        }
        bool EnsurePaidOwner()
        {
            if (ComponentGenerationRestoreInProgress || !PaidCraftingEnabled) return false;
            if (_componentDomain == null || !PaidCraftingState.IsDomainVersion(_componentDomain.SchemaVersion) || ManualStudyEnabled && _componentDomain.SchemaVersion == 3) InitializePaidCrafting();
            return _componentDomain != null && PaidCraftingState.IsDomainVersion(_componentDomain.SchemaVersion) && (_componentDomain.SchemaVersion != 4 || ManualStudyEnabled);
        }
        public GdDict CapturePaidCraftingDomain()
        {
            if (ComponentGenerationRestoreInProgress) return PaidFailure("restore_in_progress");
            if (!EnsurePaidOwner()) return PaidFailure("paid_crafting_inactive");
            if (!DomainPublicationInProgress) RefreshComponentParticipants();
            return _componentDomain.GetSummary();
        }
        GdDict ReadPaidParticipants(GdDict participants, GdDict detachedPaidState = null)
        {
            if (!PaidCraftingEnabled) return participants;
            // A capture caller may supply its already-owned candidate state. Other callers (including
            // final publication checks) take a fresh snapshot and read live participants independently.
            GdDict state = detachedPaidState ?? (_componentDomain != null && PaidCraftingState.IsDomainVersion(_componentDomain.SchemaVersion)
                ? PaidState(_componentDomain.GetSummary()) : NewPaidState());
            var knowledge = new RecipeKnowledgeState(); knowledge.ApplySummary(state.GetDictOrEmpty("knowledge"));
            GdDict recipes = RecipeCatalog(); knowledge.SeedFromRecipes(recipes);
            foreach (var book in PlayerProgression.BooksRead)
                if (book.Value is bool read && read && PlayerProgression.GetBooksCatalog().Has(book.Key)) knowledge.LearnFromBook(V.Str(book.Key), recipes);
            state["knowledge"] = knowledge.GetSummary();
            PaidCraftRewardProof.RefreshCurrent(state, participants.GetDictOrEmpty("training"));
            participants["paid_crafting"] = state;
            participants["crafting"] = PaidProjection(state, false);
            participants["field_crafting"] = new GdDict { { "field_crafting", PaidProjection(state, true) } };
            participants["spoilage"] = SpoilageState?.GetSummary() ?? new GdDict();
            if (ManualStudyEnabled && _componentDomain?.SchemaVersion == 4)
                participants["manual_study"] = ManualStudyState.State(_componentDomain.GetSummary()).DeepCopy();
            return participants;
        }
        GdDict PaidProjection(GdDict state, bool field)
        {
            GdDict baseline = field ? FieldCraftingState.GetSummary().GetDictOrEmpty("field_crafting") : CraftingState.GetSummary();
            GdDict stations = baseline.GetDictOrEmpty("station_summaries").DeepCopy();
            foreach (GdDict station in stations.Values.OfType<GdDict>())
            {
                station["active_recipe_id"] = ""; station["progress_seconds"] = 0.0; station["required_seconds"] = 0.0;
                station["status"] = 0L; station["queue"] = new GdArray(); station["resume_required"] = false;
            }
            string channel = field ? "field" : "station";
            GdDict active = new GdDict();
            foreach (GdDict job in state.GetDictOrEmpty("jobs").Values.OfType<GdDict>())
            {
                if (job.GetString("channel") != channel || PaidCraftingState.Terminal(job)) continue;
                string kind = job.GetString("station_kind");
                if (!stations.Has(kind)) { var station = new StationState(); station.Configure(new GdDict { { "station_kind", kind } }); stations[kind] = station.GetSummary(); }
                GdDict projected = stations.GetDictOrEmpty(kind);
                if (job.GetString("input_state") == "unpaid") continue;
                active = new GdDict { { "recipe_id", job.Get("recipe_id") }, { "station_kind", kind }, { "quality_score", job.Get("quality_score") },
                    { "quality_tier", job.Get("quality_tier") }, { "quality_multiplier", job.Get("quality_multiplier") } };
                projected["active_recipe_id"] = job.Get("recipe_id"); projected["progress_seconds"] = job.Get("progress_seconds"); projected["required_seconds"] = job.Get("required_seconds");
                projected["resume_required"] = job.Get("resume_required");
                projected["status"] = job.GetString("status") == "completed_pending_delivery" ? 3L : job.GetBool("resume_required") || job.GetString("status") == "paused" ? 2L : 1L;
            }
            foreach (object id in state.GetDictOrEmpty("queues").GetArrayOrEmpty(channel))
            {
                GdDict queued = state.GetDictOrEmpty("jobs").GetDictOrEmpty(id);
                stations.GetDictOrEmpty(queued.GetString("station_kind")).GetArrayOrEmpty("queue").Add(queued.Get("recipe_id"));
            }
            return new GdDict { { "recipe_count", CraftingState.RecipeCount() }, { "active_craft", active }, { "station_summaries", stations } };
        }
        void SetPaidProjections(GdDict domain)
        {
            GdDict participants = domain.GetDictOrEmpty("participating_state"), state = PaidState(domain);
            PaidCraftRewardProof.RefreshCurrent(state, participants.GetDictOrEmpty("training"));
            participants["crafting"] = PaidProjection(state, false);
            participants["field_crafting"] = new GdDict { { "field_crafting", PaidProjection(state, true) } };
        }
        void ApplyPaidProjections(GdDict participants)
        {
            CraftingState.ApplyOwnedSummary(participants.GetDictOrEmpty("crafting"));
            FieldCraftingState.ApplyOwnedSummary(participants.GetDictOrEmpty("field_crafting"));
            RecipeKnowledge = RecipeKnowledge ?? new RecipeKnowledgeState();
            RecipeKnowledge.ApplySummary(participants.GetDictOrEmpty("paid_crafting").GetDictOrEmpty("knowledge"));
            if (SpoilageState != null && participants.Get("spoilage") is GdDict spoilage) RestoreExactSpoilage(SpoilageState, spoilage);
        }
        static void RestoreExactSpoilage(SpoilageState target, GdDict summary)
        {
            target.Clear();
            foreach (var entry in summary.GetDictOrEmpty("foods"))
            {
                var row = (GdDict)entry.Value; FoodState food = target.AddFood(V.Str(entry.Key), row);
                food.ElapsedSeconds = row.GetFloat("elapsed_seconds"); food.CurrentStage = row.GetInt("stage");
                food.TotalSpoilageSeconds = row.GetFloat("total_spoilage_seconds");
            }
            target.ApplySummary(summary);
        }
        string PaidStationOwner(string kind) => kind == "field_crafting" ? PLAYER_LOCAL_ID : HomeShip?.ShipId ?? "";
        string PaidStationId(string kind) => kind == "field_crafting" ? "portable:" + RunId + ":" + PLAYER_LOCAL_ID : "station:" + RunId + ":" + PaidStationOwner(kind) + ":" + kind;
        StationState CurrentPaidStation(string kind) => kind == "field_crafting" ? null : CraftingState.GetStation(kind);
        bool PaidStationExists(string kind, bool start) => kind == "field_crafting" || HomeShip?.SceneRoot != null && HomeShip.SceneRoot.IsValid &&
            CraftingState.GetStation(kind) != null && CraftingStations.Any(st => st.IsValid && st.StationKind == kind && ReferenceEquals(st.Parent, HomeShip.SceneRoot));
        InventoryState PrivateInventory(GdDict summary)
        {
            var inventory = new InventoryState(); inventory.ApplySummary(summary);
            double equipmentMass = summary.GetFloat("total_weight") - inventory.GetTotalWeight();
            inventory.ComponentMass = () => equipmentMass;
            return inventory;
        }
        GdDict GateContext(GdDict domain, string kind, string recipeId, GdDict job = null)
        {
            GdDict recipe = CraftingState.GetRecipe(recipeId), state = PaidState(domain);
            StationState station = CurrentPaidStation(kind);
            var inventory = PrivateInventory(domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("inventory"));
            GdDict output = recipe.GetDictOrEmpty("produces");
            bool busy = state.GetDictOrEmpty("jobs").Values.OfType<GdDict>().Any(row => row.GetString("channel") == CraftChannel(kind) && row.GetString("input_state") == "paid" && !PaidCraftingState.Terminal(row) && row.GetString("job_id") != job?.GetString("job_id"));
            return new GdDict { { "recipe", recipe.DeepCopy() }, { "equipment_reason", PaidRecipePreflight(recipe) }, { "station_exists", PaidStationExists(kind, job == null || job.GetString("input_state") == "unpaid") },
                { "station_kind", kind }, { "station_id", PaidStationId(kind) }, { "station_owner_id", PaidStationOwner(kind) }, { "run_id", RunId }, { "actor_id", PLAYER_LOCAL_ID },
                { "skill", PlayerProgression.GetSkillLevel("fabrication") }, { "tier", station?.EffectiveTier() ?? 0L }, { "powered", station?.Powered ?? true }, { "busy", busy },
                { "knowledge", state.GetDictOrEmpty("knowledge").DeepCopy() }, { "inventory", domain.GetDictOrEmpty("participating_state").GetDictOrEmpty("inventory").DeepCopy() },
                { "output_capacity", inventory.CanAccept(output.GetString("item_id"), output.GetInt("quantity")) }, { "job", job?.DeepCopy() ?? new GdDict() },
                { "payment_receipt", domain.GetDictOrEmpty("receipts").GetDictOrEmpty(job?.GetString("payment_commit_id") ?? "").DeepCopy() } };
        }
        string Gate(GdDict domain, string kind, string recipe, string phase, GdDict job = null)
            => new RecipeGateService().Evaluate(recipe, phase, GateContext(domain, kind, recipe, job)).GetString("reason");
        string PaidAvailability(string kind, string recipe)
        {
            if (!EnsurePaidOwner()) return "paid_crafting_inactive";
            return Gate(CapturePaidCraftingDomain(), kind, recipe, "preview");
        }
        GdDict PaidContextFingerprint()
        {
            var stations = new GdDict();
            foreach (string kind in CRAFTING_STATION_KINDS)
            {
                StationState station = CraftingState.GetStation(kind);
                if (station != null) stations[kind] = GdArray.Of(station.Level, station.EffectiveTier(), station.Powered, PaidStationExists(kind, false),
                    new GdArray(CraftingStations.Where(st => st.IsValid && st.StationKind == kind && ReferenceEquals(st.Parent, HomeShip?.SceneRoot))
                        .Select(st => (object)(long)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(st))));
            }
            return new GdDict { { "run_id", RunId }, { "home", HomeShip?.ShipId ?? "" }, { "playable", PlayableStarted && !SliceComplete },
                { "incapacitated", VitalsState?.IsIncapacitated() == true }, { "stations", stations }, { "recipes", RecipeCatalog() }, { "multipliers", PlayerProgression.XpMultipliers.DeepCopy() } };
        }
        void ValidateFinalPaidPublication(GdDict before)
        {
            if (ComponentTerminalPending || SliceComplete || !PlayableStarted || VitalsState?.IsIncapacitated() == true ||
                !V.VariantEquals(before.Get("participating_state"), ReadComponentParticipants()) ||
                !V.VariantEquals(_paidPublicationContext, PaidContextFingerprint())) throw new InvalidOperationException("stale_context");
            _studyPublicationGate?.Invoke();
            if (_paidRewardContext && (!ReferenceEquals(_paidRewardFilter, TrainingEventBus.EventFilter) || !ReferenceEquals(_paidRewardGate, TrainingEventBus.SkillGate))) throw new InvalidOperationException("stale_training_policy");
            if (_paidMaterialInputs != null)
                foreach (var input in _paidMaterialInputs)
                    if (MaterialState.GetQuality(V.Str(input.Key)) != V.F64(input.Value)) throw new InvalidOperationException("stale_material_quality");
        }
        GdDict CraftCommand(GdDict domain, string action, string commandId, string kind = "", string recipe = "", string job = "", string legacy = "", string decision = "")
        {
            bool internalStep = commandId == null && (action == "progress" || action == "block");
            string id = commandId ?? (internalStep ? PaidCraftingState.InternalCommandPrefix : "paid:") + RunId + ":" + checked(domain.GetInt("command_sequence") + 1);
            return new GdDict { { "command_id", id }, { "internal", internalStep }, { "action", action }, { "station_kind", kind }, { "recipe_id", recipe }, { "job_id", job }, { "reconciliation_id", legacy }, { "decision", decision } };
        }
        GdDict PriorCraftResult(GdDict domain, GdDict command)
        {
            if (!command.GetBool("internal") && command.GetString("command_id").StartsWith(PaidCraftingState.InternalCommandPrefix, StringComparison.Ordinal)) return PaidFailure("reserved_command_id");
            foreach (GdDict receipt in domain.GetDictOrEmpty("receipts").Values.OfType<GdDict>())
                if (receipt.GetString("command_id") == command.GetString("command_id"))
                    return receipt.GetString("command_hash") == PaidCraftingState.Hash(command) ? _componentDomain.Commit(receipt.GetString("commit_id")) : PaidFailure("command_collision");
            return null;
        }
        GdDict ExecutePaid(GdDict command, Func<GdDict, GdDict> effect)
        {
            if (DomainPublicationInProgress) return PaidFailure("reentrant_mutation");
            if (ComponentTerminalPending || SliceComplete || !PlayableStarted || VitalsState?.IsIncapacitated() == true) return PaidFailure("terminal_pending");
            _paidPublicationContext = PaidContextFingerprint(); _componentMutating = true;
            try
            {
                GdDict prepared = _componentDomain.PrepareCraft(command, candidate => {
                    GdDict result = effect(candidate);
                    if (result.GetBool("ok", true)) { candidate["command_sequence"] = checked(candidate.GetInt("command_sequence") + 1); SetPaidProjections(candidate); }
                    return result;
                });
                if (!prepared.GetBool("ok") || prepared.GetBool("committed")) return prepared;
                return _componentDomain.Commit(prepared.GetString("transaction_id"));
            }
            finally { _componentMutating = false; _paidPublicationContext = null; _paidMaterialInputs = null; _paidRewardContext = false; _paidRewardFilter = null; _paidRewardGate = null; }
        }
        GdDict NewUnpaid(GdDict domain, string kind, string recipe, string id)
            => new GdDict { { "job_id", id }, { "recipe_id", recipe }, { "run_id", RunId }, { "actor_id", PLAYER_LOCAL_ID },
                { "inventory_owner_id", "player:" + PLAYER_LOCAL_ID }, { "station_owner_id", PaidStationOwner(kind) }, { "station_id", PaidStationId(kind) }, { "station_kind", kind },
                { "channel", CraftChannel(kind) }, { "input_state", "unpaid" }, { "status", "unpaid" }, { "payment_commit_id", "" }, { "resume_required", false }, { "blocked_reason", "" } };
        GdDict PayJob(GdDict candidate, string kind, string recipeId, string commitId, string jobId)
        {
            GdDict jobs = PaidState(candidate).GetDictOrEmpty("jobs"), prior = jobs.GetDictOrEmpty(jobId);
            string gate = Gate(candidate, kind, recipeId, "start", prior.IsEmpty ? null : prior);
            if (gate != "ready") return PaidFailure(gate);
            GdDict recipe = CraftingState.GetRecipe(recipeId).DeepCopy();
            if (!Finite(recipe.GetFloat("craft_time_seconds")) || recipe.GetFloat("craft_time_seconds") <= 0) return PaidFailure("invalid_recipe_duration");
            if (!PaidCraftingState.TryRecipeIngredients(recipe, out GdDict ingredients)) return PaidFailure("invalid_recipe");
            GdDict job = prior.IsEmpty ? NewUnpaid(candidate, kind, recipeId, jobId) : prior;
            var inventory = PrivateInventory(candidate.GetDictOrEmpty("participating_state").GetDictOrEmpty("inventory"));
            foreach (var input in ingredients)
                if (inventory.RemoveItem(V.Str(input.Key), (long)input.Value) != (long)input.Value) return PaidFailure("missing_ingredients");
            StationState station = CurrentPaidStation(kind);
            long skill = PlayerProgression.GetSkillLevel("fabrication"), level = station?.Level ?? 0L;
            double material = MaterialState.AverageIngredientQuality(recipe.GetDictOrEmpty("ingredients"));
            _paidMaterialInputs = new GdDict();
            foreach (object ingredient in ingredients.Keys) _paidMaterialInputs[ingredient] = MaterialState.GetQuality(V.Str(ingredient));
            GdDict quality = new QualityTierResolver().Resolve(material, skill, level, station?.Powered ?? false);
            job["input_state"] = "paid"; job["status"] = station == null || station.Powered ? "running" : "paused";
            job["recipe_definition"] = recipe; job["recipe_hash"] = PaidCraftingState.Hash(recipe); job["consumed"] = ingredients;
            job["start_skill"] = skill; job["start_level"] = level; job["start_tier"] = station?.EffectiveTier() ?? 0L; job["start_known"] = true; job["start_powered"] = station?.Powered ?? false;
            job["material_quality"] = material; job["quality_score"] = quality.Get("score"); job["quality_tier"] = quality.Get("tier"); job["quality_multiplier"] = quality.Get("multiplier");
            job["required_seconds"] = recipe.GetFloat("craft_time_seconds"); job["progress_seconds"] = 0.0; job["payment_commit_id"] = commitId;
            job["completion_commit_id"] = "craft:complete:" + jobId; job["resume_required"] = false; job["blocked_reason"] = "";
            jobs[jobId] = job; PaidState(candidate).GetDictOrEmpty("queues").GetArrayOrEmpty(CraftChannel(kind)).Remove(jobId);
            candidate.GetDictOrEmpty("participating_state")["inventory"] = inventory.GetSummary();
            return new GdDict { { "operation", "craft_start" }, { "reason", "started" }, { "job_id", jobId }, { "recipe_id", recipeId }, { "payment", PaidCraftingState.Payment(job) } };
        }
        public GdDict RequestPaidCraft(string stationKind, string recipeId, string commandId = null)
        {
            if (!EnsurePaidOwner()) return PaidFailure("paid_crafting_inactive");
            GdDict domain = CapturePaidCraftingDomain(), command = CraftCommand(domain, "start", commandId, stationKind, recipeId);
            GdDict prior = PriorCraftResult(domain, command); if (prior != null) return prior;
            if (PaidState(domain).GetDictOrEmpty("queues").GetArrayOrEmpty(CraftChannel(stationKind)).Count > 0) return PaidFailure("queue_pending");
            string gate = Gate(domain, stationKind, recipeId, "start");
            if (gate != "ready") return PaidFailure(gate);
            domain = CapturePaidCraftingDomain();
            string id = "craft_job:" + RunId + ":" + checked(domain.GetInt("command_sequence") + 1);
            return ExecutePaid(command, candidate => PayJob(candidate, stationKind, recipeId, "craft:" + command.GetString("command_id"), id));
        }
        public GdDict EnqueuePaidCraft(string stationKind, string recipeId, string commandId = null)
        {
            if (!EnsurePaidOwner()) return PaidFailure("paid_crafting_inactive");
            GdDict domain = CapturePaidCraftingDomain(), command = CraftCommand(domain, "enqueue", commandId, stationKind, recipeId);
            GdDict prior = PriorCraftResult(domain, command); if (prior != null) return prior;
            if (!CraftingState.HasRecipe(recipeId) || CraftingState.GetStationKind(recipeId) != stationKind) return PaidFailure("recipe_missing");
            if (!PaidStationExists(stationKind, true)) return PaidFailure("station_missing");
            string unavailable = PaidRecipePreflight(CraftingState.GetRecipe(recipeId)); if (unavailable.Length > 0) return PaidFailure(unavailable);
            if (PaidState(domain).GetDictOrEmpty("queues").GetArrayOrEmpty(CraftChannel(stationKind)).Count >= StationState.DEFAULT_MAX_QUEUE) return PaidFailure("queue_full");
            string id = "craft_job:" + RunId + ":" + checked(domain.GetInt("command_sequence") + 1);
            return ExecutePaid(command, candidate => {
                PaidState(candidate).GetDictOrEmpty("jobs")[id] = NewUnpaid(candidate, stationKind, recipeId, id);
                PaidState(candidate).GetDictOrEmpty("queues").GetArrayOrEmpty(CraftChannel(stationKind)).Add(id);
                return new GdDict { { "operation", "craft_enqueue" }, { "reason", "queued" }, { "job_id", id }, { "recipe_id", recipeId } };
            });
        }
        public GdArray ListPaidCraftJobs(string stationKind = null)
        {
            if (!EnsurePaidOwner()) return new GdArray();
            return new GdArray(PaidState(CapturePaidCraftingDomain()).GetDictOrEmpty("jobs").Values.OfType<GdDict>().Where(j => stationKind == null || j.GetString("station_kind") == stationKind).Select(j => j.DeepCopy()));
        }
        public GdArray ListLegacyCraftReconciliations(string stationKind = null)
        {
            if (!EnsurePaidOwner()) return new GdArray();
            return new GdArray(PaidState(CapturePaidCraftingDomain()).GetDictOrEmpty("legacy").Values.OfType<GdDict>().Where(j => stationKind == null || j.GetString("station_kind") == stationKind).Select(j => j.DeepCopy()));
        }

        public GdDict ResumePaidCraft(string jobId, string commandId = null) => ConsentOrRetryPaid(jobId, commandId, "resume");
        public GdDict RetryPaidCraft(string jobId, string commandId = null) => ConsentOrRetryPaid(jobId, commandId, "retry");
        GdDict ConsentOrRetryPaid(string jobId, string commandId, string action)
        {
            if (!EnsurePaidOwner()) return PaidFailure("paid_crafting_inactive");
            GdDict domain = CapturePaidCraftingDomain(), command = CraftCommand(domain, action, commandId, job: jobId);
            GdDict repeat = PriorCraftResult(domain, command); if (repeat != null) return repeat;
            GdDict job = PaidState(domain).GetDictOrEmpty("jobs").GetDictOrEmpty(jobId);
            if (job.IsEmpty) return PaidFailure("job_missing");
            if (PaidCraftingState.Terminal(job)) return _componentDomain.Commit(job.GetString(job.GetString("status") == "completed_delivered" ? "completion_commit_id" : "terminal_commit_id"));
            if (job.GetString("input_state") == "unpaid")
            {
                GdArray queue = PaidState(domain).GetDictOrEmpty("queues").GetArrayOrEmpty(job.GetString("channel"));
                if (queue.Count == 0 || V.Str(queue[0]) != jobId) return PaidFailure("queue_not_head");
                return ExecutePaid(command, candidate => PayJob(candidate, job.GetString("station_kind"), job.GetString("recipe_id"), "craft:" + command.GetString("command_id"), jobId));
            }
            if (!job.GetBool("resume_required") && job.GetString("status") == "completed_pending_delivery") return DeliverPaid(jobId, command);
            GdDict consent = ExecutePaid(command, candidate => {
                GdDict row = PaidState(candidate).GetDictOrEmpty("jobs").GetDictOrEmpty(jobId);
                row["resume_required"] = false;
                row["status"] = row.GetFloat("progress_seconds") == row.GetFloat("required_seconds") ? "completed_pending_delivery" : "running";
                row["blocked_reason"] = "";
                return new GdDict { { "operation", "craft_resume" }, { "reason", "resumed" }, { "job_id", jobId } };
            });
            if (consent.GetBool("committed") && PaidState(_componentDomain.GetSummary()).GetDictOrEmpty("jobs").GetDictOrEmpty(jobId).GetString("status") == "completed_pending_delivery")
                DeliverPaid(jobId, null);
            return consent;
        }
        public GdDict CancelPaidCraft(string jobId, string commandId = null)
        {
            if (!EnsurePaidOwner()) return PaidFailure("paid_crafting_inactive");
            GdDict domain = CapturePaidCraftingDomain(), command = CraftCommand(domain, "cancel", commandId, job: jobId);
            GdDict repeat = PriorCraftResult(domain, command); if (repeat != null) return repeat;
            GdDict original = PaidState(domain).GetDictOrEmpty("jobs").GetDictOrEmpty(jobId);
            if (original.IsEmpty || original.GetString("input_state") != "paid") return PaidFailure("paid_job_missing");
            if (PaidCraftingState.Terminal(original)) return PaidFailure("job_terminal");
            return ExecutePaid(command, candidate => {
                GdDict job = PaidState(candidate).GetDictOrEmpty("jobs").GetDictOrEmpty(jobId);
                job["status"] = "cancelled"; job["resume_required"] = false; job["blocked_reason"] = "cancelled"; job["terminal_commit_id"] = "craft:" + command.GetString("command_id");
                foreach (GdDict queued in PaidState(candidate).GetDictOrEmpty("jobs").Values.OfType<GdDict>())
                    if (queued.GetString("channel") == job.GetString("channel") && queued.GetString("input_state") == "unpaid") queued["resume_required"] = true;
                return new GdDict { { "operation", "craft_cancel" }, { "reason", "cancelled" }, { "job_id", jobId } };
            });
        }
        void CancelPaidChannel(string channel)
        {
            if (!EnsurePaidOwner()) return;
            GdDict job = PaidState(CapturePaidCraftingDomain()).GetDictOrEmpty("jobs").Values.OfType<GdDict>().FirstOrDefault(j => j.GetString("channel") == channel && j.GetString("input_state") == "paid" && !PaidCraftingState.Terminal(j));
            if (job != null) CancelPaidCraft(job.GetString("job_id"));
        }
        void AdvancePaidCrafting(double delta, string channel)
        {
            if (!Finite(delta) || delta < 0 || DomainPublicationInProgress || !EnsurePaidOwner() || SliceComplete || ComponentTerminalPending) return;
            if (!_componentDomain.HasExecutablePaidChannel(channel)) return;
            GdDict domain = CapturePaidCraftingDomain();
            GdDict job = PaidState(domain).GetDictOrEmpty("jobs").Values.OfType<GdDict>().FirstOrDefault(j => j.GetString("channel") == channel && j.GetString("input_state") == "paid" && !PaidCraftingState.Terminal(j));
            if (job == null || job.GetBool("resume_required")) return;
            string id = job.GetString("job_id");
            if (job.GetString("status") == "completed_pending_delivery") { DeliverPaid(id, null); return; }
            string gate = Gate(domain, job.GetString("station_kind"), job.GetString("recipe_id"), "advance", job);
            if (gate != "ready")
            {
                if (job.GetString("status") != "paused" || job.GetString("blocked_reason") != gate)
                    ExecutePaid(CraftCommand(domain, "block", null, job: id), candidate => {
                        GdDict row = PaidState(candidate).GetDictOrEmpty("jobs").GetDictOrEmpty(id); row["status"] = "paused"; row["blocked_reason"] = gate;
                        return new GdDict { { "operation", "craft_block" }, { "reason", gate }, { "job_id", id } };
                    });
                return;
            }
            if (delta <= 0) return;
            GdDict progressed = ExecutePaid(CraftCommand(domain, "progress", null, job: id), candidate => {
                GdDict row = PaidState(candidate).GetDictOrEmpty("jobs").GetDictOrEmpty(id);
                row["progress_seconds"] = Math.Min(row.GetFloat("required_seconds"), row.GetFloat("progress_seconds") + delta);
                row["status"] = row.GetFloat("progress_seconds") == row.GetFloat("required_seconds") ? "completed_pending_delivery" : "running"; row["blocked_reason"] = "";
                return new GdDict { { "operation", "craft_progress" }, { "reason", "advanced" }, { "job_id", id }, { "delta_seconds", delta } };
            });
            if (progressed.GetBool("committed") && PaidState(_componentDomain.GetSummary()).GetDictOrEmpty("jobs").GetDictOrEmpty(id).GetString("status") == "completed_pending_delivery") DeliverPaid(id, null);
        }
        string CraftTrainingEvent(GdDict job)
        {
            string kind = job.GetString("station_kind"), recipe = job.GetString("recipe_id"), item = job.GetDictOrEmpty("recipe_definition").GetDictOrEmpty("produces").GetString("item_id");
            if (kind == "kitchen" || kind == "synthesizer" && RecipeIsCooking(recipe, item)) return "cook_meal";
            if (kind == "workbench" || kind == "fabricator" || kind == "field_crafting") return "fabricate_part";
            if (kind == "medbay" && (recipe.Contains("stim") || item.Contains("stim"))) return "compound_stimulant";
            return "";
        }
        void StagePaidTraining(GdDict candidate, GdDict job, GdDict effect)
        {
            _paidRewardContext = true; _paidRewardFilter = TrainingEventBus.EventFilter; _paidRewardGate = TrainingEventBus.SkillGate;
            GdDict participants = candidate.GetDictOrEmpty("participating_state");
            effect["progression_before"] = participants.GetDictOrEmpty("progression").DeepCopy();
            effect["training_before"] = participants.GetDictOrEmpty("training").DeepCopy();
            string eventId = CraftTrainingEvent(job); effect["training_event"] = eventId; effect["xp_multipliers"] = PlayerProgression.XpMultipliers.DeepCopy();
            if (eventId.Length > 0)
            {
                var progression = new PlayerProgressionState(); var classes = ClassDefinition.LoadAll(); classes.TryGetValue(PlayerProgression.ClassId, out ClassDefinition definition);
                progression.Configure(definition, PlayerProgressionState.LoadSkillsCatalog(), PlayerProgression.GetBooksCatalog());
                if (!PaidCraftRewardProof.CopyProgressionExact(progression, participants.GetDictOrEmpty("progression"))) throw new ArgumentException("invalid_paid_progression");
                progression.XpMultipliers.Clear(); foreach (var entry in PlayerProgression.XpMultipliers) progression.XpMultipliers[entry.Key] = entry.Value;
                var emitter = new TrainingEventBus(); emitter.Configure(); emitter.ApplySummary(participants.GetDictOrEmpty("training"));
                emitter.SkillGate = _paidRewardGate; emitter.EventFilter = _paidRewardFilter;
                GdDict record = emitter.Emit(eventId, job.GetDictOrEmpty("recipe_definition").GetDictOrEmpty("produces").GetString("item_id"), progression);
                var recorded = new TrainingEventBus(); recorded.Configure(); recorded.ApplySummary(participants.GetDictOrEmpty("training"));
                if (record != null) recorded.RecordApplied(record, job.GetString("completion_commit_id"));
                GdDict training = recorded.ToDict(); training["xp_total"] = emitter.GetTotalXpDelivered(); training["dropped"] = emitter.GetDroppedCount();
                participants["progression"] = progression.GetSummary(); participants["training"] = training;
                effect["training_record"] = record == null ? null : recorded.GetLog().OfType<GdDict>().Last().DeepCopy();
            }
            else effect["training_record"] = null;
            effect["progression_after"] = participants.GetDictOrEmpty("progression").DeepCopy();
            effect["training_after"] = participants.GetDictOrEmpty("training").DeepCopy();
            PaidCraftRewardProof.Compact(PaidState(candidate), effect);
        }
        GdDict DeliverPaid(string jobId, GdDict suppliedCommand)
        {
            GdDict domain = CapturePaidCraftingDomain(), job = PaidState(domain).GetDictOrEmpty("jobs").GetDictOrEmpty(jobId);
            if (job.IsEmpty) return PaidFailure("job_missing");
            if (job.GetString("status") == "completed_delivered") return _componentDomain.Commit(job.GetString("completion_commit_id"));
            string gate = Gate(domain, job.GetString("station_kind"), job.GetString("recipe_id"), "complete", job);
            if (gate != "ready") return PaidFailure(gate);
            GdDict command = suppliedCommand ?? CraftCommand(domain, "complete", "complete:" + jobId, job: jobId);
            GdDict delivered = ExecutePaid(command, candidate => {
                GdDict participants = candidate.GetDictOrEmpty("participating_state"), row = PaidState(candidate).GetDictOrEmpty("jobs").GetDictOrEmpty(jobId);
                var inventory = PrivateInventory(participants.GetDictOrEmpty("inventory"));
                GdDict output = row.GetDictOrEmpty("recipe_definition").GetDictOrEmpty("produces");
                if (!inventory.CanAccept(output.GetString("item_id"), output.GetInt("quantity")) || inventory.AddItem(output.GetString("item_id"), output.GetInt("quantity")) != output.GetInt("quantity")) return PaidFailure("output_full");
                participants["inventory"] = inventory.GetSummary(); row["status"] = "completed_delivered"; row["resume_required"] = false; row["blocked_reason"] = "";
                var effect = new GdDict { { "operation", "craft_complete" }, { "reason", "delivered" }, { "job_id", jobId }, { "recipe_id", row.Get("recipe_id") }, { "output", output.DeepCopy() } };
                StagePaidTraining(candidate, row, effect);
                var spoilage = new SpoilageState(); RestoreExactSpoilage(spoilage, participants.GetDictOrEmpty("spoilage"));
                string itemId = output.GetString("item_id"), category = inventory.GetCategory(itemId);
                if ((category == "food" || category == "drink") && !spoilage.HasFood(itemId)) spoilage.AddFood(itemId, inventory.GetDefinition(itemId));
                participants["spoilage"] = spoilage.GetSummary();
                return effect;
            });
            if (delivered.GetBool("committed")) TryAutoStartPaidQueue(job.GetString("channel"));
            return delivered;
        }
        void TryAutoStartPaidQueue(string channel)
        {
            GdDict domain = CapturePaidCraftingDomain(); GdArray queue = PaidState(domain).GetDictOrEmpty("queues").GetArrayOrEmpty(channel);
            if (queue.Count == 0) return;
            string id = V.Str(queue[0]); GdDict job = PaidState(domain).GetDictOrEmpty("jobs").GetDictOrEmpty(id);
            if (job.GetBool("resume_required")) return;
            GdDict result = RetryPaidCraft(id);
            if (result.GetBool("ok")) return;
            domain = CapturePaidCraftingDomain(); string reason = result.GetString("reason");
            if (PaidState(domain).GetDictOrEmpty("jobs").GetDictOrEmpty(id).GetString("blocked_reason") == reason) return;
            ExecutePaid(CraftCommand(domain, "block", null, job: id), candidate => {
                PaidState(candidate).GetDictOrEmpty("jobs").GetDictOrEmpty(id)["blocked_reason"] = reason;
                return new GdDict { { "operation", "craft_block" }, { "reason", reason }, { "job_id", id } };
            });
        }
        void NotifyPaidCraft(GdDict result)
        {
            GdDict effect = result.GetDictOrEmpty("result");
            if (effect.GetString("operation") == "craft_complete")
            {
                GdDict job = PaidState(_componentDomain.GetSummary()).GetDictOrEmpty("jobs").GetDictOrEmpty(effect.GetString("job_id"));
                PlaySfx(AudioEventSeam.SFX_CRAFT_COMPLETE);
                Log.Info("CRAFT COMPLETED item=" + effect.GetDictOrEmpty("output").GetString("item_id") + " qty=" + effect.GetDictOrEmpty("output").GetInt("quantity") + " quality=" + job.GetString("quality_tier"));
            }
            else if (effect.GetString("operation") == "craft_start" || effect.GetString("decision") == "start_fresh") PlaySfx(AudioEventSeam.SFX_TOOL_USE);
            RefreshInventoryHud(); RecomputePlayerEncumbrance();
        }

        public bool ValidatePaidCraftingRestore(GdDict summary, out string reason)
        {
            if (ComponentGenerationRestoreInProgress) { reason = "restore_in_progress"; return false; }
            return ValidatePaidCraftingRestoreInContext(summary, CurrentPaidRestoreContext(), out reason);
        }

        bool ValidatePaidCraftingRestoreInContext(GdDict summary, PaidRestoreContext context, out string reason)
        {
            reason = "paid_crafting_inactive";
            if (!PaidCraftingEnabled) return false;
            if (!DomainBundle.TryCreate(summary, out _, out reason) || !PaidCraftingState.IsDomainVersion(summary.GetInt("schema_version")) || summary.GetInt("schema_version") == 4 && !ManualStudyEnabled) return false;
            if (summary.GetString("domain_mode") != (ComponentIntegrationEnabled ? "components_and_craft" : "craft_only")) { reason = "domain_mode_mismatch"; return false; }
            if (ComponentIntegrationEnabled && !ValidateComponentDomainRestore(summary, out reason)) return false;
            GdDict state = PaidState(summary), participants = summary.GetDictOrEmpty("participating_state");
            if (context == null || state.GetString("run_id") != context.RunId || state.GetString("actor_id") != PLAYER_LOCAL_ID) { reason = "paid_owner_mismatch"; return false; }
            foreach (GdDict job in state.GetDictOrEmpty("jobs").Values.OfType<GdDict>())
            {
                string kind = job.GetString("station_kind"), recipe = job.GetString("recipe_id");
                if (job.GetString("station_id") != context.StationId(kind) || job.GetString("station_owner_id") != context.StationOwner(kind) ||
                    job.GetString("inventory_owner_id") != "player:" + PLAYER_LOCAL_ID || !context.StationExists(kind)) { reason = "paid_station_owner_mismatch"; return false; }
                if (!PaidCraftingState.Terminal(job) && (!context.Crafting.HasRecipe(recipe) || context.Crafting.GetStationKind(recipe) != kind ||
                    job.GetString("input_state") == "paid" && PaidCraftingState.Hash(context.Crafting.GetRecipe(recipe)) != job.GetString("recipe_hash")))
                { reason = "recipe_definition_mismatch"; return false; }
            }
            if (!ValidatePaidMirrors(participants)) { reason = "paid_projection_mismatch"; return false; }
            foreach (var entry in participants.GetDictOrEmpty("inventory").GetDictOrEmpty("items"))
                if (!(entry.Key is string) || !(entry.Value is long amount) || amount <= 0) { reason = "invalid_paid_inventory"; return false; }
            var training = new TrainingEventBus(); training.Configure();
            if (!training.ApplySummary(participants.GetDictOrEmpty("training"))) { reason = "invalid_paid_training"; return false; }
            if (!(participants.Get("spoilage") is GdDict spoilage)) { reason = "missing_paid_spoilage"; return false; }
            try
            {
                var exact = new SpoilageState(); RestoreExactSpoilage(exact, spoilage);
                if (!V.VariantEquals(spoilage, exact.GetSummary())) { reason = "invalid_paid_spoilage"; return false; }
            }
            catch { reason = "invalid_paid_spoilage"; return false; }
            reason = "ok"; return true;
        }
        internal static bool ValidatePaidMirrors(GdDict participants)
        {
            GdDict state = participants.GetDictOrEmpty("paid_crafting");
            foreach (string channel in new[] { "station", "field" })
            {
                GdDict summary = channel == "station" ? participants.GetDictOrEmpty("crafting") : participants.GetDictOrEmpty("field_crafting").GetDictOrEmpty("field_crafting");
                GdDict job = state.GetDictOrEmpty("jobs").Values.OfType<GdDict>().FirstOrDefault(j => j.GetString("channel") == channel && j.GetString("input_state") == "paid" && !PaidCraftingState.Terminal(j));
                GdDict active = summary.GetDictOrEmpty("active_craft");
                if (job == null) { if (!active.IsEmpty) return false; }
                else
                {
                    foreach (string key in new[] { "recipe_id", "station_kind", "quality_score", "quality_tier", "quality_multiplier" }) if (!V.VariantEquals(job.Get(key), active.Get(key))) return false;
                    GdDict station = summary.GetDictOrEmpty("station_summaries").GetDictOrEmpty(job.GetString("station_kind"));
                    if (station.GetString("active_recipe_id") != job.GetString("recipe_id") || !V.VariantEquals(station.Get("progress_seconds"), job.Get("progress_seconds")) ||
                        !V.VariantEquals(station.Get("required_seconds"), job.Get("required_seconds"))) return false;
                    long status = job.GetString("status") == "completed_pending_delivery" ? 3L : job.GetBool("resume_required") || job.GetString("status") == "paused" ? 2L : 1L;
                    if (station.GetInt("status") != status || station.GetBool("resume_required") != job.GetBool("resume_required")) return false;
                }
                foreach (object id in state.GetDictOrEmpty("queues").GetArrayOrEmpty(channel))
                    if (!summary.GetDictOrEmpty("station_summaries").Has(state.GetDictOrEmpty("jobs").GetDictOrEmpty(id).GetString("station_kind"))) return false;
                foreach (var entry in summary.GetDictOrEmpty("station_summaries"))
                {
                    var expectedQueue = new GdArray();
                    foreach (object id in state.GetDictOrEmpty("queues").GetArrayOrEmpty(channel))
                    { GdDict queued = state.GetDictOrEmpty("jobs").GetDictOrEmpty(id); if (queued.GetString("station_kind") == V.Str(entry.Key)) expectedQueue.Add(queued.Get("recipe_id")); }
                    if (!(entry.Value is GdDict station) || !V.VariantEquals(expectedQueue, station.Get("queue"))) return false;
                    if ((job == null || job.GetString("station_kind") != V.Str(entry.Key)) && station.GetString("active_recipe_id").Length != 0) return false;
                }
            }
            return true;
        }
        public bool RestorePaidCraftingDomain(GdDict summary)
        {
            if (DomainPublicationInProgress || !ValidatePaidCraftingRestore(summary, out _)) return false;
            GdDict candidate = summary.DeepCopy();
            if (candidate.GetInt("schema_version") == 3 && ManualStudyEnabled)
            { candidate["schema_version"] = 4L; candidate.GetDictOrEmpty("participating_state")["manual_study"] = ManualStudyState.New(PaidState(candidate).GetString("run_id"), PLAYER_LOCAL_ID); }
            GdDict studyJob = ManualStudyState.State(candidate).GetDictOrEmpty("job");
            if (!studyJob.IsEmpty && studyJob.GetString("status") != "completed")
            { studyJob["status"] = "paused"; studyJob["resume_required"] = true; studyJob["reason"] = "explicit_resume_required"; }
            foreach (GdDict job in PaidState(candidate).GetDictOrEmpty("jobs").Values.OfType<GdDict>())
                if (!PaidCraftingState.Terminal(job)) { job["resume_required"] = true; if (job.GetString("status") == "running") job["status"] = "paused"; }
            // Preserve saved station metadata; consent and executable rows are projected from saved authority.
            PauseSavedPaidMirrors(candidate.GetDictOrEmpty("participating_state"));
            GdDict work = candidate.GetDictOrEmpty("component_work");
            if (!work.IsEmpty && work.GetString("status") != "committed") { work["status"] = "paused_restore"; work["resume_required"] = true; work["reason"] = "explicit_resume_required"; }
            try
            {
                DomainTransactionCoordinator next = NewComponentOwner(candidate);
                ApplyComponentViews(candidate); _componentDomain = next;
                if (ComponentIntegrationEnabled) { BindComponentReadViews(); ProjectComponentPlacement(candidate); }
                BindPaidCraftingModels(); _workHoldInput = false; _studyConsent = false; RefreshStudyHud();
                return true;
            }
            catch { return false; }
        }
        static void PauseSavedPaidMirrors(GdDict participants)
        {
            foreach (GdDict job in participants.GetDictOrEmpty("paid_crafting").GetDictOrEmpty("jobs").Values.OfType<GdDict>())
            {
                if (job.GetString("input_state") != "paid" || PaidCraftingState.Terminal(job)) continue;
                GdDict summary = job.GetString("channel") == "field" ? participants.GetDictOrEmpty("field_crafting").GetDictOrEmpty("field_crafting") : participants.GetDictOrEmpty("crafting");
                GdDict station = summary.GetDictOrEmpty("station_summaries").GetDictOrEmpty(job.GetString("station_kind"));
                station["resume_required"] = true; station["status"] = job.GetString("status") == "completed_pending_delivery" ? 3L : 2L;
            }
        }

        GdDict LegacyImportCommand(GdDict domain, GdDict stationSummary, GdDict fieldSummary, string sourceId, string sourceHash)
        {
            GdDict command = CraftCommand(domain, "legacy_import", "legacy-import:" + PaidCraftingState.Hash(GdArray.Of(sourceId, sourceHash)));
            command["source_id"] = sourceId; command["source_hash"] = sourceHash;
            command["station_summary"] = stationSummary.DeepCopy(); command["field_summary"] = fieldSummary.DeepCopy();
            return command;
        }
        GdDict StageLegacyImport(GdDict candidate, GdDict command)
        {
            string source = command.GetString("source_id"), hash = command.GetString("source_hash"), commit = "craft:" + command.GetString("command_id");
            if (string.IsNullOrWhiteSpace(source) || hash.Length != 64 || hash.Any(ch => !Uri.IsHexDigit(ch))) return PaidFailure("invalid_legacy_source");
            GdDict added = new GdDict();
            void Add(object original, GdDict station, string kind, string path, string channel)
            {
                string id = PaidCraftingState.LegacyId(source, hash, path);
                string recipe = original is GdDict row ? row.GetString("recipe_id", row.GetString("active_recipe_id")) : V.Str(original);
                var record = new GdDict { { "reconciliation_id", id }, { "source_id", source }, { "source_hash", hash }, { "source_path", path },
                    { "original_record", V.DeepCopy(original) }, { "original_hash", PaidCraftingState.Hash(original) },
                    { "original_context", new GdDict { { "station_summary", station.DeepCopy() }, { "station_kind", kind }, { "channel", channel } } },
                    { "recipe_id", recipe }, { "station_kind", kind }, { "payment_state", "unverified" }, { "status", "paused_unverified" },
                    { "disposition", "" }, { "decision_commit_id", "" }, { "import_commit_id", commit } };
                if (!PaidState(candidate).GetDictOrEmpty("legacy").Has(id)) { PaidState(candidate).GetDictOrEmpty("legacy")[id] = record; added[id] = record.DeepCopy(); }
            }
            void Read(GdDict summary, string prefix, string channel)
            {
                GdDict active = summary.GetDictOrEmpty("active_craft"), stations = summary.GetDictOrEmpty("station_summaries");
                string activeKind = active.GetString("station_kind");
                if (!active.IsEmpty) Add(active, stations.GetDictOrEmpty(activeKind), activeKind, prefix + "/active_craft", channel);
                foreach (var entry in stations)
                {
                    if (!(entry.Value is GdDict station)) throw new ArgumentException("invalid_legacy_station");
                    string kind = V.Str(entry.Key);
                    if (station.GetString("active_recipe_id").Length > 0 && (active.IsEmpty || kind != activeKind || station.GetString("active_recipe_id") != active.GetString("recipe_id"))) Add(station, station, kind, prefix + "/station_summaries/" + kind + "/active", channel);
                    GdArray queue = station.GetArrayOrEmpty("queue");
                    for (int i = 0; i < queue.Count; i++) Add(queue[i], station, kind, prefix + "/station_summaries/" + kind + "/queue/" + i, channel);
                }
            }
            Read(command.GetDictOrEmpty("station_summary"), "crafting", "station");
            GdDict field = command.GetDictOrEmpty("field_summary");
            Read(field.Has("field_crafting") ? field.GetDictOrEmpty("field_crafting") : field, "field_crafting", "field");
            return new GdDict { { "operation", "craft_legacy_import" }, { "reason", "legacy_quarantined" }, { "legacy_records", added } };
        }
        public GdDict PrepareLegacyCrafting(GdDict domain, GdDict stationSummary, GdDict fieldSummary, string sourceId, string sourceHash)
        {
            if (!ValidatePaidCraftingRestore(domain, out string reason)) return PaidFailure(reason);
            if (stationSummary == null || fieldSummary == null || !ItemInstanceState.IsSafeSnapshot(stationSummary) || !ItemInstanceState.IsSafeSnapshot(fieldSummary)) return PaidFailure("invalid_legacy_summary");
            var owner = new DomainTransactionCoordinator(domain);
            GdDict command = LegacyImportCommand(domain, stationSummary, fieldSummary, sourceId, sourceHash);
            GdDict prepared = owner.PrepareCraft(command, candidate => {
                GdDict result = StageLegacyImport(candidate, command);
                if (result.GetBool("ok", true)) candidate["command_sequence"] = checked(candidate.GetInt("command_sequence") + 1);
                return result;
            });
            if (!prepared.GetBool("ok")) return prepared;
            return new GdDict { { "ok", true }, { "reason", "prepared" }, { "domain", prepared.GetBool("committed") ? domain.DeepCopy() : prepared.GetDictOrEmpty("candidate").DeepCopy() } };
        }
        public GdDict ImportLegacyCrafting(GdDict stationSummary, GdDict fieldSummary, string sourceId, string sourceHash)
        {
            if (!EnsurePaidOwner()) return PaidFailure("paid_crafting_inactive");
            if (stationSummary == null || fieldSummary == null || !ItemInstanceState.IsSafeSnapshot(stationSummary) || !ItemInstanceState.IsSafeSnapshot(fieldSummary)) return PaidFailure("invalid_legacy_summary");
            GdDict domain = CapturePaidCraftingDomain(), command = LegacyImportCommand(domain, stationSummary, fieldSummary, sourceId, sourceHash);
            GdDict repeat = PriorCraftResult(domain, command); if (repeat != null) return repeat;
            return ExecutePaid(command, candidate => StageLegacyImport(candidate, command));
        }
        public GdDict ReconcileLegacyCraft(string reconciliationId, string decision, string commandId = null)
        {
            if (!EnsurePaidOwner()) return PaidFailure("paid_crafting_inactive");
            GdDict domain = CapturePaidCraftingDomain(), command = CraftCommand(domain, "legacy_decision", commandId, legacy: reconciliationId, decision: decision);
            GdDict repeat = PriorCraftResult(domain, command); if (repeat != null) return repeat;
            GdDict old = PaidState(domain).GetDictOrEmpty("legacy").GetDictOrEmpty(reconciliationId);
            if (old.IsEmpty) return PaidFailure("legacy_record_missing");
            if (old.GetString("disposition") == "started_fresh" || old.GetString("disposition") == "abandoned") return PaidFailure("legacy_decision_terminal");
            if (decision != "keep_paused" && decision != "abandon" && decision != "start_fresh") return PaidFailure("invalid_legacy_decision");
            if (decision == "start_fresh" && PaidState(domain).GetDictOrEmpty("queues").GetArrayOrEmpty(CraftChannel(old.GetString("station_kind"))).Count > 0) return PaidFailure("queue_pending");
            return ExecutePaid(command, candidate => {
                GdDict row = PaidState(candidate).GetDictOrEmpty("legacy").GetDictOrEmpty(reconciliationId), effect;
                if (decision == "start_fresh")
                {
                    string jobId = "craft_job:" + RunId + ":" + checked(candidate.GetInt("command_sequence") + 1);
                    effect = PayJob(candidate, row.GetString("station_kind"), row.GetString("recipe_id"), "craft:" + command.GetString("command_id"), jobId);
                    if (!effect.GetBool("ok", true)) return effect;
                    row["disposition"] = "started_fresh";
                }
                else { effect = new GdDict(); row["disposition"] = decision == "abandon" ? "abandoned" : "keep_paused"; }
                row["decision_commit_id"] = "craft:" + command.GetString("command_id");
                effect["operation"] = "craft_legacy_decision"; effect["reason"] = row.GetString("disposition"); effect["decision"] = decision; effect["reconciliation_id"] = reconciliationId;
                return effect;
            });
        }
    }
}
