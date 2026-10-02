using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    /// <summary>Explicit diagnostic equipment fixtures. No earned wrench/source or normal-mode activation claim.</summary>
    public class LiveComponentJourneyTests : WorkSessionTestBase
    {
        readonly List<RunSession> _diagnosticSessions = new List<RunSession>();

        [TearDown]
        public void DisposeDiagnosticSessions()
        {
            foreach (RunSession session in _diagnosticSessions) session.Dispose();
            _diagnosticSessions.Clear();
        }

        static MethodInfo Api(string name, int parameterCount)
        {
            MethodInfo method = typeof(RunSession).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .SingleOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == parameterCount);
            Assert.IsNotNull(method, "Admitted live equipment owner requires RunSession." + name +
                " with " + parameterCount + " parameters; a detached transfer service cannot satisfy the live journey.");
            return method;
        }

        static object Invoke(RunSession session, string name, params object[] arguments)
        {
            try { return Api(name, arguments.Length).Invoke(session, arguments); }
            catch (TargetInvocationException failure) { throw failure.InnerException ?? failure; }
        }

        static GdDict Capture(RunSession session) => (GdDict)Invoke(session, "CaptureComponentDomain");
        static GdDict Registry(GdDict summary) => summary.GetDictOrEmpty("registry").GetDictOrEmpty("instances");
        static GdDict Request(RunSession session, string name, params object[] args) => (GdDict)Invoke(session, name, args);
        static GdArray Targets(RunSession session, string instanceId) => (GdArray)Invoke(session, "ListInstallTargets", instanceId);
        static GdDict Work(RunSession session) => (GdDict)Invoke(session, "GetComponentWorkState");
        static void Equal(object expected, object actual, string message)
            => Assert.IsTrue(V.VariantEquals(expected, actual), message);

        SessionHarness.Rig DiagnosticBoot(IStorage storage = null)
        {
            FieldInfo optIn = typeof(RunSessionDeps).GetField("EnableComponentIntegration");
            Assert.IsNotNull(optIn, "Known-equipment activation must have an explicit default-off diagnostic opt-in.");
            RunSessionDeps deps = SessionHarness.GoldenDeps(out SessionHarness.Rig rig);
            SessionHarness.OverlayGamePlayability(deps);
            deps.SettingsState = new SettingsState();
            if (storage != null) deps.Storage = storage;
            optIn.SetValue(deps, true);
            rig.Session = RunSession.Create(deps);
            _diagnosticSessions.Add(rig.Session);
            Assert.IsTrue(rig.Session.PlayableStarted, rig.Session.LastFailureReason);
            rig.Session.ThreatManager.Threats.Clear();
            return rig;
        }

        static GdDict Mounted(RunSession session)
        {
            GdDict summary = Capture(session);
            GdDict row = Registry(summary).Values.OfType<GdDict>().FirstOrDefault(instance =>
                summary.GetDictOrEmpty("holders").GetDictOrEmpty(instance.GetString("holder")).GetString("kind") == "slot");
            Assert.IsNotNull(row, "Actual golden generation must register its mounted component placements.");
            return row.DeepCopy();
        }

        static GdDict LinkedMounted(RunSession session)
        {
            GdDict summary = Capture(session);
            GdDict row = Registry(summary).Values.OfType<GdDict>().FirstOrDefault(instance =>
                summary.GetDictOrEmpty("holders").GetDictOrEmpty(instance.GetString("holder")).Has("machinery_id"));
            Assert.IsNotNull(row, "Actual golden generation must retain its established physical machinery links.");
            return row.DeepCopy();
        }

        static Vec3 Anchor(RunSession session, GdDict row)
        {
            GdDict target = Targets(session, row.GetString("instance_id")).OfType<GdDict>()
                .FirstOrDefault(candidate => candidate.GetString("holder_id") == row.GetString("holder"));
            Assert.IsNotNull(target, "Installed source must retain an actual physical target row, including occupied targets.");
            object value = target.Get("world_position");
            if (value is Vec3 position) return position;
            Assert.IsInstanceOf<GdArray>(value, "Physical target must carry its actual world anchor.");
            return Vec3.FromArray((GdArray)value);
        }

        static void ProvisionWrench(RunSession session) => session.InventoryState.Items["wrench"] = 1L;

        static void SetKnownCondition(RunSession session, string instanceId, double condition, double? machineryHealth = null)
        {
            GdDict summary = Capture(session);
            GdDict instance = Registry(summary).GetDictOrEmpty(instanceId);
            Assert.IsFalse(instance.IsEmpty);
            instance["condition_state"] = "known";
            instance["condition"] = condition;
            if (machineryHealth.HasValue)
            {
                string machine = summary.GetDictOrEmpty("holders").GetDictOrEmpty(instance.GetString("holder")).GetString("machinery_id");
                Assert.IsNotEmpty(machine);
                summary.GetDictOrEmpty("machinery").GetDictOrEmpty(machine)["health"] = machineryHealth.Value;
            }
            Assert.IsTrue((bool)Invoke(session, "RestoreComponentDomain", summary),
                "Explicit exact-condition diagnostic snapshot must bind without inventing equipment or health.");
        }

        static GdDict CompletePending(SessionHarness.Rig rig, string instanceId, string expectedHolder)
        {
            rig.Session.VitalsState.Stamina = rig.Session.VitalsState.MaxStamina;
            for (int tick = 0; tick < 140; tick++)
            {
                rig.Session.StageWorkAction(0.1);
                GdDict row = Registry(Capture(rig.Session)).GetDictOrEmpty(instanceId);
                if (row.GetString("holder") == expectedHolder) return row;
            }
            Assert.Fail("Existing eligible held component work must finish through RunSession.StageWorkAction, not an immediate UI mutation.");
            return new GdDict();
        }

        static void AssertPending(GdDict result)
        {
            Assert.IsTrue(result.GetBool("ok"), result.GetString("reason"));
            Assert.IsFalse(result.GetBool("committed"), "Removal/install request must enter work before the component effect.");
            Assert.AreEqual("started", result.GetString("reason"));
            Assert.IsNotEmpty(result.GetString("job_id"));
        }

        [TestCase("ListComponentInstances", 1)]
        [TestCase("ListInstallTargets", 1)]
        [TestCase("RequestComponentRemoval", 2)]
        [TestCase("RequestComponentTransfer", 2)]
        [TestCase("RequestComponentInstall", 3)]
        [TestCase("CaptureComponentDomain", 0)]
        [TestCase("ValidateComponentDomainRestore", 2)]
        [TestCase("RestoreComponentDomain", 1)]
        [TestCase("GetComponentWorkState", 0)]
        [TestCase("GetComponentHolderIds", 0)]
        public void FrozenSessionSurface_ExposesActualOwnerBoundary(string method, int parameters) => Api(method, parameters);

        [Test]
        public void FrozenSessionSurface_ExposesDefensivePublicationNotification()
        {
            EventInfo changed = typeof(RunSession).GetEvent("ComponentDomainChanged");
            Assert.IsNotNull(changed, "UI must refresh from the committed live owner rather than mutate a panel bag.");
            Assert.AreEqual(typeof(Action<GdDict>), changed.EventHandlerType);
        }

        [Test]
        public void NormalSession_ComponentCommandsAreExplicitlyInactive()
        {
            SessionHarness.Rig rig = Boot();
            GdDict before = rig.Session.InventoryState.GetSummary();
            GdDict result = Request(rig.Session, "RequestComponentTransfer", "named-instance", "player:player_local");
            Assert.IsFalse(result.GetBool("ok"));
            Assert.AreEqual("component_integration_inactive", result.GetString("reason"));
            Equal(before, rig.Session.InventoryState.GetSummary(), "Default-off component command cannot alter normal stack inventory.");
        }

        [Test]
        public void DiagnosticBoot_RegistersActualPlacementsWithQualifiedIdentityAndExactEvidence()
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            GdDict domain = Capture(rig.Session);
            Assert.Greater(Registry(domain).Count, 0, "Diagnostic activation must bind actual mounted equipment, not only an empty registry.");
            foreach (GdDict row in Registry(domain).Values.OfType<GdDict>())
            {
                GdDict holder = domain.GetDictOrEmpty("holders").GetDictOrEmpty(row.GetString("holder"));
                Assert.IsFalse(holder.IsEmpty);
                Assert.IsTrue(row.GetString("instance_id").StartsWith(holder.GetString("owner_id") + ":", StringComparison.Ordinal));
                Assert.AreEqual("known", row.GetString("condition_state"));
                Assert.That(row.GetFloat("condition"), Is.InRange(0.0, 1.0));
                Assert.Greater(row.GetFloat("mass"), 0);
                Assert.IsTrue(row.Has("origin") && row.Has("provenance"));
            }
        }

        [Test]
        public void CaptureAndList_AreDefensiveWithoutLiveAliases()
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            GdDict before = Capture(rig.Session), forged = Capture(rig.Session);
            GdDict row = Registry(forged).Values.OfType<GdDict>().First();
            row["condition"] = 0.123;
            row["holder"] = "forged";
            forged["receipts"] = new GdDict { { "forged", true } };
            GdArray listed = (GdArray)Invoke(rig.Session, "ListComponentInstances", Mounted(rig.Session).GetString("holder"));
            Assert.Greater(listed.Count, 0);
            ((GdDict)listed[0])["holder"] = "also-forged";
            Equal(before, Capture(rig.Session), "Mutating API snapshots cannot change the sole owner.");
        }

        [TestCase("known_null")]
        [TestCase("unknown_null")]
        [TestCase("unknown_numeric")]
        [TestCase("known_nan")]
        public void UnsafeConditionRestore_RefusesBeforeAnyLiveMutation(string fault)
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            GdDict before = Capture(rig.Session), bad = before.DeepCopy();
            GdDict row = Registry(bad).Values.OfType<GdDict>().First();
            row["condition_state"] = fault.StartsWith("unknown", StringComparison.Ordinal) ? "unknown" : "known";
            row["condition"] = fault == "unknown_numeric" ? (object)0.0 : fault == "known_nan" ? (object)double.NaN : null;
            object[] args = { bad, null };
            Assert.IsFalse((bool)Api("ValidateComponentDomainRestore", 2).Invoke(rig.Session, args));
            Assert.IsNotEmpty((string)args[1], "Unsafe conversion must report a concrete refusal.");
            Assert.IsFalse((bool)Invoke(rig.Session, "RestoreComponentDomain", bad));
            Equal(before, Capture(rig.Session), "Refused legacy/invalid condition cannot change registry, holders, jobs, receipts or machinery.");
        }

        [Test]
        public void SameFormDifferentCondition_RestoreKeepsBothExactInstances()
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            GdDict snapshot = Capture(rig.Session);
            GdDict[] pair = Registry(snapshot).Values.OfType<GdDict>().GroupBy(row => row.GetString("item_form"))
                .FirstOrDefault(group => group.Count() >= 2)?.Take(2).ToArray();
            Assert.IsNotNull(pair, "Actual golden diagnostic generation must retain multiple same-form placements.");
            pair[0]["condition"] = 0.23;
            pair[1]["condition"] = 0.81;
            Assert.IsTrue((bool)Invoke(rig.Session, "RestoreComponentDomain", snapshot));
            GdDict restored = Capture(rig.Session);
            Equal(pair[0], Registry(restored).GetDictOrEmpty(pair[0].GetString("instance_id")), "Chosen0.23 remains its own identity.");
            Equal(pair[1], Registry(restored).GetDictOrEmpty(pair[1].GetString("instance_id")), "Other0.81 must not become the chosen instance.");
        }

        [Test]
        public void TimedRemoval_ConservesConditionMassHealthAndNeverGrantsAnonymousForm()
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            GdDict row = LinkedMounted(rig.Session);
            SetKnownCondition(rig.Session, row.GetString("instance_id"), 0.23, 0.8);
            row = Registry(Capture(rig.Session)).GetDictOrEmpty(row.GetString("instance_id"));
            rig.Scene.PlayerPosition = Anchor(rig.Session, row);
            ProvisionWrench(rig.Session);
            rig.Session.BeginWorkHold();
            GdDict before = Capture(rig.Session);
            double weight = rig.Session.InventoryState.GetTotalWeight();
            long anonymous = rig.Session.InventoryState.GetQuantity(row.GetString("item_form"));
            AssertPending(Request(rig.Session, "RequestComponentRemoval", row.GetString("instance_id"), null));
            Equal(Registry(before), Registry(Capture(rig.Session)), "Starting work cannot detach equipment before elapsed eligible work.");
            GdDict removed = CompletePending(rig, row.GetString("instance_id"), "player:player_local");
            Assert.AreEqual(0.23, removed.GetFloat("condition"));
            Assert.AreEqual(row.GetFloat("mass"), removed.GetFloat("mass"));
            Assert.AreEqual(weight + row.GetFloat("mass"), rig.Session.InventoryState.GetTotalWeight(), 0.000001);
            Assert.AreEqual(anonymous, rig.Session.InventoryState.GetQuantity(row.GetString("item_form")), "Unique removal cannot mint aggregate form quantity.");
            Equal(before.Get("machinery"), Capture(rig.Session).Get("machinery"), "Detaching linked equipment cannot damage saved machinery health.");
            Assert.AreEqual(before.GetDictOrEmpty("receipts").Count + 1, Capture(rig.Session).GetDictOrEmpty("receipts").Count);
        }

        [TestCase("missing_tool")]
        [TestCase("out_of_range")]
        public void RemovalRequest_InvalidLiveGatePreservesAllEffects(string gate)
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            GdDict row = Mounted(rig.Session);
            rig.Scene.PlayerPosition = Anchor(rig.Session, row);
            if (gate == "out_of_range")
            {
                ProvisionWrench(rig.Session);
                rig.Scene.PlayerPosition += new Vec3(1000, 0, 1000);
            }
            else
            {
                rig.Session.InventoryState.Items.Erase("wrench");
                rig.Session.InventoryState.Items.Erase("tool_wrench");
            }
            GdDict before = Capture(rig.Session);
            GdDict result = Request(rig.Session, "RequestComponentRemoval", row.GetString("instance_id"), null);
            Assert.IsFalse(result.GetBool("ok"), "Named component command must enforce " + gate + ".");
            Assert.IsFalse(result.GetBool("committed"));
            Equal(before, Capture(rig.Session), "Blocked component request leaves the complete captured owner unchanged.");
        }

        [Test]
        public void WorkToolLoss_StopsCompletionWithoutComponentOrRewardEffect()
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            GdDict row = Mounted(rig.Session);
            rig.Scene.PlayerPosition = Anchor(rig.Session, row);
            ProvisionWrench(rig.Session);
            rig.Session.BeginWorkHold();
            GdDict before = Capture(rig.Session);
            int events = TrainingCount(rig.Session);
            AssertPending(Request(rig.Session, "RequestComponentRemoval", row.GetString("instance_id"), null));
            rig.Session.InventoryState.Items.Erase("wrench");
            for (int tick = 0; tick < 100; tick++) rig.Session.StageWorkAction(0.1);
            GdDict after = Capture(rig.Session);
            Equal(Registry(before), Registry(after), "Losing required tool cannot detach the exact instance.");
            Equal(before.Get("machinery"), after.Get("machinery"), "Failed work cannot alter machinery health.");
            Equal(before.Get("receipts"), after.Get("receipts"), "Failed work cannot publish completion receipt.");
            Assert.AreEqual(events, TrainingCount(rig.Session), "Failed component work cannot deliver XP/training.");
        }

        [Test]
        public void AnonymousFormInstall_CannotSelectOrReplaceUniqueEquipment()
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            ProvisionWrench(rig.Session);
            rig.Session.InventoryState.Items["reactor_console"] = 2L;
            GdDict target = Targets(rig.Session, "").OfType<GdDict>().First();
            rig.Scene.PlayerPosition = target.Get("world_position") is Vec3 anchor ? anchor : Vec3.FromArray(target.GetArrayOrEmpty("world_position"));
            GdDict before = Capture(rig.Session);
            GdDict result = Request(rig.Session, "RequestComponentInstall", "reactor_console", target.GetString("ship_id"), target.GetString("slot_id"));
            Assert.IsFalse(result.GetBool("ok"));
            Equal(before, Capture(rig.Session), "Anonymous same-form counts cannot be interpreted as a unique install choice.");
            Assert.AreEqual(2, rig.Session.InventoryState.GetQuantity("reactor_console"));
        }

        [Test]
        public void PhysicalTargetBrowse_ListsRealAnchorsAndNeverAuthorizesUnnamedInstall()
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            GdArray rows = Targets(rig.Session, "");
            Assert.Greater(rows.Count, 0);
            foreach (GdDict row in rows.OfType<GdDict>())
            {
                Assert.AreEqual(rig.Session.CurrentShip.ShipId, row.GetString("ship_id"));
                Assert.IsFalse(row.GetString("slot_id").StartsWith("hub_slot_", StringComparison.Ordinal));
                Assert.IsTrue(row.Has("world_position") && row.Has("occupied") && row.Has("requirements"));
                Assert.IsFalse(row.GetBool("ok"));
                Assert.AreEqual("selection_required", row.GetString("reason"));
            }
        }

        [Test]
        public void CompletedRemoval_RepeatedTicksAndOwnerRestoreDoNotReplayXpOrOutput()
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            GdDict row = Mounted(rig.Session);
            rig.Scene.PlayerPosition = Anchor(rig.Session, row);
            ProvisionWrench(rig.Session);
            rig.Session.BeginWorkHold();
            AssertPending(Request(rig.Session, "RequestComponentRemoval", row.GetString("instance_id"), null));
            CompletePending(rig, row.GetString("instance_id"), "player:player_local");
            GdDict saved = Capture(rig.Session), progression = rig.Session.PlayerProgression.GetSummary();
            GdArray log = rig.Session.TrainingEventBus.GetLog();
            for (int tick = 0; tick < 30; tick++) rig.Session.StageWorkAction(0.1);
            Assert.IsTrue((bool)Invoke(rig.Session, "RestoreComponentDomain", saved));
            for (int tick = 0; tick < 30; tick++) rig.Session.StageWorkAction(0.1);
            Equal(Registry(saved), Registry(Capture(rig.Session)), "Repeated completion/restore retains one exact instance.");
            Equal(saved.Get("receipts"), Capture(rig.Session).Get("receipts"), "Repeated completion/restore retains one receipt.");
            Equal(progression, rig.Session.PlayerProgression.GetSummary(), "Receipt-owned completion cannot grant XP on restore.");
            Equal(log, rig.Session.TrainingEventBus.GetLog(), "Receipt-owned training cannot append/replay on restore.");
        }

        [Test]
        public void OwnerCodec_PreservesExactLongRevisionAndNullAcrossActualJson()
        {
            Type codec = typeof(RunSession).Assembly.GetType("SynapticSea.Core.Session.ComponentDomainCodec");
            Assert.IsNotNull(codec, "Generation persistence needs an owned exact component codec; Godot numeric parsing loses typed long authority.");
            MethodInfo encode = codec.GetMethod("Encode", BindingFlags.Public | BindingFlags.Static);
            MethodInfo decode = codec.GetMethod("TryDecode", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(encode);
            Assert.IsNotNull(decode);
            var original = new GdDict { { "revision", long.MaxValue }, { "beyond_double", 9007199254740993L },
                { "condition_state", "unknown" }, { "condition", null }, { "anchor", new Vec3(1, 2, 3) },
                { "opaque_metadata", new GdDict { { "type", "integer" }, { "value", "not-a-codec-node" } } } };
            var encoded = (GdDict)encode.Invoke(null, new object[] { original });
            GdDict parsed = GdJson.ParseDict(GdJson.Stringify(encoded));
            object[] args = { parsed, null, null };
            Assert.IsTrue((bool)decode.Invoke(null, args), args[2] as string);
            Equal(original, args[1], "Owned codec must preserve exact long/null/anchor/metadata types after real Godot JSON parsing.");
        }

        [TestCase("output")]
        [TestCase("input")]
        public void DiagnosticComponentCraft_PreflightPreservesInputsJobStationAndXp(string componentSide)
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            // Explicit diagnostic fixture adapts an authored ordinary recipe to an actual catalog form.
            // There is no authored component acquisition recipe in the admitted scope.
            string form = Mounted(rig.Session).GetString("item_form");
            GdDict recipe = rig.Session.CraftingState.GetRecipe("weld_plating");
            Assert.IsFalse(recipe.IsEmpty);
            if (componentSide == "output") recipe.GetDictOrEmpty("produces")["item_id"] = form;
            else recipe.GetDictOrEmpty("ingredients")[form] = 1L;
            foreach (var ingredient in recipe.GetDictOrEmpty("ingredients"))
                rig.Session.InventoryState.Items[V.Str(ingredient.Key)] = V.I64(ingredient.Value) + 2L;
            rig.Session.CraftingState.GetOrCreateStation("workbench").SetPower(true);
            GdDict inventory = rig.Session.InventoryState.GetSummary();
            GdDict craft = rig.Session.CraftingState.GetSummary();
            GdDict progression = rig.Session.PlayerProgression.GetSummary();
            GdArray training = rig.Session.TrainingEventBus.GetLog();
            bool started = rig.Session.CraftingState.BeginCraft("weld_plating", rig.Session.InventoryState,
                rig.Session.MaterialState, 999L);
            Assert.IsFalse(started, "Diagnostic generic craft must reject component-form " + componentSide +
                " before charging anonymous ingredients or starting an output that cannot enter the registry.");
            Equal(inventory, rig.Session.InventoryState.GetSummary(), "Refused unsupported component craft preserves every ingredient.");
            Equal(craft, rig.Session.CraftingState.GetSummary(), "Refused unsupported component craft preserves job and station state.");
            Equal(progression, rig.Session.PlayerProgression.GetSummary(), "Refused component craft cannot train skill.");
            Equal(training, rig.Session.TrainingEventBus.GetLog(), "Refused component craft cannot append training.");
            GdDict entry = rig.Session.CraftingState.ListRecipeEntries("workbench", rig.Session.InventoryState, 999L)
                .OfType<GdDict>().Single(row => row.GetString("recipe_id") == "weld_plating");
            Assert.IsFalse(entry.GetBool("craftable"));
            Assert.AreEqual("diagnostic_component_crafting_unavailable", entry.GetString("status"));
        }

        static GdDict RemoveToPlayer(SessionHarness.Rig rig)
        {
            GdDict row = Mounted(rig.Session);
            rig.Scene.PlayerPosition = Anchor(rig.Session, row);
            ProvisionWrench(rig.Session);
            rig.Session.BeginWorkHold();
            AssertPending(Request(rig.Session, "RequestComponentRemoval", row.GetString("instance_id"), null));
            return CompletePending(rig, row.GetString("instance_id"), "player:player_local");
        }

        static string OpenActualCargo(SessionHarness.Rig rig)
        {
            CargoHoldControl control = rig.Session.CargoHoldControls.FirstOrDefault(c => c.CarrierId == rig.Session.CurrentShip.ShipId && c.IsValid);
            Assert.IsNotNull(control, "Golden fixture must have its actual cargo interaction control.");
            rig.Scene.PlayerPosition = control.GlobalPosition;
            Assert.IsTrue(control.TryDeposit(rig.Scene.PlayerPosition));
            string holder = ((GdDict)Invoke(rig.Session, "GetComponentHolderIds")).GetString("ship_cargo");
            Assert.IsNotEmpty(holder, "Actual reachable cargo interaction must authorize its owner-qualified holder.");
            return holder;
        }

        [Test]
        public void ChosenInstance_BagCargoAndTimedReinstallConserveExactEvidence()
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            GdDict original = Mounted(rig.Session);
            string id = original.GetString("instance_id"), source = original.GetString("holder");
            SetKnownCondition(rig.Session, id, 0.23);
            original = Registry(Capture(rig.Session)).GetDictOrEmpty(id).DeepCopy();
            GdDict physical = Capture(rig.Session).GetDictOrEmpty("physical_slots").GetDictOrEmpty(source);
            rig.Scene.PlayerPosition = Anchor(rig.Session, original);
            ProvisionWrench(rig.Session); rig.Session.BeginWorkHold();
            AssertPending(Request(rig.Session, "RequestComponentRemoval", id, null));
            CompletePending(rig, id, "player:player_local");
            double bagWeight = rig.Session.InventoryState.GetTotalWeight();
            string cargo = OpenActualCargo(rig);
            double cargoWeight = rig.Session.CurrentShip.GetInventory().GetTotalWeight();
            Assert.IsTrue(Request(rig.Session, "RequestComponentTransfer", id, cargo).GetBool("committed"));
            Assert.AreEqual(bagWeight - original.GetFloat("mass"), rig.Session.InventoryState.GetTotalWeight(), 0.000001);
            Assert.AreEqual(cargoWeight + original.GetFloat("mass"), rig.Session.CurrentShip.GetInventory().GetTotalWeight(), 0.000001);
            Assert.IsTrue(Request(rig.Session, "RequestComponentTransfer", id, "player:player_local").GetBool("committed"));
            // Explicit diagnostic power provisioning lets this fixture verify chosen mounting without changing authored balance.
            rig.Session.ShipModificationState.PowerSupply = 10000;
            GdDict target = Targets(rig.Session, id).OfType<GdDict>().Single(row => row.GetString("holder_id") == source);
            rig.Scene.PlayerPosition = (Vec3)target.Get("world_position"); rig.Session.BeginWorkHold();
            GdDict before = Capture(rig.Session);
            AssertPending(Request(rig.Session, "RequestComponentInstall", id, physical.GetString("ship_id"), physical.GetString("slot_id")));
            GdDict mounted = CompletePending(rig, id, source);
            Assert.AreEqual(0.23, mounted.GetFloat("condition"));
            Assert.AreEqual(original.GetFloat("mass"), mounted.GetFloat("mass"));
            Equal(original.Get("origin"), mounted.Get("origin"), "Storage and chosen mount preserve original physical provenance.");
            Equal(before.Get("machinery"), Capture(rig.Session).Get("machinery"), "Chosen remount never heals saved machinery damage.");
            Assert.AreEqual(0, rig.Session.InventoryState.GetQuantity(original.GetString("item_form")));
        }

        [Test]
        public void CargoCapacityReducedAfterRegistration_RefusesWithoutChangingEitherOwner()
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = RemoveToPlayer(rig);
            string cargo = OpenActualCargo(rig);
            rig.Session.CurrentShip.GetInventory().MaxWeight = 0;
            GdDict before = Capture(rig.Session);
            GdDict result = Request(rig.Session, "RequestComponentTransfer", row.GetString("instance_id"), cargo);
            Assert.IsFalse(result.GetBool("committed"), "Current cargo capacity, not its registration-time capacity, must gate named transfers.");
            Assert.AreEqual("capacity_mass", result.GetString("reason"));
            Equal(before, Capture(rig.Session), "Full destination preserves instance, stacks, receipts and holder revisions.");
        }

        [Test]
        public void CatalogOnlyAnonymousForm_RefusesCaptureValidationWithoutRemovingQuantity()
        {
            SessionHarness.Rig rig = DiagnosticBoot();
            var registered = new HashSet<string>(Registry(Capture(rig.Session)).Values.OfType<GdDict>().Select(row => row.GetString("item_form")));
            string form = new[] { "console_unit", "conduit_segment", "pump_assembly", "wall_locker", "machinery_block", "reactor_console", "air_recycler_unit", "nav_console", "thruster_control", "sensor_rack", "plating_plate" }
                .FirstOrDefault(candidate => rig.Session.ComponentCatalog.ComponentIdForItemForm(candidate).Length > 0 && !registered.Contains(candidate));
            Assert.IsNotNull(form, "Golden fixture must leave at least one actual catalog form unregistered.");
            rig.Session.InventoryState.Items[form] = 2L;
            GdDict capture = Capture(rig.Session); object[] args = { capture, null };
            Assert.IsFalse((bool)Api("ValidateComponentDomainRestore", 2).Invoke(rig.Session, args),
                "Parallel anonymous equipment outside observed registry forms cannot certify exact component capture.");
            Assert.AreEqual("diagnostic_anonymous_component_quantity", args[1]);
            Assert.AreEqual(2, rig.Session.InventoryState.GetQuantity(form), "Refusal cannot silently convert, heal or erase anonymous quantities.");
        }

        [Test]
        public void DiagnosticRemoval_PreservesExistingUnboltProgressNoise()
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = Mounted(rig.Session);
            rig.Scene.PlayerPosition = Anchor(rig.Session, row); ProvisionWrench(rig.Session); rig.Session.BeginWorkHold();
            AssertPending(Request(rig.Session, "RequestComponentRemoval", row.GetString("instance_id"), null));
            bool pulsed = false;
            for (int tick = 0; tick < 12; tick++) { rig.Session.StageWorkAction(0.1); pulsed |= rig.Session.WorkActionDriver.LastProgressNoise > 0; }
            Assert.IsTrue(pulsed,
                "Diagnostic owner integration must retain existing noisy unbolt work detection pulses.");
        }

        [TestCase("registry")]
        [TestCase("holders")]
        [TestCase("machinery")]
        [TestCase("placement")]
        [TestCase("inventory")]
        [TestCase("job")]
        [TestCase("progression")]
        [TestCase("receipt")]
        [TestCase("validation")]
        [TestCase("publication")]
        [TestCase("live_inventory")]
        [TestCase("live_machinery")]
        [TestCase("live_placement")]
        public void EveryComponentStageFault_LeavesAllBeforeImagesAndNoReceipt(string stage)
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = RemoveToPlayer(rig); string cargo = OpenActualCargo(rig);
            GdDict before = Capture(rig.Session), inventory = rig.Session.InventoryState.GetSummary(), progression = rig.Session.PlayerProgression.GetSummary();
            GdDict training = rig.Session.TrainingEventBus.ToDict(), placement = rig.Session.ComponentPlacementState.GetSummary();
            bool reached = false;
            rig.Session.ComponentStageHook = current => { if (current == stage) { reached = true; throw new InvalidOperationException("provisioned-stage-fault"); } };
            GdDict result = Request(rig.Session, "RequestComponentTransfer", row.GetString("instance_id"), cargo);
            rig.Session.ComponentStageHook = null;
            Assert.IsTrue(reached, "Fault fixture must reach named real owner boundary " + stage + ".");
            Assert.IsFalse(result.GetBool("committed"));
            Equal(before, Capture(rig.Session), "Fault cannot publish any component domain effect.");
            Equal(inventory, rig.Session.InventoryState.GetSummary(), "Fault preserves exact live bag.");
            Equal(progression, rig.Session.PlayerProgression.GetSummary(), "Fault preserves exact live progression.");
            Equal(training, rig.Session.TrainingEventBus.ToDict(), "Fault preserves exact live training receipts.");
            Equal(placement, rig.Session.ComponentPlacementState.GetSummary(), "Fault preserves exact live physical placement.");
        }

        [TestCase("live_inventory")]
        [TestCase("live_machinery")]
        [TestCase("live_placement")]
        public void EveryExternalPublicationHook_ObservesConsistentOldOwnerAndParticipatingViews(string stage)
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = Mounted(rig.Session);
            rig.Scene.PlayerPosition = Anchor(rig.Session, row); ProvisionWrench(rig.Session); rig.Session.BeginWorkHold();
            AssertPending(Request(rig.Session, "RequestComponentRemoval", row.GetString("instance_id"), null));
            bool reached = false, mixed = false;
            rig.Session.ComponentStageHook = current => {
                if (current != stage) return; reached = true;
                GdDict old = Capture(rig.Session).GetDictOrEmpty("participating_state");
                mixed |= !V.VariantEquals(old.Get("inventory"), rig.Session.InventoryState.GetSummary()) ||
                    !V.VariantEquals(old.Get("progression"), rig.Session.PlayerProgression.GetSummary()) ||
                    !V.VariantEquals(old.Get("training"), rig.Session.TrainingEventBus.ToDict());
            };
            CompletePending(rig, row.GetString("instance_id"), "player:player_local"); rig.Session.ComponentStageHook = null;
            Assert.IsTrue(reached);
            Assert.IsFalse(mixed, "External publication callback cannot see new XP/training/live view with old registry/receipts.");
        }

        [TestCase("wrench")]
        [TestCase("range")]
        [TestCase("participant")]
        public void FinalPublicationRevalidatesWorkAfterCallback_AndPreservesExternalChange(string change)
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = Mounted(rig.Session);
            rig.Scene.PlayerPosition = Anchor(rig.Session, row); ProvisionWrench(rig.Session); rig.Session.BeginWorkHold();
            GdDict before = Capture(rig.Session);
            AssertPending(Request(rig.Session, "RequestComponentRemoval", row.GetString("instance_id"), null));
            bool reached = false;
            rig.Session.ComponentStageHook = stage => {
                if (stage != "publication" || reached) return; reached = true;
                if (change == "wrench") rig.Session.InventoryState.Items.Erase("wrench");
                else if (change == "range") rig.Scene.PlayerPosition += new Vec3(1000, 0, 1000);
                else rig.Session.InventoryState.Items["scrap_metal"] = 37L;
            };
            for (int tick = 0; tick < 140; tick++) rig.Session.StageWorkAction(0.1);
            rig.Session.ComponentStageHook = null; GdDict after = Capture(rig.Session);
            Assert.IsTrue(reached);
            Equal(Registry(before), Registry(after), "Callback-induced stale work context cannot detach equipment.");
            Equal(before.Get("receipts"), after.Get("receipts"), "Rejected stale context cannot own a completion receipt.");
            if (change == "wrench") Assert.AreEqual(0, rig.Session.InventoryState.GetQuantity("wrench"), "Rejection cannot resurrect externally removed tool.");
            if (change == "participant") Assert.AreEqual(37, rig.Session.InventoryState.GetQuantity("scrap_metal"), "Rejection cannot erase external participant change.");
        }

        [TestCase("storage")]
        [TestCase("participant")]
        public void FinalPublicationRevalidatesTransferAfterCallback(string change)
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = RemoveToPlayer(rig); string cargo = OpenActualCargo(rig);
            GdDict before = Capture(rig.Session); bool reached = false;
            rig.Session.ComponentStageHook = stage => {
                if (stage != "publication" || reached) return; reached = true;
                if (change == "storage") rig.Session.CloseComponentStorage();
                else rig.Session.InventoryState.Items["scrap_metal"] = 37L;
            };
            GdDict result = Request(rig.Session, "RequestComponentTransfer", row.GetString("instance_id"), cargo);
            rig.Session.ComponentStageHook = null;
            Assert.IsTrue(reached); Assert.IsFalse(result.GetBool("committed"), "Final live storage/participant gate must run after callbacks.");
            Equal(Registry(before), Registry(Capture(rig.Session)), "Stale transfer preserves named source membership.");
            Equal(before.Get("receipts"), Capture(rig.Session).Get("receipts"), "Stale transfer cannot publish receipt.");
            if (change == "participant") Assert.AreEqual(37, rig.Session.InventoryState.GetQuantity("scrap_metal"));
        }

        [TestCase("duration")]
        [TestCase("action_id")]
        [TestCase("status")]
        [TestCase("command_id")]
        [TestCase("physical_holder_id")]
        [TestCase("ship_id")]
        [TestCase("slot_id")]
        [TestCase("source_holder_id")]
        [TestCase("destination_holder_id")]
        public void MalformedRestoredWork_RefusesBeforePublishingOrResuming(string field)
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = Mounted(rig.Session);
            rig.Scene.PlayerPosition = Anchor(rig.Session, row); ProvisionWrench(rig.Session); rig.Session.BeginWorkHold();
            AssertPending(Request(rig.Session, "RequestComponentRemoval", row.GetString("instance_id"), null));
            GdDict before = Capture(rig.Session), bad = before.DeepCopy(); GdDict work = bad.GetDictOrEmpty("component_work");
            if (field == "duration") work[field] = 0.001;
            else if (field == "source_holder_id") work[field] = "player:player_local";
            else if (field == "destination_holder_id") work[field] = row.GetString("holder");
            else if (field == "physical_holder_id") work[field] = before.GetDictOrEmpty("physical_slots").Keys.Select(key => V.Str(key)).First(key => key != row.GetString("holder"));
            else work[field] = "forged";
            Assert.IsFalse(rig.Session.RestoreComponentDomain(bad), "Malformed exact command binding cannot become resumable work: " + field);
            Equal(before, Capture(rig.Session), "Rejected work restore leaves complete current owner unchanged.");
        }

        [TestCase("commit_id_type")]
        [TestCase("commit_id_alias")]
        [TestCase("commit_id_missing")]
        [TestCase("training_receipt")]
        public void MalformedRestoredReceipt_RefusesUnboundTrainingAuthority(string fault)
        {
            SessionHarness.Rig rig = DiagnosticBoot(); RemoveToPlayer(rig);
            GdDict before = Capture(rig.Session), bad = before.DeepCopy();
            GdDict receipt = bad.GetDictOrEmpty("receipts").Values.OfType<GdDict>().First();
            if (fault == "commit_id_type") receipt["commit_id"] = 1L;
            else if (fault == "commit_id_alias") receipt["commit_id"] = "forged";
            else if (fault == "commit_id_missing") receipt.Erase("commit_id");
            else
            {
                GdDict record = bad.GetDictOrEmpty("participating_state").GetDictOrEmpty("training").GetArrayOrEmpty("log").OfType<GdDict>().First(r => r.GetBool("receipt_owned"));
                record["commit_id"] = "component_transfer:forged";
            }
            Assert.IsFalse(rig.Session.RestoreComponentDomain(bad), "Component receipt and receipt-owned training must have the same exact authority.");
            Equal(before, Capture(rig.Session), "Refused receipt restore cannot erase or reinterpret effects.");
        }

        [TestCase("missing_machinery")]
        [TestCase("live_inventory_fault")]
        public void FailedRestore_CannotLeakNewProgressionBookIntoOldLiveState(string failure)
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict before = Capture(rig.Session), bad = before.DeepCopy();
            GdDict progression = rig.Session.PlayerProgression.GetSummary();
            bad.GetDictOrEmpty("participating_state").GetDictOrEmpty("progression").GetDictOrEmpty("books_read")["provisioned-new-book"] = true;
            if (failure == "missing_machinery") bad.GetDictOrEmpty("machinery").Values.OfType<GdDict>().First()["system_id"] = "missing";
            else rig.Session.ComponentStageHook = stage => { if (stage == "live_inventory") throw new InvalidOperationException("provisioned-restore-fault"); };
            Assert.IsFalse(rig.Session.RestoreComponentDomain(bad)); rig.Session.ComponentStageHook = null;
            Equal(progression, rig.Session.PlayerProgression.GetSummary(), "Failed restore must exactly roll back additive BooksRead, not merge the old summary.");
            Equal(before, Capture(rig.Session), "Failed restore keeps old owner and participating state.");
        }

        [Test]
        public void CommittedNotification_ObservesNewOwnerMassProgressionAndReceiptTogether()
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = Mounted(rig.Session);
            rig.Scene.PlayerPosition = Anchor(rig.Session, row); ProvisionWrench(rig.Session); rig.Session.BeginWorkHold();
            bool notified = false, mixed = false;
            rig.Session.ComponentDomainChanged += result => {
                notified = true; GdDict complete = Capture(rig.Session), participants = complete.GetDictOrEmpty("participating_state");
                mixed |= !V.VariantEquals(participants.Get("inventory"), rig.Session.InventoryState.GetSummary()) ||
                    !V.VariantEquals(participants.Get("progression"), rig.Session.PlayerProgression.GetSummary()) ||
                    !V.VariantEquals(participants.Get("training"), rig.Session.TrainingEventBus.ToDict()) ||
                    !complete.GetDictOrEmpty("receipts").Has(result.GetString("commit_id")) ||
                    Registry(complete).GetDictOrEmpty(row.GetString("instance_id")).GetString("holder") != "player:player_local";
            };
            AssertPending(Request(rig.Session, "RequestComponentRemoval", row.GetString("instance_id"), null));
            CompletePending(rig, row.GetString("instance_id"), "player:player_local");
            Assert.IsTrue(notified); Assert.IsFalse(mixed, "Committed notification must expose new derived mass, registry, XP, training and receipt together.");
        }

        [Test]
        public void NotificationFailure_DoesNotUndoOrReplayCommittedTransfer()
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = RemoveToPlayer(rig); string cargo = OpenActualCargo(rig);
            rig.Session.ComponentDomainChanged += _ => throw new InvalidOperationException("provisioned-notification-fault");
            GdDict result = Request(rig.Session, "RequestComponentTransfer", row.GetString("instance_id"), cargo);
            Assert.IsTrue(result.GetBool("committed")); Assert.IsTrue(result.GetBool("presentation_failed"));
            GdDict saved = Capture(rig.Session);
            DomainTransactionCoordinator owner = (DomainTransactionCoordinator)typeof(RunSession).GetField("_componentDomain", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rig.Session);
            Assert.IsTrue(owner.Commit(result.GetString("transaction_id")).GetBool("committed"));
            Equal(saved, Capture(rig.Session), "Retry of an owned receipt cannot duplicate or undo effects after notification failure.");
        }

        [Test]
        public void ComponentCap_NeverMakesHealthyRawMachineryEligibleForPaidRepair()
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = LinkedMounted(rig.Session);
            SetKnownCondition(rig.Session, row.GetString("instance_id"), 0.23, 0.8);
            GdDict domain = Capture(rig.Session);
            string machineId = domain.GetDictOrEmpty("holders").GetDictOrEmpty(row.GetString("holder")).GetString("machinery_id");
            GdDict machine = domain.GetDictOrEmpty("machinery").GetDictOrEmpty(machineId);
            ShipSubcomponent sub = rig.Session.CurrentShip.SystemsManager.GetSystem(machine.GetString("system_id")).GetSubcomponent(machine.GetString("subcomponent_id"));
            Assert.AreEqual(0.8, sub.Health); Assert.AreEqual(0.23, sub.EffectiveHealth);
            Assert.IsTrue(sub.IsFunctional(), "Raw repair threshold remains independent from the installed condition cap.");
            GdDict repair = sub.Repair(new GdArray(sub.RequiredParts), new GdArray(sub.RequiredTools), 999L);
            Assert.IsFalse(repair.GetBool("success")); Assert.AreEqual("already_functional", repair.GetString("reason"));
            Assert.AreEqual(0.8, sub.Health);
        }

        [Test]
        public void RestoredPendingWork_PreservesProgressAndRequiresExplicitResume()
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = Mounted(rig.Session);
            rig.Scene.PlayerPosition = Anchor(rig.Session, row); ProvisionWrench(rig.Session); rig.Session.BeginWorkHold();
            AssertPending(Request(rig.Session, "RequestComponentRemoval", row.GetString("instance_id"), null));
            rig.Session.StageWorkAction(1.0); GdDict saved = Capture(rig.Session);
            double progress = saved.GetDictOrEmpty("component_work").GetFloat("progress"); Assert.Greater(progress, 0);
            Assert.IsTrue(rig.Session.RestoreComponentDomain(saved));
            for (int tick = 0; tick < 30; tick++) rig.Session.StageWorkAction(0.1);
            Assert.AreEqual(progress, Work(rig.Session).GetFloat("progress"));
            Assert.IsTrue(Work(rig.Session).GetBool("resume_required"));
            Equal(Registry(saved), Registry(Capture(rig.Session)), "Restore clears held input and cannot silently finish chosen work.");
            rig.Session.BeginWorkHold(); CompletePending(rig, row.GetString("instance_id"), "player:player_local");
            Assert.AreEqual(saved.GetDictOrEmpty("receipts").Count + 1, Capture(rig.Session).GetDictOrEmpty("receipts").Count);
        }

        [TestCase("capture")]
        [TestCase("list")]
        [TestCase("command")]
        public void SaturatedOwnerRevision_RefusesChangedParticipantsWithoutThrowOrPartialPublication(string entry)
        {
            SessionHarness.Rig rig = DiagnosticBoot(); GdDict row = Mounted(rig.Session);
            ProvisionWrench(rig.Session); rig.Scene.PlayerPosition = Anchor(rig.Session, row);
            GdDict saturated = Capture(rig.Session); saturated["revision"] = long.MaxValue;
            GdDict wire = ComponentDomainCodec.Encode(saturated);
            Assert.IsTrue(ComponentDomainCodec.TryDecode(GdJson.ParseDict(GdJson.Stringify(wire)), out GdDict exact, out string codecReason), codecReason);
            Assert.IsTrue(rig.Session.RestoreComponentDomain(exact));
            DomainTransactionCoordinator owner = (DomainTransactionCoordinator)typeof(RunSession).GetField("_componentDomain", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rig.Session);
            GdDict before = owner.GetSummary(); rig.Session.InventoryState.Items["scrap_metal"] = 37L;
            GdDict result = null;
            Assert.DoesNotThrow(() => {
                if (entry == "capture") result = rig.Session.CaptureComponentDomain();
                else if (entry == "list") rig.Session.ListComponentInstances("player:player_local");
                else result = rig.Session.RequestComponentRemoval(row.GetString("instance_id"));
            }, "A valid exact Int64 boundary cannot crash live readers/commands when ordinary participants change.");
            if (entry != "list")
            {
                Assert.IsFalse(result.GetBool("ok"));
                Assert.That(result.GetString("reason"), Is.EqualTo("revision_overflow").Or.EqualTo("revision_exhausted"));
            }
            Equal(before, owner.GetSummary(), "Exhausted authority cannot partially publish a changed participant snapshot.");
            Assert.AreEqual(37, rig.Session.InventoryState.GetQuantity("scrap_metal"), "Refusal preserves the independent ordinary mutation.");
        }

        static void ProduceFailedTerminal(SessionHarness.Rig rig, GenerationStorage storage)
        {
            Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Provisioned diagnostic terminal witness"), GdJson.Stringify(rig.Session.LastSaveResult));
            storage.BeforeWrite = (path, text) => { if (path.EndsWith("/terminal.json", StringComparison.Ordinal)) throw new IOException("provisioned-terminal-final-write-fault"); };
            Assert.AreEqual(0, rig.Session.EndRun("death"));
            Assert.IsFalse(rig.Session.LastSaveResult.GetBool("ok"));
            Assert.IsFalse(rig.Session.SliceComplete, "Fixture must reach persistent pending terminal rather than successful completion.");
        }

        [TestCase("removal")]
        [TestCase("transfer")]
        [TestCase("install")]
        [TestCase("interact")]
        [TestCase("craft")]
        [TestCase("hold")]
        [TestCase("progress")]
        public void FailedTerminalProducer_BlocksDirectComponentCraftAndWorkAdmission(string entry)
        {
            var storage = new GenerationStorage(new MemoryStorage()); SessionHarness.Rig rig = DiagnosticBoot(storage);
            GdDict row = Mounted(rig.Session), original = row.DeepCopy();
            if (entry == "transfer" || entry == "install") row = RemoveToPlayer(rig);
            rig.Scene.PlayerPosition = Anchor(rig.Session, original); ProvisionWrench(rig.Session);
            if (entry == "hold" || entry == "progress")
            {
                rig.Session.BeginWorkHold(); AssertPending(rig.Session.RequestComponentRemoval(row.GetString("instance_id")));
                rig.Session.StageWorkAction(0.1);
            }
            if (entry == "craft")
                foreach (var ingredient in rig.Session.CraftingState.GetRecipe("weld_plating").GetDictOrEmpty("ingredients"))
                    rig.Session.InventoryState.Items[V.Str(ingredient.Key)] = V.I64(ingredient.Value) + 2L;
            ProduceFailedTerminal(rig, storage);
            GdDict before = Capture(rig.Session), inventory = rig.Session.InventoryState.GetSummary(), craft = rig.Session.CraftingState.GetSummary();
            double progress = Work(rig.Session).GetFloat("progress");
            if (entry == "interact") Assert.AreEqual("terminal_pending", rig.Session.RequestInteract());
            else if (entry == "craft") Assert.IsFalse(rig.Session.CraftingState.BeginCraft("weld_plating", rig.Session.InventoryState, rig.Session.MaterialState, 999L), "Pending terminal cannot charge ordinary diagnostic craft ingredients.");
            else if (entry == "hold") Assert.IsFalse(rig.Session.BeginWorkHold(), "Pending terminal cannot resume/authorize held component work.");
            else if (entry == "progress") { rig.Session.StageWorkAction(1.0); Assert.AreEqual(progress, Work(rig.Session).GetFloat("progress"), "Direct work stage cannot advance after pending terminal."); }
            else
            {
                GdDict result = entry == "removal" ? rig.Session.RequestComponentRemoval(row.GetString("instance_id")) :
                    entry == "transfer" ? rig.Session.RequestComponentTransfer(row.GetString("instance_id"), "ship_cargo:" + rig.Session.CurrentShip.ShipId) :
                    rig.Session.RequestComponentInstall(row.GetString("instance_id"), before.GetDictOrEmpty("holders").GetDictOrEmpty(original.GetString("holder")).GetString("owner_id"),
                        Capture(rig.Session).GetDictOrEmpty("holders").GetDictOrEmpty(original.GetString("holder")).GetString("slot_id"));
                Assert.IsFalse(result.GetBool("committed")); Assert.IsFalse(result.GetBool("ok")); Assert.AreEqual("terminal_pending", result.GetString("reason"));
            }
            Equal(Registry(before), Registry(Capture(rig.Session)), "Pending terminal blocks direct owner mutations.");
            Equal(before.Get("receipts"), Capture(rig.Session).Get("receipts"), "Pending terminal cannot mint effect authority.");
            Equal(inventory, rig.Session.InventoryState.GetSummary(), "Pending terminal preserves craft inputs/bag.");
            Equal(craft, rig.Session.CraftingState.GetSummary(), "Pending terminal preserves ordinary craft job/station.");
        }

        [Test]
        public void FailedTerminalAtPublicationCallback_VetoesComponentCompletion()
        {
            var storage = new GenerationStorage(new MemoryStorage()); SessionHarness.Rig rig = DiagnosticBoot(storage);
            GdDict row = Mounted(rig.Session); ProvisionWrench(rig.Session); rig.Scene.PlayerPosition = Anchor(rig.Session, row);
            Assert.IsTrue(rig.Session.RequestSaveToSlot("world", "world", "Provisioned diagnostic terminal witness"), GdJson.Stringify(rig.Session.LastSaveResult));
            rig.Session.BeginWorkHold(); AssertPending(rig.Session.RequestComponentRemoval(row.GetString("instance_id")));
            GdDict before = Capture(rig.Session); bool reached = false;
            storage.BeforeWrite = (path, text) => { if (path.EndsWith("/terminal.json", StringComparison.Ordinal)) throw new IOException("provisioned-terminal-final-write-fault"); };
            rig.Session.ComponentStageHook = stage => { if (stage == "publication" && !reached) { reached = true; rig.Session.EndRun("death"); } };
            for (int tick = 0; tick < 140; tick++) rig.Session.StageWorkAction(0.1);
            rig.Session.ComponentStageHook = null;
            Assert.IsTrue(reached); Assert.IsFalse(rig.Session.LastSaveResult.GetBool("ok")); Assert.IsFalse(rig.Session.SliceComplete);
            Equal(Registry(before), Registry(Capture(rig.Session)), "Pending terminal transition during private staging vetoes component completion.");
            Equal(before.Get("receipts"), Capture(rig.Session).Get("receipts"), "Pending terminal transition cannot publish completion authority.");
        }

        [TestCase("vector", "e")]
        [TestCase("vector", "e+")]
        [TestCase("array", "e")]
        [TestCase("array", "e+")]
        public void ArchivedPhysicalWitness_RejectsOriginalMalformedExponentToken(string representation, string suffix)
        {
            SessionHarness.Rig rig = DiagnosticBoot(); RunSession session = rig.Session;
            session.ForceRepairAll(); GdDict travelled = null;
            foreach (string id in session.ScannableMarkerIds())
            { travelled = session.TravelToMarkerId(id); if (travelled.GetBool("success")) break; }
            Assert.IsNotNull(travelled); Assert.IsTrue(travelled.GetBool("success"), GdJson.Stringify(travelled));
            string owner = session.CurrentShip.ShipId;
            GdDict domain = Capture(session), layout = session.CurrentShip.BuiltLayout.DeepCopy();
            Assert.IsTrue(RunSession.ValidateComponentPhysicalLayout(domain, owner, layout, out string initialReason),
                "Actual retained runtime layout is the positive control: " + initialReason);
            GdDict slot = domain.GetDictOrEmpty("physical_slots").Values.OfType<GdDict>().First(row => row.GetString("ship_id") == owner);
            GdDict room = layout.GetArrayOrEmpty("rooms").OfType<GdDict>().Single(row => row.GetString("id") == slot.GetString("room_id"));
            GdArray cell = slot.GetArrayOrEmpty("cell");
            string key = room.GetInt("deck") + "|" + V.I64(cell[0]) + "|" + V.I64(cell[1]);
            GdDict floor = layout.GetDictOrEmpty("structural_plan").GetArrayOrEmpty("floor_placements").OfType<GdDict>()
                .Single(row => row.GetString("cell_key") == key && row.GetString("room_id") == room.GetString("id"));
            GdArray position = GeneratedShipLayout.ReadPlacementPosition(floor);
            Assert.AreEqual(3, position.Count);
            string x = V.F64(position[0]).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                y = V.F64(position[1]).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                z = V.F64(position[2]).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string field = floor.Get("position") != null ? "position" : "world_position";
            floor[field] = representation == "vector" ? (object)("(" + x + "e+0, " + y + ", " + z + ")") : GdArray.Of(x + "e+0", y, z);
            Assert.IsTrue(RunSession.ValidateComponentPhysicalLayout(domain, owner, layout, out string validReason),
                "Complete exponent tokens preserve exact witnessed coordinates: " + validReason);
            floor[field] = representation == "vector" ? (object)("(" + x + suffix + ", " + y + ", " + z + ")") : GdArray.Of(x + suffix, y, z);
            Assert.IsFalse(RunSession.ValidateComponentPhysicalLayout(domain, owner, layout, out string reason),
                "Original malformed " + representation + " coordinate " + x + suffix + " must refuse before legacy prefix conversion: " + reason);
        }
    }
}
