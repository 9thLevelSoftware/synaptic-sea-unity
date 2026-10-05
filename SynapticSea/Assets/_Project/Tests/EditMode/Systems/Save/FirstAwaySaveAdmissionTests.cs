using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
using SynapticSea.Tests.Session;

namespace SynapticSea.Tests.Systems
{
    // Focused admission regressions; full profile commit/restore roundtrip belongs to session integration tests.
    public sealed class FirstAwaySaveAdmissionTests
    {
        IResourceReader _resources;
        [SetUp] public void SetUp() { _resources=CoreServices.Resources; CoreServices.Resources=new FileSystemResourceReader(Fixtures.StreamingDataRoot); CatalogRegistry.Clear(); }
        [TearDown] public void TearDown() { CoreServices.Resources=_resources; CatalogRegistry.Clear(); }
        static GdDict Extended() { var c=GenerationFixtures.Compatibility();c.GetDictOrEmpty("profiles")[FirstAwayGenerationInputs.Profile]=FirstAwayGenerationInputs.Profile;return c; }
        static SaveCommitCoordinator Coordinator(IStorage storage,GdDict compatibility) => new SaveCommitCoordinator(storage,GenerationFixtures.Root,new GenerationAuthority(),compatibility);
        [Test] public void ExplicitCapabilityReadsExactPriorGenerationWithoutRewritingItsBytes()
        {
            var storage=new MemoryStorage();var request=GenerationFixtures.Request();
            Assert.IsTrue(GenerationFixtures.Coordinator(storage).Commit(request,GenerationFixtures.Run,"slot_01").GetBool("ok"));
            string pointer=storage.ReadText(GenerationFixtures.Active("slot_01"));
            var extended=Coordinator(storage,Extended());
            GenerationFixtures.AssertBundle(request,extended.ReadSelected("slot_01"));
            Assert.AreEqual(pointer,storage.ReadText(GenerationFixtures.Active("slot_01")));
        }
        [TestCase("catalog")][TestCase("unknown_profile")][TestCase("removed_profile")]
        public void KnownExtensionRejectsOtherCatalogChangesAndPreservesActivePointer(string mutation)
        {
            var storage=new MemoryStorage();var baseline=GenerationFixtures.Request();
            Assert.IsTrue(GenerationFixtures.Coordinator(storage).Commit(baseline,GenerationFixtures.Run,"slot_01").GetBool("ok"));
            string pointer=storage.ReadText(GenerationFixtures.Active("slot_01"));
            var request=GenerationFixtures.Request(generation:"bad-profile-child");var c=Extended();request["compatibility"]=c;
            if(mutation=="catalog")c["catalog_version"]="unreviewed";
            if(mutation=="unknown_profile")c.GetDictOrEmpty("profiles")["unknown_profile"]="unknown_profile";
            if(mutation=="removed_profile")c.GetDictOrEmpty("profiles").Erase(ConstrainedExpedition.Profile);
            Assert.IsFalse(Coordinator(storage,Extended()).Commit(request,GenerationFixtures.Run,"slot_01").GetBool("ok"));
            Assert.AreEqual(pointer,storage.ReadText(GenerationFixtures.Active("slot_01")));
            GenerationFixtures.AssertBundle(baseline,GenerationFixtures.Coordinator(storage).ReadSelected("slot_01"));
        }
        [Test] public void BaselineCapabilityRefusesExtendedRequest()
        {
            var request=GenerationFixtures.Request();request["compatibility"]=Extended();
            Assert.AreEqual("incompatible_content",GenerationFixtures.Coordinator(new MemoryStorage()).ValidateSuppliedPayload(request,GenerationFixtures.Run,"slot_01").GetString("reason"));
        }
        [Test]
        public void StationaryHomeAnchorSurvivesAwayContinueAndGuardedReturnWithoutOriginGuess()
        {
            var engine = CoreServices.Engine; CoreServices.Engine = new FixedEngineInfo(SessionHarness.GodotVersion);
            RunSession s = null;
            try
            {
                var deps = SessionHarness.GoldenDeps(out var rig); SessionHarness.OverlayGamePlayability(deps); deps.EnablePaidCrafting = true;
                s = rig.Session = RunSession.Create(deps); s.EnableReviewedFirstAwayProfile = true;
                // Nonzero world-start fixture demonstrates that return uses retained ownership, not a literal origin.
                s.StartingHomeSeaPosition = new Vec3(42, 0, -72); s.HomeSeaPosition = s.StartingHomeSeaPosition;
                s.SynapticSeaWorld.SetPlayerPosition(s.HomeSeaPosition);
                var anchorOnlyCapture = SavePayloadAssembler.Build(s, "world", "world"); Assert.IsTrue(anchorOnlyCapture.GetBool("ok"));
                var anchorOnly = anchorOnlyCapture.GetDictOrEmpty("payloads");
                Assert.AreEqual(0, s.VisitedShips.Count, "anchor capability tested without any new-profile ship artifacts");
                var anchorCoordinator = new SaveCommitCoordinator(rig.Storage, SaveLoadService.PaidGenerationRoot, new GenerationAuthority(), s.SaveLoadService.ComponentCompatibility(), allowPaidCrafting: true);
                Assert.IsTrue(anchorCoordinator.ValidateSuppliedPayload(anchorOnly, anchorOnly.GetString("run_id"), "world").GetBool("ok"));
                var anchorStripped = anchorOnly.DeepCopy(); anchorStripped.GetDictOrEmpty("compatibility").GetDictOrEmpty("profiles").Erase(FirstAwayGenerationInputs.Profile);
                Assert.IsFalse(anchorCoordinator.ValidateSuppliedPayload(anchorStripped, anchorStripped.GetString("run_id"), "world").GetBool("ok"));
                s.ForceRepairAll(); s.ThreatManager.Threats.Clear();
                foreach (string id in s.LifeboatShip.SystemsManager.Systems.Keys.ToList())
                    foreach (var part in s.LifeboatShip.SystemsManager.GetSystem(id).Subcomponents) s.LifeboatShip.SystemsManager.ForceRepair(id, part.SubcomponentId);
                s.PropulsionExpandedState.Configure(new GdDict { { "thrust_percent", 100.0 }, { "operational", true } });
                GdDict travelled = null;
                foreach (string id in s.ScannableMarkerIds()) { travelled = s.TravelToMarkerId(id); if (travelled.GetBool("success")) break; }
                Assert.IsTrue(travelled.GetBool("success"), GdJson.Stringify(travelled));
                string marker = s.CurrentShip.MarkerId, descriptor = s.CurrentShip.Blueprint.FirstAwayDescriptorText;
                Assert.IsTrue(s.RequestSave(), GdJson.Stringify(s.LastSaveResult)); Assert.IsTrue(s.RequestLoad());
                Assert.AreEqual(new Vec3(42, 0, -72), s.HomeSeaPosition); Assert.AreEqual("", s.HomeSeaMarkerId);
                var bridge = s.BridgeTerminals.Single(t => t.ShipId == s.LifeboatShip.ShipId);
                rig.Scene.PlayerPosition = bridge.GlobalPosition;
                var items = s.InventoryState.Items.DeepCopy(); var xp = s.PlayerProgression.GetSummary().DeepCopy();
                var returned = s.ReturnHomeFromNavigation(); Assert.IsTrue(returned.GetBool("success"), GdJson.Stringify(returned));
                Assert.AreEqual(new Vec3(42, 0, -72), s.SynapticSeaWorld.PlayerPosition); Assert.IsFalse(s.AwayFromStart);
                Assert.IsTrue(s.RequestSave(), GdJson.Stringify(s.LastSaveResult)); Assert.IsTrue(s.RequestLoad());
                Assert.AreEqual(new Vec3(42, 0, -72), s.HomeSeaPosition); Assert.AreEqual(s.HomeSeaPosition, s.SynapticSeaWorld.PlayerPosition);
                Assert.AreEqual(descriptor, s.VisitedShips[marker].Blueprint.FirstAwayDescriptorText);
                var capture = SavePayloadAssembler.Build(s, "world", "world"); Assert.IsTrue(capture.GetBool("ok"));
                var payload = capture.GetDictOrEmpty("payloads");
                var stripped = payload.DeepCopy(); stripped.GetDictOrEmpty("compatibility").GetDictOrEmpty("profiles").Erase(FirstAwayGenerationInputs.Profile);
                var coordinator = new SaveCommitCoordinator(rig.Storage, SaveLoadService.PaidGenerationRoot, new GenerationAuthority(), s.SaveLoadService.ComponentCompatibility(), allowPaidCrafting: true);
                Assert.IsFalse(coordinator.ValidateSuppliedPayload(stripped, stripped.GetString("run_id"), "world").GetBool("ok"), "baseline request cannot smuggle stationary anchor through extended reader");
                var baseline = s.SaveLoadService.ComponentCompatibility().DeepCopy(); baseline.GetDictOrEmpty("profiles").Erase(FirstAwayGenerationInputs.Profile);
                var oldReader = new SaveCommitCoordinator(rig.Storage, SaveLoadService.PaidGenerationRoot, new GenerationAuthority(), baseline, allowPaidCrafting: true);
                Assert.IsFalse(oldReader.ValidateSuppliedPayload(payload, payload.GetString("run_id"), "world").GetBool("ok"));
                Assert.AreEqual(new Vec3(42, 0, -72), s.HomeSeaPosition);
                Assert.IsTrue(V.VariantEquals(items, s.InventoryState.Items)); Assert.IsTrue(V.VariantEquals(xp, s.PlayerProgression.GetSummary()));
            }
            finally { s?.Dispose(); CoreServices.Engine = engine; }
        }

        [Test]
        public void NormalPaidGenerationRoundTripPreservesProfileRawArtifactsDescriptorAndInventory()
        {
            var engine=CoreServices.Engine; CoreServices.Engine=new FixedEngineInfo(SessionHarness.GodotVersion);
            RunSession session=null;
            try
            {
                var deps=SessionHarness.GoldenDeps(out var rig);SessionHarness.OverlayGamePlayability(deps);deps.EnablePaidCrafting=true;
                session=rig.Session=RunSession.Create(deps);Assert.IsTrue(session.PlayableStarted,session.LastFailureReason);
                Assert.IsTrue(session.RequestSaveToSlot("world","world","Baseline compatibility witness"),GdJson.Stringify(session.LastSaveResult));
                var baseline=session.SaveLoadService.SelectGeneration("world");Assert.IsTrue(baseline.GetBool("ok"));
                session.EnableReviewedFirstAwayProfile=true;
                Assert.IsTrue(session.ApplySelectedGeneration(baseline),GdJson.Stringify(session.LastSaveResult));
                // Provisioned transport fixture only: physical acquisition, earned skills and cost proof belong to the checkpoint.
                session.ForceRepairAll();session.ThreatManager.Threats.Clear();
                var boat=session.LifeboatShip.SystemsManager;
                foreach(string system in boat.Systems.Keys.ToList())
                    foreach(var part in boat.GetSystem(system).Subcomponents)boat.ForceRepair(system,part.SubcomponentId);
                session.PropulsionExpandedState.Configure(new GdDict{{"thrust_percent",100.0},{"operational",true}});
                Assert.IsTrue(session.TravelCapability().GetBool("success"),GdJson.Stringify(session.TravelCapability()));
                GdDict travelled=null;
                foreach(string id in session.ScannableMarkerIds())
                {travelled=session.TravelToMarkerId(id);if(travelled.GetBool("success"))break;}
                Assert.IsNotNull(travelled);Assert.IsTrue(travelled.GetBool("success"),GdJson.Stringify(travelled));
                string owner=session.CurrentShip.ShipId,marker=session.CurrentShip.MarkerId,descriptor=session.CurrentShip.Blueprint.FirstAwayDescriptorText;
                Assert.AreEqual(FirstAwayGenerationInputs.Profile,session.CurrentShip.Blueprint.GenerationProfile);Assert.IsNotEmpty(descriptor);
                var inventory=session.InventoryState.GetSummary().DeepCopy();var progression=session.PlayerProgression.GetSummary().DeepCopy();
                bool profileSaved=session.RequestSaveToSlot("slot_01","manual","Profile raw artifact roundtrip");
                if(!profileSaved)
                {
                    var failedCapture=SavePayloadAssembler.Build(session,"slot_01","manual");
                    if(failedCapture.GetBool("ok"))
                    {
                        var failedPayload=failedCapture.GetDictOrEmpty("payloads");
                        foreach(var pair in failedPayload.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references"))
                        {
                            var reference=(GdDict)pair.Value;
                            var records=failedPayload.GetArrayOrEmpty("artifacts").OfType<GdDict>();
                            var layoutRecord=records.Single(r=>r.GetString("logical_path")==reference.GetString("layout_path"));
                            var kitRecord=records.Single(r=>r.GetString("logical_path")==reference.GetString("kit_path"));
                            var layout=GdJson.ParseDict(layoutRecord.GetString("text"));var kit=GdJson.ParseDict(kitRecord.GetString("text"));
                            var flags=BindingFlags.Static|BindingFlags.NonPublic;
                            TestContext.WriteLine("ARTIFACT "+V.Str(pair.Key)+" layoutkit="+layout.GetString("kit_id")+" kitid="+kit.GetString("kit_id")+" kitpath="+reference.GetString("kit_path")+" layoutvalid="+typeof(SaveCommitCoordinator).GetMethod("ValidLayout",flags).Invoke(null,new object[]{layout})+" kitvalid="+typeof(SaveCommitCoordinator).GetMethod("ValidKit",flags).Invoke(null,new object[]{kit})+" kitjoin="+typeof(SaveCommitCoordinator).GetMethod("KitJoin",flags).Invoke(null,new object[]{layout,kit}));
                        }
                    }
                    DumpUnsupported(RunSnapshotAssembler.Build(session).ToDict(),"active");
                    DumpUnsupported(WorldSnapshotAssembler.Build(session).ToDict(),"world");
                    var a=RunSnapshotAssembler.Build(session).ToDict();var w=WorldSnapshotAssembler.Build(session).ToDict();
                    var domain=session.CapturePaidCraftingDomain();Assert.IsTrue(DomainBundle.TryCreate(domain,out _,out string reason),reason);
                    a.GetDictOrEmpty("crafting_summary")["paid_craft"]=PaidSnapshotCodec.Envelope(domain,false);
                    w.GetDictOrEmpty("home_ship").GetDictOrEmpty("crafting_summary")["paid_craft"]=PaidSnapshotCodec.Envelope(domain,false);
                    DumpPolicyFields(a,PaidSnapshotCodec.Policy.OrdinaryRun,"active");DumpPolicyFields(w,PaidSnapshotCodec.Policy.OrdinaryWorld,"world");
                    TestContext.WriteLine("POLICY active="+PaidSnapshotCodec.IsValidGraph(a,PaidSnapshotCodec.Policy.OrdinaryRun)+" active_version="+a.GetString("slice_version")+" world="+PaidSnapshotCodec.IsValidGraph(w,PaidSnapshotCodec.Policy.OrdinaryWorld)+" world_version="+w.GetString("slice_version")+" home_version="+w.GetDictOrEmpty("home_ship").GetString("slice_version"));
                }
                Assert.IsTrue(profileSaved,GdJson.Stringify(session.LastSaveResult));
                var selected=session.SaveLoadService.SelectGeneration("slot_01");Assert.IsTrue(selected.GetBool("ok"),GdJson.Stringify(selected));
                var reread=session.SaveLoadService.ReadGeneration(selected.GetString("run_id"),"slot_01",selected.GetString("generation_id"),selected.GetString("manifest_sha256"));
                Assert.IsTrue(reread.GetBool("ok"));Assert.IsTrue(PaidSnapshotCodec.Same(selected.Get("payloads"),reread.Get("payloads")));
                var payload=selected.GetDictOrEmpty("payloads");
                var withoutCapability=new SaveLoadService(rig.Storage,rig.Clock,false,true);
                Assert.IsFalse(withoutCapability.SelectGeneration("slot_01").GetBool("ok"),"baseline loader cannot activate a profile generation");
                var baselineCatalogRequest=payload.DeepCopy();baselineCatalogRequest.GetDictOrEmpty("compatibility").GetDictOrEmpty("profiles").Erase(FirstAwayGenerationInputs.Profile);
                var coordinator=new SaveCommitCoordinator(rig.Storage,SaveLoadService.PaidGenerationRoot,new GenerationAuthority(),session.SaveLoadService.ComponentCompatibility(),allowPaidCrafting:true);
                Assert.IsFalse(coordinator.ValidateSuppliedPayload(baselineCatalogRequest,selected.GetString("run_id"),"slot_01").GetBool("ok"),"baseline supplied catalog cannot authorize new profile artifacts");
                Assert.IsTrue(session.TravelHome());
                bool restored=session.ApplySelectedGeneration(selected);
                if(!restored)DumpSelectedStage(session,selected);
                Assert.IsTrue(restored,GdJson.Stringify(session.LastSaveResult));
                Assert.AreEqual(owner,session.CurrentShip.ShipId);Assert.AreEqual(marker,session.CurrentShip.MarkerId);
                Assert.AreEqual(descriptor,session.CurrentShip.Blueprint.FirstAwayDescriptorText);
                Assert.IsTrue(PaidSnapshotCodec.Same(inventory,session.InventoryState.GetSummary()));
                Assert.IsTrue(PaidSnapshotCodec.Same(progression,session.PlayerProgression.GetSummary()));
                var assembled=SavePayloadAssembler.Build(session,"slot_01","manual");Assert.IsTrue(assembled.GetBool("ok"),GdJson.Stringify(assembled));
                var next=assembled.GetDictOrEmpty("payloads");
                var references=payload.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty(owner);
                foreach(string role in new[]{"layout","gameplay_slice","kit","blueprint"})
                {
                    string path=references.GetString(role+"_path");
                    string raw=payload.GetArrayOrEmpty("artifacts").OfType<GdDict>().Single(a=>a.GetString("logical_path")==path).GetString("text");
                    string after=next.GetArrayOrEmpty("artifacts").OfType<GdDict>().Single(a=>a.GetString("logical_path")==path).GetString("text");
                    Assert.AreEqual(raw,after,"exact archived "+role+" bytes survive normal Continue");
                }
            }
            finally {session?.Dispose();CoreServices.Engine=engine;}
        }
        static void DumpSelectedStage(RunSession session,GdDict selected)
        {
            var payload=selected.GetDictOrEmpty("payloads");
            var worldDict=PaidSnapshotCodec.Parse(payload.GetString("world_text"),PaidSnapshotCodec.Policy.OrdinaryWorld);
            var world=WorldSnapshot.FromDict(worldDict,WorldSnapshot.WorldSliceVersion,session.Deps.Engine.VersionString);
            var archive=payload.GetArrayOrEmpty("artifacts").OfType<GdDict>().ToDictionary(a=>a.GetString("logical_path"));
            var flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.NonPublic;
            Action<string> phase=label=>
            {
                foreach(var pair in worldDict.GetDictOrEmpty("visited_ships"))
                {
                    var row=(GdDict)pair.Value;string owner=row.GetString("ship_id"),marker=V.Str(pair.Key);
                    var bp=ShipBlueprint.FromDict(row.GetDictOrEmpty("blueprint"));
                    var reference=payload.GetDictOrEmpty("binding").GetDictOrEmpty("ship_references").GetDictOrEmpty(owner);
                    var docs=new ShipDocuments{LayoutJson=archive[reference.GetString("layout_path")].GetString("text"),GameplaySliceJson=archive[reference.GetString("gameplay_slice_path")].GetString("text"),KitPath=reference.GetString("kit_path")};
                    docs.Layout=GdJson.ParseDict(docs.LayoutJson);string kitText=archive[docs.KitPath].GetString("text");docs.Kit=GdJson.ParseDict(kitText);
                    var inputs=new FirstAwayGenerationInputs(bp.SeedValue,world.WorldSummary.GetInt("world_seed"),bp.ShipSize,bp.ShipCondition,marker,owner,"breach_field","standard");
                    var descriptor=PaidSnapshotCodec.Parse(bp.FirstAwayDescriptorText);
                    string contractDir=ModularSocketCatalog.CONTRACTS_ROOT+ModularSocketCatalog.DEFAULT_KIT_ID;
                    bool directory=ProcgenCompat.ResDirExists(contractDir);
                    int fileCount=ProcgenCompat.ListResFiles(contractDir).Count;
                    bool socketKit=new ModularSocketCatalog().LoadKit("ship_structural_hazard");
                    bool pins=FirstAwaySalvageProfile.LoadPinnedCatalogs()!=null;
                    var canonical=new ShipGenerator().GenerateFirstAway(inputs);
                    var errorMember=CoreServices.Log.GetType().GetProperty("Errors");
                    var errors=errorMember?.GetValue(CoreServices.Log)??CoreServices.Log.GetType().GetField("Errors")?.GetValue(CoreServices.Log);
                    TestContext.WriteLine("STAGE_DIRECTORY "+label+" directory="+directory+" files="+fileCount+" socket_kit="+socketKit+" pins="+pins+" canonical="+(canonical!=null)+" errors="+(errors is System.Collections.IEnumerable sequence?string.Join(" | ",sequence.Cast<object>()):V.Str(errors)));
                    bool core=session.ShipGenerator.TryRestoreFirstAway(inputs,descriptor,docs.LayoutJson,docs.GameplaySliceJson,out var restored);
                    object[] args={docs,bp,marker,owner,inputs.WorldSeed,null,kitText};
                    bool validated=(bool)typeof(RunSession).GetMethod("ValidateFirstAwayDocuments",flags).Invoke(session,args);
                    TestContext.WriteLine("STAGE_PROFILE "+label+" owner="+owner+" source="+RunSession.FirstAwayMatchesSourceMarker(inputs)+" core="+core+" session="+validated+" kit_path="+docs.KitPath+" canonical_path="+(restored?.KitPath??"null")+" archive_kit_sha="+SaveGenerationArtifacts.Hash(kitText)+" optin="+session.EnableReviewedFirstAwayProfile);
                    foreach(var catalog in descriptor.GetDictOrEmpty("catalogs"))
                    {string resource=V.Str(catalog.Key);string text=CoreServices.Resources.ReadText(resource);TestContext.WriteLine("STAGE_CATALOG "+label+" "+resource+" expected="+((GdDict)catalog.Value).GetString("sha256")+" actual="+(text==null?"null":SaveGenerationArtifacts.Hash(text)));}
                }
                var roots=new System.Collections.Generic.Dictionary<string,IShipSceneRoot>();
                try{bool staged=(bool)typeof(RunSession).GetMethod("StageGenerationRoots",flags).Invoke(session,new object[]{payload,world,roots});TestContext.WriteLine("STAGE_ROOTS "+label+" ok="+staged+" built="+string.Join(",",roots.Keys));}
                finally{foreach(var root in roots.Values)session.Deps.ShipHost.FreeShipRoot(root);}
            };
            phase("outside");
            Assert.IsTrue(SaveGenerationArtifacts.TryCreateReader(selected,CoreServices.Resources,out var reader,out string reason,true,PaidSnapshotCodec.OrdinaryMode),reason);
            typeof(RunSession).GetMethod("WithArtifactReader",flags).MakeGenericMethod(typeof(bool)).Invoke(null,new object[]{reader,new Func<bool>(()=>{phase("inside");return true;})});
        }
        static void DumpPolicyFields(GdDict snapshot,PaidSnapshotCodec.Policy policy,string name)
        {
            var flags=BindingFlags.Static|BindingFlags.NonPublic;
            var shape=typeof(PaidSnapshotCodec).GetMethod("SnapshotShape",flags);
            var valid=typeof(PaidSnapshotCodec).GetMethod("ValidSnapshot",flags);
            TestContext.WriteLine("SHAPE "+name+" "+shape.Invoke(null,new object[]{snapshot,policy}));
            foreach(var field in snapshot)
            {
                object[] args={field.Value,policy,PaidSnapshotCodec.ChildPath(policy,PaidSnapshotCodec.RootPath(policy),V.Str(field.Key)),new System.Collections.Generic.HashSet<object>(),1,0};
                if(!(bool)valid.Invoke(null,args))TestContext.WriteLine("BAD_POLICY_FIELD "+name+"."+V.Str(field.Key)+" nodes="+args[5]);
            }
            object[] whole={snapshot,policy,PaidSnapshotCodec.RootPath(policy),new System.Collections.Generic.HashSet<object>(),0,0};
            TestContext.WriteLine("WHOLE "+name+" "+valid.Invoke(null,whole)+" nodes="+whole[5]);
        }
        static void DumpUnsupported(object value,string path)
        {
            if(value is GdDict dict){foreach(var p in dict){if(!(p.Key is string))TestContext.WriteLine("UNSUPPORTED_KEY "+path+" type="+p.Key.GetType().FullName+" value="+V.Str(p.Key));DumpUnsupported(p.Value,path+"."+V.Str(p.Key));}return;}
            if(value is GdArray array){for(int i=0;i<array.Count;i++)DumpUnsupported(array[i],path+"["+i+"]");return;}
            if(value==null||value is string||value is bool||value is long||value is double)return;
            TestContext.WriteLine("UNSUPPORTED_RAW "+path+" "+value.GetType().FullName+" "+V.Str(value));
        }
        [TestCase("valid")][TestCase("collision")][TestCase("noncanonical")][TestCase("unrelated")][TestCase("bad_progress")]
        public void RetainedObjectiveProjectionKeepsSourceAndRejectsAmbiguousOrUnrelatedKeys(string mutation)
        {
            var progress=new GdDict{{1L,new GdDict{{"completed_steps",0L},{"complete",false}}}};
            if(mutation=="collision")progress["1"]=progress.Get(1L);
            if(mutation=="noncanonical"){progress.Clear();progress["01"]=new GdDict();}
            var objective=new GdDict{{"progress",progress},{"cleared",false}};
            if(mutation=="bad_progress")objective["progress"]="forged_progress";
            var ships=new GdDict{{"0:0:0",new GdDict{{"objective",objective}}}};
            if(mutation=="unrelated")ships.GetDictOrEmpty("0:0:0")["other"]=new GdDict{{9L,"unrelated"}};
            var before=ships.DeepCopy();
            var method=typeof(SavePayloadAssembler).GetMethod("PaidRetainedPlacements",BindingFlags.Static|BindingFlags.NonPublic);
            if(mutation=="collision"||mutation=="noncanonical"||mutation=="bad_progress")
                Assert.Throws<TargetInvocationException>(()=>method.Invoke(null,new object[]{ships}));
            else
            {
                var projected=(GdDict)method.Invoke(null,new object[]{ships});
                Assert.IsTrue(projected.GetDictOrEmpty("0:0:0").GetDictOrEmpty("objective").GetDictOrEmpty("progress").Has("1"));
                Assert.AreEqual(mutation=="valid",PaidSnapshotCodec.IsValidGraph(projected));
            }
            Assert.IsTrue(V.VariantEquals(before,ships),"projection cannot rewrite runtime progress or source keys");
        }
        static bool Profile(GdDict world,GdDict active,GdDict bp,string owner,string layout,string gameplay,string kitPath,string kitText)
        {
            var method=typeof(SaveCommitCoordinator).GetMethod("ValidateFirstAwayProfile",BindingFlags.Static|BindingFlags.NonPublic);
            Assert.IsNotNull(method);return (bool)method.Invoke(null,new object[]{owner,world,active,bp,layout,gameplay,kitPath,kitText});
        }
        [TestCase("valid")][TestCase("missing")][TestCase("malformed")][TestCase("provider")][TestCase("catalog")]
        [TestCase("layout")][TestCase("gameplay")][TestCase("kit")][TestCase("kit_path")][TestCase("world")][TestCase("marker")][TestCase("source_size")][TestCase("source_condition")]
        public void DescriptorAdmissionBindsRawArtifactsActualCatalogsAndProductionMarker(string mutation)
        {
            long worldSeed=17;var marker=new MarkerGenerator().MarkersForCell(worldSeed,new Vec2i(0,0)).First();
            long size=marker.SizeClass,condition=marker.Condition;
            if(mutation=="source_size")size=(size+1)%3;
            if(mutation=="source_condition")condition=(condition+1)%3;
            string owner="ship_"+marker.MarkerId;
            var inputs=new FirstAwayGenerationInputs(42,worldSeed,size,condition,marker.MarkerId,owner,"breach_field","standard");
            var docs=new ShipGenerator().GenerateFirstAway(inputs);Assert.IsNotNull(docs,"authored profile for descriptor fixture");
            var descriptor=docs.FirstAwayDescriptor.Snapshot();
            if(mutation=="provider")descriptor["provider"]="forged_provider";
            if(mutation=="catalog")descriptor.GetDictOrEmpty("catalogs").Erase(descriptor.GetDictOrEmpty("catalogs").Keys.First());
            var bp=new GdDict{{"size",size},{"condition",condition},{"seed_value",42L},{"generation_profile",FirstAwayGenerationInputs.Profile},{"first_away_descriptor_text",PaidSnapshotCodec.Stringify(descriptor)}};
            if(mutation=="missing")bp.Erase("first_away_descriptor_text");
            if(mutation=="malformed")bp["first_away_descriptor_text"]="not-json";
            var retained=new GdDict{{"ship_id",owner},{"marker_id",marker.MarkerId},{"blueprint",bp}};
            var world=new GdDict{{"world_summary",new GdDict{{"world_seed",worldSeed}}},{"visited_ships",new GdDict{{marker.MarkerId,retained}}}};
            var active=new GdDict{{"world_seed",worldSeed}};
            if(mutation=="world"){active["world_seed"]=18L;world.GetDictOrEmpty("world_summary")["world_seed"]=18L;}
            if(mutation=="marker")retained["marker_id"]="1:0:0";
            string layout=docs.LayoutJson+(mutation=="layout"?" ":""),gameplay=docs.GameplaySliceJson+(mutation=="gameplay"?" ":"");
            Assert.AreEqual(mutation=="valid",Profile(world,active,bp,owner,layout,gameplay,docs.KitPath+(mutation=="kit_path"?".forged":""),CoreServices.Resources.ReadText(docs.KitPath)+(mutation=="kit"?" ":"")));
        }
    }
}
