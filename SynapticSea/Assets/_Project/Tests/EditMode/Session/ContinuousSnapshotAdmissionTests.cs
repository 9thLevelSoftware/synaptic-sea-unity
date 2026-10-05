using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class ContinuousSnapshotAdmissionTests:InfraDataTestBase
    {
        static void Fixture(out GdDict run,out GdDict world,out ProofResourceBinding binding,out ContinuousSaveReadPolicy policy)
        {
            // Admission UNIT fixture: actual stock roots + synthetic transcript; never a real world-cut claim.
            var f=new CheckpointProofAdmissionTests();var package=f.Fixture(false,out binding,out _,false,true);
            var wire=new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",ComponentDomainCodec.Encode(package,ComponentDomainCodec.BitExactSchema)}};
            Assert.IsTrue(ProofPackageCodec.TryOwnEnvelope(wire,out var input,out string reason),reason);
            Assert.IsTrue(CheckpointProofAdmission.TryAdmit(input,binding,out var paid,out reason),reason);
            Assert.IsTrue(PaidSnapshotCodec.TryCreateProofEnvelope(paid,true,out var envelope,out reason),reason);
            run=f.CopyCapturedRun();world=f.CopyCapturedWorld();var home=world.GetDictOrEmpty("home_ship");var owner=paid.CopyOwner();
            var encoded=ComponentDomainCodec.Encode(owner,ComponentDomainCodec.BitExactSchema);
            foreach(var root in new[]{run,home,world}){root["run_id"]=package.Get("run_id");root["generation_id"]="unit_generation";root["capture_revision"]="1";root["component_domain"]=encoded.DeepCopy();}
            run["slice_version"]=RunSnapshot.ComponentIntegrationVersion;home["slice_version"]=RunSnapshot.ComponentIntegrationVersion;world["slice_version"]=WorldSnapshot.ComponentIntegrationVersion;
            run.GetDictOrEmpty("crafting_summary")["paid_craft"]=envelope.DeepCopy();home.GetDictOrEmpty("crafting_summary")["paid_craft"]=envelope.DeepCopy();
            run["current_location"]=world.Get("current_location");home["current_location"]="";run["player_position"]=((GdArray)world.Get("player_position_in_ship")).DeepCopy();
            Assert.IsTrue(ContinuousSaveReadPolicy.TrySelectExplicitReader(binding,out policy,out reason),reason);
        }
        static bool Read(GdDict run,GdDict world,ProofResourceBinding binding,ContinuousSaveReadPolicy policy,out AdmittedContinuousSnapshots result,out string reason)
            =>ContinuousSnapshotAdmission.TryRead(PaidSnapshotCodec.Stringify(run,PaidSnapshotCodec.Policy.Raw,PaidHashContext.BitsV2),PaidSnapshotCodec.Stringify(world,PaidSnapshotCodec.Policy.Raw,PaidHashContext.BitsV2),binding,policy,out result,out reason);
        [Test]public void ActualComponentRootsAndOngoingProofAdmitAndCopiesRemainOwned()
        {
            Fixture(out var run,out var world,out var binding,out var policy);run.GetDictOrEmpty("vitals_summary")["health"]=System.BitConverter.Int64BitsToDouble(long.MinValue);
            Assert.IsTrue(Read(run,world,binding,policy,out var result,out string reason),reason);Assert.AreEqual("1",result.CaptureRevision);Assert.AreEqual(7,result.CopyOwner().GetInt("schema_version"));
            var copy=result.CopyRun();copy["run_id"]="changed";Assert.AreEqual(result.RunId,result.CopyRun().Get("run_id"));Assert.AreEqual(long.MinValue,System.BitConverter.DoubleToInt64Bits(result.CopyRun().GetDictOrEmpty("vitals_summary").GetFloat("health")));
        }
        [Test]public void MissingReaderPolicyOrChangedResourceRefuses(){Fixture(out var run,out var world,out var binding,out var policy);Assert.IsFalse(Read(run,world,binding,null,out _,out _));ResourceAuthorityPublication.Invalidate();Assert.IsFalse(Read(run,world,binding,policy,out _,out _));}
        [Test]public void HomeProofTamperAndComponentOwnerMismatchRefuse()
        {
            Fixture(out var run,out var world,out var binding,out var policy);world.GetDictOrEmpty("home_ship").GetDictOrEmpty("crafting_summary").GetDictOrEmpty("paid_craft")["save_mode"]="changed";Assert.IsFalse(Read(run,world,binding,policy,out _,out string reason));Assert.AreEqual("continuous_paid_mirror_mismatch",reason);
            Fixture(out run,out world,out binding,out policy);world["component_domain"]=ComponentDomainCodec.Encode(new GdDict{{"schema_version",7L}},ComponentDomainCodec.BitExactSchema);Assert.IsFalse(Read(run,world,binding,policy,out _,out reason));Assert.AreEqual("continuous_component_mirror_mismatch",reason);
        }
        [Test]public void UnknownRootMissingFieldAndPoseMismatchRefuse()
        {
            Fixture(out var run,out var world,out var binding,out var policy);run["unknown"]=true;Assert.IsFalse(Read(run,world,binding,policy,out _,out _));run.Erase("unknown");run.Erase("oxygen_summary");Assert.IsFalse(Read(run,world,binding,policy,out _,out _));
            Fixture(out run,out world,out binding,out policy);run["player_position"]=GdArray.Of(999.0,0.0,0.0);Assert.IsFalse(Read(run,world,binding,policy,out _,out _));
        }
        [Test]public void CallerCannotMintIssuerOrUseUnboundedImmutableText()
        {
            Assert.Throws<System.InvalidOperationException>(()=>new ContinuousSnapshotAdmission.Issuer(new object(),new GdDict(),new GdDict(),null));
            Fixture(out _,out _,out var binding,out var policy);Assert.IsFalse(ContinuousSnapshotAdmission.TryRead(new string('x',4194305),"{}",binding,policy,out _,out string reason));Assert.AreEqual("whole_snapshot_wire_bytes",reason);
        }
    }
}
