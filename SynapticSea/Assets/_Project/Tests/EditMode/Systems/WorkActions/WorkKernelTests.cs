using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    /// <summary>Diagnostic detached tickets and fake owners only; no scene, acquisition, saves or live effects.</summary>
    public class WorkKernelTests
    {
        const string Job = "job-A";

        static GdDict Ticket(string job = Job, string mode = "hold") => new GdDict
        {
            { "schema_version", 1L }, { "job_id", job }, { "action_id", "diagnostic-cut" },
            { "actor_id", "actor-A" }, { "ship_id", "ship-A" }, { "target_id", "room/wall" },
            { "owner_revision", 3L }, { "target_revision", 4L }, { "definition_hash", "fixture-definition-1" },
            { "local_anchor", Vec3.Zero }, { "range_m", 3.5 }, { "range_comparison", "inclusive" },
            { "los_policy", "none" }, { "input_mode", mode }, { "duration_seconds", 10.0 }, { "progress_seconds", 0.0 },
            { "requirements", new GdDict { { "tool_class", "crowbar" }, { "skill_id", "repair" },
                { "min_skill_level", 1L }, { "materials", new GdDict { { "scrap", 1L } } }, { "require_power", false } } },
            { "material_policy", "final_commit" }, { "payment_commit_id", "" }, { "output_owner", "actor-A" },
            { "committed_effect_ids", GdArray.Of("diagnostic-effect") },
            { "completion_command", new GdDict { { "command_id", "complete/" + job }, { "effect", "diagnostic" },
                { "extension", new GdDict { { "items", GdArray.Of(new GdDict { { "tag", "original" } }) } } } } },
            { "effort_policy", new GdDict { { "definition_hash", "fixture-definition-1" }, { "duration_basis", "eligible_seconds" },
                { "speed_rule", "constant" }, { "base_speed", 1.0 }, { "stamina_rule", "per_eligible_second" },
                { "stamina_per_eligible_second", 2.0 }, { "exhaustion_threshold", 0.001 },
                { "interrupt_on_damage", true }, { "power_loss_behavior", "pause" }, { "clip_final_tick", true } } },
        };

        static GdDict Context(string mode = "hold", bool held = true, bool resume = false) => new GdDict
        {
            { "actor_id", "actor-A" }, { "ship_id", "ship-A" }, { "owner_revision", 3L },
            { "target_id", "room/wall" }, { "target_revision", 4L }, { "definition_hash", "fixture-definition-1" },
            { "owner_valid", true }, { "target_exists", true }, { "player_local_position", Vec3.Zero },
            { "target_local_anchor", Vec3.Zero }, { "line_of_sight", true }, { "tool_class", "crowbar" },
            { "skill_id", "repair" }, { "skill_level", 1L }, { "inventory", new GdDict { { "scrap", 3L } } },
            { "inventory_revision", 8L }, { "power_available", true }, { "stamina", 100.0 }, { "max_stamina", 100.0 },
            { "damaged", false }, { "dead", false }, { "wound_speed_mult", 1.0 }, { "input_mode", mode },
            { "input_held", held }, { "resume_requested", resume }, { "payment_commit_id", "" },
        };

        static GdDict Row(WorkTransactionState state, string id = Job) => state.GetSummary().GetDictOrEmpty("jobs").GetDictOrEmpty(id);
        static GdDict Policy(GdDict ticket) => ticket.GetDictOrEmpty("effort_policy");
        static void Same(object expected, object actual) => Assert.IsTrue(V.VariantEquals(expected, actual), "exact detached state changed");
        static GdDict Accepted(GdDict result)
        {
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
            return result;
        }
        static void Rejected(GdDict result, string reason)
        {
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual(reason, result.GetString("reason"));
        }
        static void NoEffort(GdDict result)
        {
            Assert.AreEqual(0.0, result.GetFloat("eligible_delta"));
            Assert.AreEqual(0.0, result.GetFloat("progress_delta"));
            Assert.AreEqual(0.0, result.GetFloat("stamina_debit"));
        }

        sealed class RecordingPort : IWorkCommitPort
        {
            public int PrepareCalls, CommitCalls, Effects;
            public string PrepareBehavior = "ok", CommitBehavior = "ok";
            public Func<GdDict> Observe;
            public GdDict DuringPrepare, DuringCommit, LastCommand;
            readonly Dictionary<string, GdDict> _commands = new Dictionary<string, GdDict>();
            readonly Dictionary<string, GdDict> _receipts = new Dictionary<string, GdDict>();

            public GdDict Prepare(GdDict command)
            {
                PrepareCalls++;
                DuringPrepare = Observe?.Invoke();
                LastCommand = command.DeepCopy();
                string commandId = command.GetString("command_id"), id = "fake-owner/" + commandId;
                if (PrepareBehavior == "reject") return new GdDict { { "ok", false }, { "reason", "fixture_rejection" } };
                if (PrepareBehavior == "throw") throw new InvalidOperationException("fixture prepare failure");
                _commands[id] = command.DeepCopy();
                return new GdDict { { "ok", true }, { "command_id", PrepareBehavior == "wrong_command" ? "other-command" : commandId },
                    { "transaction_id", PrepareBehavior == "missing_id" ? "" : id } };
            }

            public GdDict Commit(string transactionId)
            {
                CommitCalls++;
                DuringCommit = Observe?.Invoke();
                if (CommitBehavior == "throw_before")
                {
                    CommitBehavior = "ok";
                    throw new InvalidOperationException("fixture threw before effect");
                }
                if (CommitBehavior == "reject") return new GdDict { { "committed", false }, { "transaction_id", transactionId }, { "reason", "fixture_rejection" } };
                if (!_receipts.TryGetValue(transactionId, out GdDict receipt))
                {
                    Assert.IsTrue(_commands.ContainsKey(transactionId), "kernel invented/replaced its prepared identity");
                    Effects++;
                    receipt = new GdDict { { "ok", true }, { "reason", "ok" }, { "committed", true },
                        { "transaction_id", transactionId }, { "commit_id", transactionId },
                        { "command_id", _commands[transactionId].GetString("command_id") },
                        { "result", new GdDict { { "effect_id", "diagnostic-effect" }, { "tag", "original" } } } };
                    _receipts[transactionId] = receipt.DeepCopy();
                }
                string behavior = CommitBehavior;
                CommitBehavior = "ok";
                if (behavior == "throw_after") throw new InvalidOperationException("fixture published then threw");
                if (behavior == "malformed") return new GdDict { { "transaction_id", transactionId } };
                GdDict result = receipt.DeepCopy();
                if (behavior == "wrong_id") result["transaction_id"] = "other-owner-id";
                if (behavior == "unsafe_result") result.GetDictOrEmpty("result")["cycle"] = result.GetDictOrEmpty("result");
                if (behavior == "presentation_failed") result["presentation_failed"] = true;
                return result;
            }
        }

        sealed class Rig
        {
            public readonly RecordingPort Port;
            public readonly WorkTransactionState State;
            public readonly GdDict Ticket, Context;
            public Rig(string mode = "hold", RecordingPort port = null, Action<GdDict> ticketChange = null, Action<GdDict> contextChange = null)
            {
                Port = port ?? new RecordingPort();
                Ticket = WorkKernelTests.Ticket(mode: mode);
                Context = WorkKernelTests.Context(mode);
                ticketChange?.Invoke(Ticket);
                contextChange?.Invoke(Context);
                State = new WorkTransactionState(Port);
                Accepted(State.Begin(Ticket, Context));
                Port.Observe = State.GetSummary;
            }
            public void Ready() => Accepted(State.Advance(Job, Ticket.GetFloat("duration_seconds"), Context));
        }

        [TestCase("hold", true)]
        [TestCase("toggle", false)]
        [TestCase("accessibility", false)]
        public void Eligibility_ExplicitInputModesPermitEligibleWork(string mode, bool held)
        {
            GdDict ticket = Ticket(mode: mode), context = Context(mode, held), a = ticket.DeepCopy(), b = context.DeepCopy();
            GdDict result = Accepted(new WorkEligibility().Evaluate(ticket, context));
            Assert.AreEqual(3, result.GetInt("owner_revision"));
            Assert.AreEqual(4, result.GetInt("target_revision"));
            Same(a, ticket); Same(b, context);
        }

        [TestCase("actor", "actor_changed")]
        [TestCase("ship", "owner_changed")]
        [TestCase("owner_revision", "owner_changed")]
        [TestCase("target", "target_changed")]
        [TestCase("target_revision", "target_changed")]
        [TestCase("definition", "definition_changed")]
        [TestCase("missing_target", "target_missing")]
        [TestCase("invalid_owner", "owner_invalid")]
        [TestCase("anchor", "target_anchor_changed")]
        [TestCase("remote", "left_work_site")]
        [TestCase("sight", "line_of_sight")]
        [TestCase("dead", "dead")]
        [TestCase("damage", "damaged")]
        [TestCase("exhaustion", "exhausted")]
        [TestCase("tool", "tool")]
        [TestCase("skill", "skill")]
        [TestCase("materials", "materials")]
        [TestCase("power", "power")]
        [TestCase("receipt", "payment_receipt")]
        public void Eligibility_RechecksCurrentIdentityAndRequirements(string variant, string reason)
        {
            GdDict ticket = Ticket(), context = Context();
            switch (variant)
            {
                case "actor": context["actor_id"] = "actor-B"; break;
                case "ship": context["ship_id"] = "ship-B"; break;
                case "owner_revision": context["owner_revision"] = 5L; break;
                case "target": context["target_id"] = "other-wall"; break;
                case "target_revision": context["target_revision"] = 5L; break;
                case "definition": context["definition_hash"] = "changed-definition"; break;
                case "missing_target": context["target_exists"] = false; break;
                case "invalid_owner": context["owner_valid"] = false; break;
                case "anchor": context["target_local_anchor"] = new Vec3(1, 0, 0); break;
                case "remote": context["player_local_position"] = new Vec3(1000, 0, 1000); break;
                case "sight": ticket["los_policy"] = "required"; context["line_of_sight"] = false; break;
                case "dead": context["dead"] = true; break;
                case "damage": context["damaged"] = true; break;
                case "exhaustion": context["stamina"] = 0.0; break;
                case "tool": context["tool_class"] = ""; break;
                case "skill": context["skill_level"] = 0L; break;
                case "materials": context.GetDictOrEmpty("inventory")["scrap"] = 0L; break;
                case "power": ticket.GetDictOrEmpty("requirements")["require_power"] = true; context["power_available"] = false; break;
                case "receipt": ticket["material_policy"] = "paid_receipt"; ticket["payment_commit_id"] = "paid-A"; context["payment_commit_id"] = "paid-B"; break;
            }
            GdDict before = ticket.DeepCopy(), current = context.DeepCopy();
            Rejected(new WorkEligibility().Evaluate(ticket, context), reason);
            Same(before, ticket); Same(current, context);
        }

        [TestCase("inclusive", 3.5, true)]
        [TestCase("inclusive", 3.5001, false)]
        [TestCase("exclusive", 3.5, false)]
        [TestCase("exclusive", 3.4999, true)]
        public void Eligibility_PreservesExactRangeComparator(string comparison, double distance, bool allowed)
        {
            GdDict ticket = Ticket(), context = Context();
            ticket["range_comparison"] = comparison;
            context["player_local_position"] = new Vec3(distance, 0, 0);
            GdDict result = new WorkEligibility().Evaluate(ticket, context);
            Assert.AreEqual(allowed, result.GetBool("ok"));
            Assert.AreEqual(allowed ? "ok" : "left_work_site", result.GetString("reason"));
        }

        [Test]
        public void Eligibility_HoldReleasePausesAndNoLosPolicyDoesNotInventSight()
        {
            GdDict ticket = Ticket(), context = Context(held: false);
            Rejected(new WorkEligibility().Evaluate(ticket, context), "hold_released");
            context["input_mode"] = "toggle"; context["line_of_sight"] = false;
            Accepted(new WorkEligibility().Evaluate(ticket, context));
        }

        [Test]
        public void Eligibility_PaidReceiptDoesNotRequireAnotherIngredientSet()
        {
            GdDict ticket = Ticket(), context = Context();
            ticket["material_policy"] = "paid_receipt"; ticket["payment_commit_id"] = "paid-A";
            context["payment_commit_id"] = "paid-A"; context.GetDictOrEmpty("inventory")["scrap"] = 0L;
            Accepted(new WorkEligibility().Evaluate(ticket, context));
        }

        [Test]
        public void Eligibility_ExplicitNoDrainDoesNotInventIndustrialExhaustion()
        {
            GdDict ticket = Ticket(), context = Context();
            Policy(ticket)["stamina_rule"] = "none"; Policy(ticket)["stamina_per_eligible_second"] = 0.0;
            context["stamina"] = 0.0;
            Accepted(new WorkEligibility().Evaluate(ticket, context));
        }

        [Test]
        public void Eligibility_DefinitionCanExplicitlyIgnoreNonterminalDamage()
        {
            GdDict ticket = Ticket(), context = Context();
            Policy(ticket)["interrupt_on_damage"] = false; context["damaged"] = true;
            Accepted(new WorkEligibility().Evaluate(ticket, context));
        }

        [TestCase("schema")]
        [TestCase("revision")]
        [TestCase("range")]
        [TestCase("duration")]
        [TestCase("anchor")]
        [TestCase("effort_hash")]
        [TestCase("missing_clip")]
        [TestCase("missing_effort")]
        [TestCase("missing_command_id")]
        public void Eligibility_MalformedTicketRejectsWithoutMutation(string variant)
        {
            GdDict ticket = Ticket(), context = Context();
            switch (variant)
            {
                case "schema": ticket["schema_version"] = 1.0; break;
                case "revision": ticket["owner_revision"] = 3.0; break;
                case "range": ticket["range_m"] = -1.0; break;
                case "duration": ticket["duration_seconds"] = double.PositiveInfinity; break;
                case "anchor": ticket["local_anchor"] = new Vec3(double.NaN, 0, 0); break;
                case "effort_hash": Policy(ticket)["definition_hash"] = "other-definition"; break;
                case "missing_clip": Policy(ticket).Erase("clip_final_tick"); break;
                case "missing_effort": ticket.Erase("effort_policy"); break;
                case "missing_command_id": ticket.GetDictOrEmpty("completion_command").Erase("command_id"); break;
            }
            GdDict before = ticket.DeepCopy();
            Rejected(new WorkEligibility().Evaluate(ticket, context), "invalid_ticket");
            Same(before, ticket);
        }

        [TestCase("missing_actor")]
        [TestCase("mode")]
        [TestCase("stamina")]
        [TestCase("wound")]
        [TestCase("skill")]
        [TestCase("held")]
        public void Eligibility_MalformedContextRejectsWithoutMutation(string variant)
        {
            GdDict ticket = Ticket(), context = Context();
            switch (variant)
            {
                case "missing_actor": context.Erase("actor_id"); break;
                case "mode": context["input_mode"] = "automatic"; break;
                case "stamina": context["stamina"] = double.NaN; break;
                case "wound": context["wound_speed_mult"] = 0.0; break;
                case "skill": context["skill_level"] = 1.0; break;
                case "held": context["input_held"] = "true"; break;
            }
            GdDict before = context.DeepCopy();
            Rejected(new WorkEligibility().Evaluate(ticket, context), "invalid_context");
            Same(before, context);
        }

        [TestCase("ticket_key", "invalid_ticket")]
        [TestCase("context_key", "invalid_context")]
        [TestCase("ticket_cycle", "invalid_ticket")]
        [TestCase("context_cycle", "invalid_context")]
        public void Eligibility_UnsafeGraphsRejectBeforeCopy(string variant, string reason)
        {
            GdDict ticket = Ticket(), context = Context();
            GdDict graph = variant.StartsWith("ticket", StringComparison.Ordinal) ? ticket : context;
            if (variant.EndsWith("key", StringComparison.Ordinal)) graph["extension"] = new GdDict { { GdArray.Of("mutable-key"), "value" } };
            else graph["extension"] = GdArray.Of(graph);
            Rejected(new WorkEligibility().Evaluate(ticket, context), reason);
            Assert.AreEqual("ship-A", graph.GetString("ship_id"));
        }

        [Test]
        public void BeginStoresOneDefensiveCanonicalRowAndNoPhysicalInputLatch()
        {
            var rig = new Rig();
            GdDict row = Row(rig.State);
            Assert.AreEqual("active", row.GetString("status"));
            Assert.AreEqual(0.0, row.GetFloat("progress_seconds"));
            Assert.IsFalse(row.Has("input_held"));
            Assert.IsFalse(row.Has("ticket"), "progress has one canonical row");
            rig.Ticket["target_id"] = "changed input";
            rig.Ticket.GetDictOrEmpty("completion_command")["effect"] = "changed input";
            row["progress_seconds"] = 9.0;
            Assert.AreEqual("room/wall", Row(rig.State).GetString("target_id"));
            Assert.AreEqual("diagnostic", Row(rig.State).GetDictOrEmpty("completion_command").GetString("effect"));
            Assert.AreEqual(0.0, Row(rig.State).GetFloat("progress_seconds"));
        }

        [Test]
        public void RepeatedBeginDoesNotResetEarnedProgress()
        {
            var rig = new Rig();
            Accepted(rig.State.Advance(Job, 2.0, rig.Context));
            Accepted(rig.State.Begin(rig.Ticket, rig.Context));
            Assert.AreEqual(2.0, Row(rig.State).GetFloat("progress_seconds"));
        }

        [TestCase("target")]
        [TestCase("command")]
        [TestCase("progress")]
        public void ConflictingBeginCannotReplaceBoundTicketOrProgress(string variant)
        {
            var rig = new Rig(); Accepted(rig.State.Advance(Job, 1.0, rig.Context));
            GdDict bad = rig.Ticket.DeepCopy(), before = rig.State.GetSummary();
            if (variant == "target") bad["target_id"] = "replacement";
            if (variant == "command") bad.GetDictOrEmpty("completion_command")["command_id"] = "another-effect";
            if (variant == "progress") bad["progress_seconds"] = 5.0;
            Rejected(rig.State.Begin(bad, rig.Context), "job_conflict");
            Same(before, rig.State.GetSummary());
        }

        [Test]
        public void ConstantEffortReturnsExactProgressAndDebitWithoutMutatingContext()
        {
            var rig = new Rig(ticketChange: t => { Policy(t)["base_speed"] = 2.0; Policy(t)["stamina_per_eligible_second"] = 3.0; });
            GdDict before = rig.Context.DeepCopy();
            GdDict result = Accepted(rig.State.Advance(Job, 1.25, rig.Context));
            Assert.AreEqual(1.25, result.GetFloat("eligible_delta"));
            Assert.AreEqual(2.5, result.GetFloat("progress_delta"));
            Assert.AreEqual(3.75, result.GetFloat("stamina_debit"));
            Assert.AreEqual(2.5, Row(rig.State).GetFloat("progress_seconds"));
            Same(before, rig.Context); Assert.AreEqual(0, rig.Port.Effects);
        }

        [TestCase(100.0, 1.0, 1.0, 2.0)]
        [TestCase(50.0, 0.5, 2.0, 1.35)]
        public void OrdinaryEffortUsesDefinitionSpeedAndExistingStaminaWoundFormula(double stamina, double wound, double speed, double progress)
        {
            var rig = new Rig(ticketChange: t => { Policy(t)["speed_rule"] = "ordinary_work"; Policy(t)["base_speed"] = speed; },
                contextChange: c => { c["stamina"] = stamina; c["wound_speed_mult"] = wound; });
            GdDict result = Accepted(rig.State.Advance(Job, 2.0, rig.Context));
            Assert.AreEqual(progress, result.GetFloat("progress_delta"), 1e-12);
            Assert.AreEqual(4.0, result.GetFloat("stamina_debit"));
        }

        [TestCase(true, 1.0, 3.0)]
        [TestCase(false, 3.0, 9.0)]
        public void FinalTickClippingIsExplicitAndChargesOnlyDeclaredTime(bool clip, double eligible, double debit)
        {
            var rig = new Rig(ticketChange: t => { t["duration_seconds"] = 2.0; Policy(t)["base_speed"] = 2.0;
                Policy(t)["stamina_per_eligible_second"] = 3.0; Policy(t)["clip_final_tick"] = clip; });
            GdDict result = Accepted(rig.State.Advance(Job, 3.0, rig.Context));
            Assert.AreEqual(eligible, result.GetFloat("eligible_delta"));
            Assert.AreEqual(2.0, result.GetFloat("progress_delta"));
            Assert.AreEqual(debit, result.GetFloat("stamina_debit"));
            Assert.AreEqual("ready", Row(rig.State).GetString("status"));
        }

        [Test]
        public void ExplicitNoDrainUsesConstantEligibleTimeWithoutIndustrialDefault()
        {
            var rig = new Rig(ticketChange: t => { Policy(t)["stamina_rule"] = "none";
                Policy(t)["stamina_per_eligible_second"] = 0.0; Policy(t)["base_speed"] = 0.5; }, contextChange: c => c["stamina"] = 0.0);
            GdDict result = Accepted(rig.State.Advance(Job, 2.5, rig.Context));
            Assert.AreEqual(1.25, result.GetFloat("progress_delta"));
            Assert.AreEqual(0.0, result.GetFloat("stamina_debit"));
        }

        [TestCase("speed")]
        [TestCase("progress")]
        [TestCase("sum")]
        [TestCase("debit")]
        public void ExtremeFiniteArithmeticRejectsBeforeAnyStateMutationOrPortCall(string variant)
        {
            var rig = new Rig(ticketChange: t =>
            {
                if (variant == "speed") { Policy(t)["speed_rule"] = "ordinary_work"; Policy(t)["base_speed"] = double.MaxValue; }
                if (variant == "progress" || variant == "sum") { t["duration_seconds"] = double.MaxValue;
                    Policy(t)["stamina_rule"] = "none"; Policy(t)["stamina_per_eligible_second"] = 0.0; }
                if (variant == "progress") Policy(t)["base_speed"] = 2.0;
                if (variant == "debit") Policy(t)["stamina_per_eligible_second"] = double.MaxValue;
            }, contextChange: c => { if (variant == "speed") c["wound_speed_mult"] = 2.0; });
            if (variant == "sum") Accepted(rig.State.Advance(Job, double.MaxValue * 0.75, rig.Context));
            double delta = variant == "progress" ? double.MaxValue : variant == "sum" ? double.MaxValue * 0.5 : 2.0;
            GdDict before = rig.State.GetSummary();
            Rejected(rig.State.Advance(Job, delta, rig.Context), "arithmetic_overflow");
            Same(before, rig.State.GetSummary()); Assert.AreEqual(0, rig.Port.PrepareCalls); Assert.AreEqual(0, rig.Port.CommitCalls);
        }

        [Test]
        public void ReleasedHoldHasZeroEffortAndHeldAdvanceResumesOnlyThatOrdinaryPause()
        {
            var rig = new Rig(); Accepted(rig.State.Advance(Job, 1.0, rig.Context));
            rig.Context["input_held"] = false;
            GdDict released = rig.State.Advance(Job, 5.0, rig.Context);
            Rejected(released, "hold_released"); NoEffort(released);
            Assert.AreEqual(1.0, Row(rig.State).GetFloat("progress_seconds"));
            Assert.AreEqual("paused_hold", Row(rig.State).GetString("status"));
            rig.Context["input_held"] = true;
            Accepted(rig.State.Advance(Job, 2.0, rig.Context));
            Assert.AreEqual(3.0, Row(rig.State).GetFloat("progress_seconds"));
            Assert.AreEqual(0, rig.Port.Effects);
        }

        [TestCase("hold")]
        [TestCase("toggle")]
        [TestCase("accessibility")]
        public void EveryInputModeStillRequiresTheOriginalPhysicalSite(string mode)
        {
            var rig = new Rig(mode); Accepted(rig.State.Advance(Job, 1.0, rig.Context));
            rig.Context["input_held"] = mode == "hold";
            rig.Context["player_local_position"] = new Vec3(1000, 0, 1000);
            GdDict result = rig.State.Advance(Job, 999.0, rig.Context);
            Rejected(result, "left_work_site"); NoEffort(result);
            Assert.AreEqual(1.0, Row(rig.State).GetFloat("progress_seconds"));
            Assert.AreEqual("interrupted", Row(rig.State).GetString("status"));
            Assert.AreEqual(0, rig.Port.PrepareCalls);
        }

        [TestCase("damaged")]
        [TestCase("exhausted")]
        public void ActualInterruptionCauseRequiresExplicitBeginAndRetainsProgress(string reason)
        {
            var rig = new Rig(); Accepted(rig.State.Advance(Job, 1.0, rig.Context));
            if (reason == "damaged") rig.Context["damaged"] = true; else rig.Context["stamina"] = 0.0;
            GdDict result = rig.State.Advance(Job, 100.0, rig.Context);
            Rejected(result, reason); NoEffort(result);
            Assert.AreEqual(reason, Row(rig.State).GetString("pause_reason"));
            GdDict good = Context();
            Rejected(rig.State.Advance(Job, 1.0, good), "resume_required");
            good["resume_requested"] = true;
            Accepted(rig.State.Begin(rig.Ticket, good));
            Assert.AreEqual(1.0, Row(rig.State).GetFloat("progress_seconds"));
        }

        [Test]
        public void ZeroDeltaIsValidAndExactlyNonmutating()
        {
            var rig = new Rig(); GdDict before = rig.State.GetSummary();
            GdDict result = Accepted(rig.State.Advance(Job, 0.0, rig.Context));
            Assert.AreEqual("no_advance", result.GetString("reason")); NoEffort(result);
            Same(before, rig.State.GetSummary());
        }

        [TestCase(-1.0)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void InvalidDeltaCannotAdvanceOrInterruptState(double delta)
        {
            var rig = new Rig(); GdDict before = rig.State.GetSummary();
            Rejected(rig.State.Advance(Job, delta, rig.Context), "invalid_delta");
            Same(before, rig.State.GetSummary()); Assert.AreEqual(0, rig.Port.Effects);
        }

        [TestCase("hold")]
        [TestCase("toggle")]
        [TestCase("accessibility")]
        public void RestoredJobsWaitForResumeRequestedBeginEvenWithHeldOrChangedMode(string mode)
        {
            var rig = new Rig(mode); Accepted(rig.State.Advance(Job, 2.0, rig.Context));
            var restored = new WorkTransactionState(rig.Port);
            Assert.IsTrue(restored.ApplySummary(rig.State.GetSummary()));
            Assert.AreEqual("paused_restore", Row(restored).GetString("status"));
            Assert.IsTrue(Row(restored).GetBool("resume_required")); Assert.IsFalse(Row(restored).Has("input_held"));
            GdDict context = Context(mode == "hold" ? "accessibility" : "hold", true);
            GdDict result = restored.Advance(Job, 100.0, context);
            Rejected(result, "resume_required"); NoEffort(result);
            Rejected(restored.Begin(rig.Ticket, context), "resume_required");
            context["resume_requested"] = true;
            Accepted(restored.Begin(rig.Ticket, context));
            Assert.AreEqual(2.0, Row(restored).GetFloat("progress_seconds"));
            Accepted(restored.Advance(Job, 1.0, context));
            Assert.AreEqual(3.0, Row(restored).GetFloat("progress_seconds")); Assert.AreEqual(0, rig.Port.Effects);
        }

        [TestCase("owner", "owner_changed")]
        [TestCase("target", "target_changed")]
        public void RestoredBeginCannotReacquireAnotherOwnerOrTarget(string variant, string reason)
        {
            var rig = new Rig(); Accepted(rig.State.Advance(Job, 2.0, rig.Context));
            var restored = new WorkTransactionState(rig.Port); Assert.IsTrue(restored.ApplySummary(rig.State.GetSummary()));
            GdDict context = Context(resume: true), before = restored.GetSummary();
            context[variant == "owner" ? "ship_id" : "target_id"] = "other";
            Rejected(restored.Begin(rig.Ticket, context), reason);
            Same(before, restored.GetSummary()); Assert.AreEqual(0, rig.Port.PrepareCalls);
        }

        [TestCase("schema")]
        [TestCase("revision")]
        [TestCase("target_revision")]
        [TestCase("identity")]
        [TestCase("progress")]
        [TestCase("status")]
        public void InvalidWholeImportLeavesEveryPriorJobUnchanged(string variant)
        {
            var rig = new Rig(); Accepted(rig.State.Advance(Job, 1.0, rig.Context));
            GdDict before = rig.State.GetSummary(), bad = before.DeepCopy(), row = bad.GetDictOrEmpty("jobs").GetDictOrEmpty(Job);
            switch (variant)
            {
                case "schema": bad["schema_version"] = 1.0; break;
                case "revision": bad["revision"] = 100.0; break;
                case "target_revision": row["target_revision"] = -1L; break;
                case "identity": row["job_id"] = "different-key"; break;
                case "progress": row["progress_seconds"] = 11.0; break;
                case "status": row["status"] = "invented"; break;
            }
            Assert.IsFalse(rig.State.ApplySummary(bad)); Same(before, rig.State.GetSummary());
        }

        [Test]
        public void ImportAndReturnedNestedRowsHaveNoMutableAliases()
        {
            var rig = new Rig(); GdDict snapshot = rig.State.GetSummary();
            var restored = new WorkTransactionState(rig.Port); Assert.IsTrue(restored.ApplySummary(snapshot));
            GdDict supplied = snapshot.GetDictOrEmpty("jobs").GetDictOrEmpty(Job).GetDictOrEmpty("completion_command");
            ((GdDict)supplied.GetDictOrEmpty("extension").GetArrayOrEmpty("items")[0])["tag"] = "changed input";
            GdDict returned = Row(restored);
            returned.GetDictOrEmpty("requirements").GetDictOrEmpty("materials")["scrap"] = 999L;
            Assert.AreEqual(1, Row(restored).GetDictOrEmpty("requirements").GetDictOrEmpty("materials").GetInt("scrap"));
            GdDict command = Row(restored).GetDictOrEmpty("completion_command");
            Assert.AreEqual("original", ((GdDict)command.GetDictOrEmpty("extension").GetArrayOrEmpty("items")[0]).GetString("tag"));
        }

        [Test]
        public void CommitPortUnboundCannotPublish()
        {
            var state = new WorkTransactionState(null); GdDict ticket = Ticket(), context = Context();
            Accepted(state.Begin(ticket, context)); Accepted(state.Advance(Job, 10.0, context));
            GdDict before = state.GetSummary();
            Rejected(state.Commit(Job, context), "commit_port_unbound");
            Same(before, state.GetSummary()); Assert.AreEqual("", Row(state).GetString("prepared_transaction_id"));
        }

        [Test]
        public void RejectedPortKeepsPriorCandidate()
        {
            var rig = new Rig(); rig.Ready(); rig.Port.PrepareBehavior = "reject";
            GdDict before = rig.State.GetSummary();
            Rejected(rig.State.Commit(Job, rig.Context), "port_prepare_rejected");
            Same(before, rig.State.GetSummary()); Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(0, rig.Port.CommitCalls);
            Assert.AreEqual(0, rig.Port.Effects);
            rig.Port.PrepareBehavior = "ok";
            Assert.IsTrue(Accepted(rig.State.Commit(Job, rig.Context)).GetBool("committed"));
            Assert.AreEqual(1, rig.Port.Effects);
        }

        [Test]
        public void FirstCommitRechecksEligibilityImmediatelyBeforeCallingThePort()
        {
            var rig = new Rig(); rig.Ready(); rig.Context["tool_class"] = "";
            Rejected(rig.State.Commit(Job, rig.Context), "tool");
            Assert.AreEqual(0, rig.Port.PrepareCalls); Assert.AreEqual(0, rig.Port.CommitCalls); Assert.AreEqual(0, rig.Port.Effects);
            Assert.AreEqual(10.0, Row(rig.State).GetFloat("progress_seconds"));
        }

        [Test]
        public void SuccessfulPrepareBindsItsIdentityBeforeAnyOwnerCommitAndReplayIsDefensive()
        {
            var rig = new Rig(); rig.Ready(); GdDict before = rig.State.GetSummary();
            GdDict result = Accepted(rig.State.Commit(Job, rig.Context));
            Assert.IsTrue(result.GetBool("committed"));
            Same(before, rig.Port.DuringPrepare);
            Assert.AreEqual("fake-owner/complete/job-A", rig.Port.DuringCommit.GetDictOrEmpty("jobs").GetDictOrEmpty(Job).GetString("prepared_transaction_id"));
            GdDict committed = rig.State.GetSummary(); result.GetDictOrEmpty("result")["tag"] = "changed output";
            GdDict replay = Accepted(rig.State.Commit(Job, rig.Context));
            Assert.AreEqual("original", replay.GetDictOrEmpty("result").GetString("tag"));
            Same(committed, rig.State.GetSummary());
            Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(1, rig.Port.CommitCalls); Assert.AreEqual(1, rig.Port.Effects);
            var restored = new WorkTransactionState(rig.Port); Assert.IsTrue(restored.ApplySummary(committed));
            Assert.IsTrue(Accepted(restored.Commit(Job, rig.Context)).GetBool("committed")); Assert.AreEqual(1, rig.Port.Effects);
        }

        [TestCase("missing_id")]
        [TestCase("wrong_command")]
        public void MalformedSuccessfulPreparationCannotCallCommitOrChangeState(string behavior)
        {
            var rig = new Rig(); rig.Ready(); rig.Port.PrepareBehavior = behavior;
            GdDict before = rig.State.GetSummary();
            Rejected(rig.State.Commit(Job, rig.Context), "invalid_port_preparation");
            Same(before, rig.State.GetSummary()); Assert.AreEqual(0, rig.Port.CommitCalls); Assert.AreEqual(0, rig.Port.Effects);
        }

        [TestCase("throw_before", 0)]
        [TestCase("throw_after", 1)]
        public void UnknownCommitRetainsExactIdAndEligibleRetryNeverPreparesAnotherEffect(string behavior, int effectsBeforeRetry)
        {
            var rig = new Rig(); rig.Ready(); rig.Port.CommitBehavior = behavior;
            Rejected(rig.State.Commit(Job, rig.Context), "commit_unknown");
            Assert.AreEqual("pending_commit", Row(rig.State).GetString("status"));
            Assert.AreEqual("fake-owner/complete/job-A", Row(rig.State).GetString("prepared_transaction_id"));
            Assert.AreEqual(effectsBeforeRetry, rig.Port.Effects);
            GdDict retried = Accepted(rig.State.Commit(Job, rig.Context)); Assert.IsTrue(retried.GetBool("committed"));
            Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(2, rig.Port.CommitCalls); Assert.AreEqual(1, rig.Port.Effects);
        }

        [TestCase("malformed")]
        [TestCase("wrong_id")]
        [TestCase("unsafe_result")]
        public void UnconfirmedCommitResponseRetainsPendingIdForSameOwnerReceiptRetry(string behavior)
        {
            var rig = new Rig(); rig.Ready(); rig.Port.CommitBehavior = behavior;
            Rejected(rig.State.Commit(Job, rig.Context), "commit_unknown");
            Assert.AreEqual("fake-owner/complete/job-A", Row(rig.State).GetString("prepared_transaction_id"));
            Assert.IsTrue(Accepted(rig.State.Commit(Job, rig.Context)).GetBool("committed"));
            Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(2, rig.Port.CommitCalls); Assert.AreEqual(1, rig.Port.Effects);
        }

        [Test]
        public void InvalidPendingRetryMakesNoPortCallAndRetainsTheOriginalId()
        {
            var rig = new Rig(); rig.Ready(); rig.Port.CommitBehavior = "throw_after";
            Rejected(rig.State.Commit(Job, rig.Context), "commit_unknown");
            rig.Context["player_local_position"] = new Vec3(1000, 0, 1000);
            Rejected(rig.State.Commit(Job, rig.Context), "left_work_site");
            Assert.AreEqual("fake-owner/complete/job-A", Row(rig.State).GetString("prepared_transaction_id"));
            Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(1, rig.Port.CommitCalls); Assert.AreEqual(1, rig.Port.Effects);
        }

        [Test]
        public void PendingBeginCannotReplaceUnresolvedJobWithAnotherCommandOrJob()
        {
            var rig = new Rig(); rig.Ready(); rig.Port.CommitBehavior = "throw_before";
            Rejected(rig.State.Commit(Job, rig.Context), "commit_unknown");
            GdDict before = rig.State.GetSummary(), other = Ticket("job-B");
            Rejected(rig.State.Begin(other, rig.Context), "commit_pending");
            Same(before, rig.State.GetSummary()); Assert.AreEqual(1, rig.Port.PrepareCalls);
        }

        [Test]
        public void PendingExportImportRequiresExplicitResumeThenReconcilesTheSameId()
        {
            var rig = new Rig(); rig.Ready(); rig.Port.CommitBehavior = "throw_after";
            Rejected(rig.State.Commit(Job, rig.Context), "commit_unknown");
            var restored = new WorkTransactionState(rig.Port); Assert.IsTrue(restored.ApplySummary(rig.State.GetSummary()));
            Rejected(restored.Commit(Job, rig.Context), "resume_required");
            GdDict resume = Context(resume: true); Accepted(restored.Begin(rig.Ticket, resume));
            Assert.IsTrue(Accepted(restored.Commit(Job, resume)).GetBool("committed"));
            Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(2, rig.Port.CommitCalls); Assert.AreEqual(1, rig.Port.Effects);
        }

        [TestCase("omit")]
        [TestCase("id")]
        [TestCase("command")]
        public void HigherRevisionImportCannotEraseOrReplaceUnresolvedPreparedIdentity(string variant)
        {
            var rig = new Rig(); rig.Ready(); rig.Port.CommitBehavior = "throw_before";
            Rejected(rig.State.Commit(Job, rig.Context), "commit_unknown");
            GdDict before = rig.State.GetSummary(), bad = before.DeepCopy(); bad["revision"] = before.GetInt("revision") + 1;
            GdDict rows = bad.GetDictOrEmpty("jobs");
            if (variant == "omit") rows.Erase(Job);
            if (variant == "id") rows.GetDictOrEmpty(Job)["prepared_transaction_id"] = "other-id";
            if (variant == "command") rows.GetDictOrEmpty(Job).GetDictOrEmpty("completion_command")["command_id"] = "other-command";
            Assert.IsFalse(rig.State.ApplySummary(bad)); Same(before, rig.State.GetSummary());
        }

        [Test]
        public void PostpublicationPresentationFailureRemainsCommittedAndCannotReplayEffects()
        {
            var rig = new Rig(); rig.Ready(); rig.Port.CommitBehavior = "presentation_failed";
            GdDict first = Accepted(rig.State.Commit(Job, rig.Context));
            Assert.IsTrue(first.GetBool("committed")); Assert.IsTrue(first.GetBool("presentation_failed"));
            GdDict replay = Accepted(rig.State.Commit(Job, rig.Context)); Assert.IsTrue(replay.GetBool("committed"));
            Assert.AreEqual(1, rig.Port.Effects); Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(1, rig.Port.CommitCalls);
        }

        [Test]
        public void HigherRevisionImportCannotForgetCommittedJob()
        {
            var rig = new Rig(); rig.Ready(); Accepted(rig.State.Commit(Job, rig.Context));
            GdDict before = rig.State.GetSummary(), bad = before.DeepCopy(); bad["revision"] = before.GetInt("revision") + 1;
            bad.GetDictOrEmpty("jobs").Erase(Job);
            Assert.IsFalse(rig.State.ApplySummary(bad)); Same(before, rig.State.GetSummary());
            Assert.IsTrue(Accepted(rig.State.Commit(Job, rig.Context)).GetBool("committed")); Assert.AreEqual(1, rig.Port.Effects);
        }

        [Test]
        public void CannotRewriteCommittedResult()
        {
            var rig = new Rig(); rig.Ready(); Accepted(rig.State.Commit(Job, rig.Context));
            GdDict before = rig.State.GetSummary(), bad = before.DeepCopy(); bad["revision"] = before.GetInt("revision") + 1;
            bad.GetDictOrEmpty("jobs").GetDictOrEmpty(Job).GetDictOrEmpty("committed_result").GetDictOrEmpty("result")["tag"] = "forged";
            Assert.IsFalse(rig.State.ApplySummary(bad)); Same(before, rig.State.GetSummary());
            Assert.AreEqual("original", Accepted(rig.State.Commit(Job, rig.Context)).GetDictOrEmpty("result").GetString("tag"));
            Assert.AreEqual(1, rig.Port.Effects);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void FirstCommitReservesBindingAndConfirmationRevisionsBeforeAnyPortCall(int remainingSpace)
        {
            var rig = new Rig(); rig.Ready(); GdDict imported = rig.State.GetSummary();
            imported["revision"] = long.MaxValue - remainingSpace - 1;
            var state = new WorkTransactionState(rig.Port); Assert.IsTrue(state.ApplySummary(imported));
            Accepted(state.Begin(rig.Ticket, Context(resume: true)));
            GdDict before = state.GetSummary(); Assert.AreEqual(long.MaxValue - remainingSpace, before.GetInt("revision"));
            Rejected(state.Commit(Job, rig.Context), "revision_overflow");
            Same(before, state.GetSummary()); Assert.AreEqual(0, rig.Port.PrepareCalls); Assert.AreEqual(0, rig.Port.CommitCalls); Assert.AreEqual(0, rig.Port.Effects);
        }

        [Test]
        public void PendingRetryAtMaximumRevisionMakesNoPortCallOrStateMutation()
        {
            var rig = new Rig(); rig.Ready(); rig.Port.CommitBehavior = "throw_before";
            Rejected(rig.State.Commit(Job, rig.Context), "commit_unknown");
            GdDict imported = rig.State.GetSummary(); imported["revision"] = long.MaxValue - 1;
            var state = new WorkTransactionState(rig.Port); Assert.IsTrue(state.ApplySummary(imported));
            Accepted(state.Begin(rig.Ticket, Context(resume: true))); GdDict before = state.GetSummary();
            Assert.AreEqual(long.MaxValue, before.GetInt("revision"));
            Rejected(state.Commit(Job, rig.Context), "revision_overflow");
            Same(before, state.GetSummary()); Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(1, rig.Port.CommitCalls); Assert.AreEqual(0, rig.Port.Effects);
        }

        [Test]
        public void ThrownPreparePreservesCandidateAndNeverCallsCommit()
        {
            var rig = new Rig(); rig.Ready(); rig.Port.PrepareBehavior = "throw";
            GdDict before = rig.State.GetSummary();
            Rejected(rig.State.Commit(Job, rig.Context), "port_prepare_failed");
            Same(before, rig.State.GetSummary()); Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(0, rig.Port.CommitCalls);
            Assert.AreEqual(0, rig.Port.Effects);
            rig.Port.PrepareBehavior = "ok";
            Assert.IsTrue(Accepted(rig.State.Commit(Job, rig.Context)).GetBool("committed"));
            Assert.AreEqual(2, rig.Port.PrepareCalls); Assert.AreEqual(1, rig.Port.CommitCalls); Assert.AreEqual(1, rig.Port.Effects);
        }

        [Test]
        public void ExplicitCommitRejectionRetainsPreparedIdentityForRetryWithoutNewPrepare()
        {
            var rig = new Rig(); rig.Ready(); rig.Port.CommitBehavior = "reject";
            Rejected(rig.State.Commit(Job, rig.Context), "port_commit_rejected");
            Assert.AreEqual("pending_commit", Row(rig.State).GetString("status"));
            Assert.AreEqual("fake-owner/complete/job-A", Row(rig.State).GetString("prepared_transaction_id"));
            Assert.AreEqual(10.0, Row(rig.State).GetFloat("progress_seconds")); Assert.AreEqual(0, rig.Port.Effects);
            rig.Port.CommitBehavior = "ok";
            Assert.IsTrue(Accepted(rig.State.Commit(Job, rig.Context)).GetBool("committed"));
            Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(2, rig.Port.CommitCalls); Assert.AreEqual(1, rig.Port.Effects);
        }

        [Test]
        public void ReentrantMutationDuringPrepareAndCommitCannotReplaceCandidateOrPublishAgain()
        {
            var rig = new Rig(); rig.Ready(); int callbacks = 0;
            rig.Port.Observe = () =>
            {
                GdDict before = rig.State.GetSummary();
                Rejected(rig.State.Begin(Ticket("job-B"), rig.Context), "publication_in_progress");
                Rejected(rig.State.Advance(Job, 1.0, rig.Context), "publication_in_progress");
                Rejected(rig.State.Commit(Job, rig.Context), "publication_in_progress");
                Rejected(rig.State.Interrupt(Job, "fixture_interrupt"), "publication_in_progress");
                Assert.IsFalse(rig.State.ApplySummary(before)); Same(before, rig.State.GetSummary()); callbacks++;
                return before;
            };
            Assert.IsTrue(Accepted(rig.State.Commit(Job, rig.Context)).GetBool("committed"));
            Assert.AreEqual(2, callbacks); Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(1, rig.Port.CommitCalls);
            Assert.AreEqual(1, rig.Port.Effects);
            Assert.IsTrue(Accepted(rig.State.Commit(Job, rig.Context)).GetBool("committed")); Assert.AreEqual(1, rig.Port.Effects);
        }

        [TestCase("range", "left_work_site")]
        [TestCase("tool", "tool")]
        [TestCase("revision", "target_changed")]
        public void PrepareContextChangesRequireRevalidationBeforeOwnerCommit(string variant, string reason)
        {
            var rig = new Rig(); rig.Ready(); int callbacks = 0;
            rig.Port.Observe = () =>
            {
                if (++callbacks == 1)
                {
                    if (variant == "range") rig.Context["player_local_position"] = new Vec3(1000, 0, 1000);
                    if (variant == "tool") rig.Context["tool_class"] = "";
                    if (variant == "revision") rig.Context["target_revision"] = 5L;
                }
                return rig.State.GetSummary();
            };
            GdDict refused = rig.State.Commit(Job, rig.Context);
            Rejected(refused, reason); NoEffort(refused);
            Assert.AreEqual("fake-owner/complete/job-A", Row(rig.State).GetString("prepared_transaction_id"));
            Assert.AreEqual(10.0, Row(rig.State).GetFloat("progress_seconds"));
            Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(0, rig.Port.CommitCalls); Assert.AreEqual(0, rig.Port.Effects);
            GdDict eligible = Context(resume: true);
            Accepted(rig.State.Begin(rig.Ticket, eligible));
            GdDict committed = Accepted(rig.State.Commit(Job, eligible));
            Assert.IsTrue(committed.GetBool("committed"));
            Assert.AreEqual("fake-owner/complete/job-A", committed.GetString("transaction_id"));
            Assert.AreEqual(1, rig.Port.PrepareCalls); Assert.AreEqual(1, rig.Port.CommitCalls); Assert.AreEqual(1, rig.Port.Effects);
        }
    }
}
