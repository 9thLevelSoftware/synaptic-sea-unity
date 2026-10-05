using System;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public sealed class AuxiliaryCheckpointExportTests : InfraDataTestBase
    {
        [Test] public void AdmittedResumeImportsWholePrefixWithoutDebitRequiresConsentAndExportsNextActualPairCut()
        {
            var deps=SessionHarness.GoldenDeps(out _);SessionHarness.OverlayGamePlayability(deps);
            var session=RunSession.Create(deps);
            try
            {
                Assert.IsTrue(session.PlayableStarted,session.LastFailureReason);
                // Isolated importer unit setup for already-restored Vitals, not an ordinary Save witness.
                session.VitalsState.Stamina=73;
                var fixture=new CheckpointProofAdmissionTests();var package=fixture.Fixture(false,out var binding,out _);
                var outer=new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",ComponentDomainCodec.Encode(package,ComponentDomainCodec.BitExactSchema)}};
                Assert.IsTrue(ProofPackageCodec.TryOwnEnvelope(outer,out var owned,out string reason),reason);
                Assert.IsTrue(CheckpointProofAdmission.TryAdmit(owned,binding,out var admitted,out reason),reason);
                Assert.IsTrue(AdmittedAuxiliaryHistory.TryIssueResume(admitted,out var history,out reason),reason);
                using(var vitals=new ActualVitalsProjectionBridge(session))
                {
                    var inventory=InventoryState.CreateTracked(new GdDict());
                    var context=new DiagnosticAuxiliaryPairContext(0,new AuxiliaryWorkFrame(.1,73,session.VitalsState.MaxStamina,1,consent:false),inventory,binding.Lease);
                    var runtime=AuxiliaryWorkRuntime.CreateFromAdmittedRunSessionHistory("home",PaidHashContext.BitsV2.Algorithm,history,256,new AuxiliaryEvidenceLimits(64,2000,8),vitals,context);
                    Assert.AreEqual(73,session.VitalsState.Stamina,"History import never reapplies old stamina costs");
                    Assert.AreEqual(history.AcceptedSteps,runtime.Snapshot().EligibleSteps);Assert.AreEqual(history.Progress,runtime.Snapshot().ProgressSeconds);
                    Assert.IsFalse(runtime.TryPrepareRunSessionPair(out _,out _,out _));Assert.AreEqual(history.Progress,runtime.Snapshot().ProgressSeconds);
                    context.Replace(1,new AuxiliaryWorkFrame(.1,73,session.VitalsState.MaxStamina,1),inventory,binding.Lease);
                    Assert.IsTrue(runtime.TryPrepareRunSessionPair(out var pair,out var step,out reason),reason);
                    using(pair)using(var attempt=CommonParticipantGate.BeginAttempt())Assert.IsTrue(pair.TryInstallUnderGate(runtime,attempt,out reason),reason);
                    Assert.AreEqual(history.AcceptedSteps+1,runtime.Snapshot().EligibleSteps);Assert.AreEqual(step.StaminaAfter,session.VitalsState.Stamina);
                    Assert.IsTrue(runtime.TryPrepareEvidenceRequestCut(runtime.EvidenceJournal.StructuralVersion,out var prepared,out reason),reason);
                    AuxiliaryWorkRuntime.EvidenceExportPin pin;
                    using(var attempt=CommonParticipantGate.BeginAttempt())
                    {Assert.IsTrue(prepared.TryInstallTransportUnderGate(out _));pin=runtime.PinInstalledEvidenceCutUnderGate();}
                    Assert.IsTrue(pin.IsProducer(runtime));Assert.AreEqual(history.ChunkCount+1,pin.ChunkCount);
                    Assert.IsTrue(AuxiliaryCheckpointExport.TryExport(pin,history,admitted.OwnedDomainForReplacement,admitted,binding,"resume-next",out var next,out reason),reason);
                    Assert.IsTrue(AdmittedAuxiliaryHistory.TryIssueResume(next,out var nextHistory,out reason),reason);
                    Assert.AreEqual(history.AcceptedSteps+1,nextHistory.AcceptedSteps);Assert.AreEqual(history.OriginalSeed.OriginDigest,nextHistory.OriginalSeed.OriginDigest);
                    Assert.AreEqual(history.ChunkAt(0).Digest,nextHistory.ChunkAt(0).Digest);Assert.AreEqual(runtime.Snapshot().ProgressSeconds,nextHistory.Progress);
                    Assert.IsTrue(ProofGenerationComparison.TryCompare(admitted,next,out var compatibility,out reason),reason);Assert.IsFalse(compatibility.RequiresOutputSourceBinding);
                    Assert.IsFalse(AuxiliaryCheckpointExport.TryExport(pin,null,admitted.OwnedDomainForReplacement,admitted,binding,"refuse",out _,out _));
                    ResourceAuthorityPublication.Invalidate();Assert.IsFalse(AuxiliaryCheckpointExport.TryExport(pin,history,admitted.OwnedDomainForReplacement,admitted,binding,"stale",out _,out _));
                }
            }
            finally {session.Dispose();}
        }
    }
}
