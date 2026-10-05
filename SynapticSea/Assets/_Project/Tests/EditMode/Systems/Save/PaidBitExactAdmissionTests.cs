using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Session;

namespace SynapticSea.Tests.Systems
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class PaidBitExactAdmissionTests : InfraDataTestBase
    {
        readonly List<RunSession> _sessions = new List<RunSession>();
        IEngineInfo _engine;
        [SetUp] public void SetEngine() { _engine=CoreServices.Engine; CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion); }
        [TearDown] public void Cleanup() { try { foreach(var s in _sessions)s.Dispose();_sessions.Clear(); } finally { CoreServices.Engine=_engine; } }
        RunSession Boot(bool bits,out SessionHarness.Rig rig,IStorage storage=null)
        {
            var deps=SessionHarness.GoldenDeps(out rig);SessionHarness.OverlayGamePlayability(deps);
            deps.EnablePaidCrafting=true;deps.EnableBitExactPaidCompatibility=bits;
            if(storage!=null)deps.Storage=storage;
            GodotGlobalRandom.Seed(173L);
            rig.Session=RunSession.Create(deps);_sessions.Add(rig.Session);
            Assert.IsTrue(rig.Session.PlayableStarted,rig.Session.LastFailureReason);
            rig.Session.ThreatManager.Threats.Clear();rig.Scene.PlayerPosition=new Vec3(0,0,0);
            return rig.Session;
        }
        static GdDict Capture(RunSession s)
        {
            var result=SavePayloadAssembler.Build(s,"slot_01","manual");
            Assert.IsTrue(result.GetBool("ok"),GdJson.Stringify(result));return result.GetDictOrEmpty("payloads");
        }
        static SaveCommitCoordinator Coordinator(RunSession s,IStorage storage,GdDict compatibility=null) =>
            new SaveCommitCoordinator(storage,SaveLoadService.PaidGenerationRoot,new GenerationAuthority(),compatibility??s.SaveLoadService.ComponentCompatibility(),allowPaidCrafting:true);
        static GdDict Validate(SaveCommitCoordinator c,GdDict p) => c.ValidateSuppliedPayload(p,p.GetString("run_id"),"slot_01");
        static void Refuses(SaveCommitCoordinator c,GdDict p) { var r=Validate(c,p);Assert.IsFalse(r.GetBool("ok"),GdJson.Stringify(r)); }

        [Test]
        public void LoaderWithoutBitExactCapabilityRefusesCommittedV2Package()
        {
            var s=Boot(true,out var rig);
            Assert.IsTrue(s.RequestSaveToSlot("slot_01","manual","V2 loader proof"),GdJson.Stringify(s.LastSaveResult));
            Assert.IsTrue(s.SaveLoadService.SelectGeneration("slot_01").GetBool("ok"));
            var legacyLoader=new SaveLoadService(rig.Storage,rig.Clock,false,true);
            Assert.IsFalse(legacyLoader.SelectGeneration("slot_01").GetBool("ok"));
            Assert.IsTrue(s.SaveLoadService.SelectGeneration("slot_01").GetBool("ok"),"refused reader must preserve admitted package");
        }
        [TestCase("missing_binding")][TestCase("wrong_binding")][TestCase("reader_profile_absent")][TestCase("request_profile_absent")]
        public void V2PackageRequiresMatchingBindingAndBothCapabilities(string mutation)
        {
            var s=Boot(true,out var rig);var p=Capture(s);var expected=s.SaveLoadService.ComponentCompatibility();
            Assert.IsTrue(Validate(Coordinator(s,rig.Storage),p).GetBool("ok"));
            if(mutation=="missing_binding")p.GetDictOrEmpty("binding").Erase("hash_algorithm");
            if(mutation=="wrong_binding")p.GetDictOrEmpty("binding")["hash_algorithm"]="legacy";
            if(mutation=="reader_profile_absent")expected.GetDictOrEmpty("profiles").Erase(ComponentDomainCodec.BitExactSchema);
            if(mutation=="request_profile_absent")p.GetDictOrEmpty("compatibility").GetDictOrEmpty("profiles").Erase(ComponentDomainCodec.BitExactSchema);
            Refuses(Coordinator(s,rig.Storage,expected),p);
        }
        [TestCase("binding")][TestCase("envelope")][TestCase("world_wire")]
        public void LegacyPackageRefusesInjectedBitBindingOrMixedWire(string mutation)
        {
            var s=Boot(false,out var rig);var p=Capture(s);
            Assert.IsTrue(Validate(Coordinator(s,rig.Storage),p).GetBool("ok"));
            if(mutation=="binding")p.GetDictOrEmpty("binding")["hash_algorithm"]=PaidCraftingState.BitExactHashAlgorithm;
            else
            {
                var world=PaidSnapshotCodec.Parse(p.GetString("world_text"),PaidSnapshotCodec.Policy.OrdinaryWorld);
                if(mutation=="envelope")world.GetDictOrEmpty("home_ship").GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft")["schema_version"]=2L;
                else
                {
                    var bits=Boot(true,out _);var bp=Capture(bits);
                    world.GetDictOrEmpty("home_ship")["crafting_summary"]=PaidSnapshotCodec.Parse(bp.GetString("world_text"),PaidSnapshotCodec.Policy.OrdinaryWorld).GetDictOrEmpty("home_ship").GetDictOrEmpty("crafting_summary").DeepCopy();
                }
                p["world_text"]=mutation=="envelope"?PaidSnapshotCodec.Stringify(world):PaidSnapshotCodec.Stringify(world,PaidSnapshotCodec.Policy.OrdinaryWorld);
            }
            Refuses(Coordinator(s,rig.Storage),p);
        }
        [TestCase(true)][TestCase(false)]
        public void NormalCommitRefusesAlgorithmTransitionAndPreservesSelectedParent(bool parentBits)
        {
            var parent=Boot(parentBits,out var rig);parent.SaveLoadService.BitExactPaidCompatibilityEnabled=true;
            Assert.IsTrue(parent.RequestSaveToSlot("slot_01","manual","Parent algorithm"),GdJson.Stringify(parent.LastSaveResult));
            var before=parent.SaveLoadService.SelectGeneration("slot_01");Assert.IsTrue(before.GetBool("ok"));
            var child=Boot(!parentBits,out _,rig.Storage);child.SaveLoadService.BitExactPaidCompatibilityEnabled=true;
            var p=Capture(child);Assert.AreEqual(before.GetString("run_id"),p.GetString("run_id"));
            Assert.AreEqual(before.GetString("generation_id"),p.GetString("parent_generation_id"));
            var c=Coordinator(child,rig.Storage);Assert.IsTrue(Validate(c,p).GetBool("ok"));
            Assert.IsFalse(c.Commit(p,p.GetString("run_id"),"slot_01").GetBool("ok"));
            var after=parent.SaveLoadService.SelectGeneration("slot_01");Assert.IsTrue(after.GetBool("ok"));
            Assert.AreEqual(before.GetString("generation_id"),after.GetString("generation_id"));
        }
        [TestCase("active")][TestCase("home")]
        public void V2PackageRefusesSignedZeroPoseMirrorMismatch(string mirror)
        {
            var s=Boot(true,out var rig);var p=Capture(s);
            Assert.IsTrue(Validate(Coordinator(s,rig.Storage),p).GetBool("ok"));
            bool home=mirror=="home";string key=home?"world_text":"run_text";
            var policy=home?PaidSnapshotCodec.Policy.OrdinaryWorld:PaidSnapshotCodec.Policy.OrdinaryRun;
            var snapshot=PaidSnapshotCodec.Parse(p.GetString(key),policy);
            var position=(home?snapshot.GetDictOrEmpty("home_ship"):snapshot).GetArrayOrEmpty("player_position");
            Assert.AreEqual(3,position.Count);
            Assert.AreEqual(0L,BitConverter.DoubleToInt64Bits(V.F64(position[0])));
            position[0]=BitConverter.Int64BitsToDouble(long.MinValue);
            p[key]=PaidSnapshotCodec.Stringify(snapshot,policy);
            StringAssert.Contains("-0.0",p.GetString(key));
            Refuses(Coordinator(s,rig.Storage),p);
        }
        [TestCase(false,false)][TestCase(true,false)][TestCase(false,true)][TestCase(true,true)]
        public void OptionalKnownCapabilitySubsetsKeepRequiredCatalogExact(bool omitBits,bool omitAway)
        {
            var s=Boot(false,out var rig);s.SaveLoadService.BitExactPaidCompatibilityEnabled=true;s.SaveLoadService.FirstAwaySalvageProfileEnabled=true;
            var p=Capture(s);var expected=s.SaveLoadService.ComponentCompatibility();var supplied=p.GetDictOrEmpty("compatibility");
            if(omitBits)supplied.GetDictOrEmpty("profiles").Erase(ComponentDomainCodec.BitExactSchema);
            if(omitAway)supplied.GetDictOrEmpty("profiles").Erase(FirstAwayGenerationInputs.Profile);
            var c=Coordinator(s,rig.Storage,expected);Assert.IsTrue(Validate(c,p).GetBool("ok"));
            supplied["catalog_version"]="unreviewed";Refuses(c,p);
        }
    }
}
