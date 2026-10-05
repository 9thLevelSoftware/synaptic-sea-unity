using System;
using System.Globalization;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Private actual safe-EndTick source ownership. It is not a globally admitted save or a timing claim.
    internal sealed class OwnedContinuousCapture
    {
        readonly GdDict _run,_world,_references,_compatibility,_homeSystems,_homePlacement,_awaySystems;
        readonly GdArray _artifacts;
        internal readonly RunSession.ContinuousRuntimeProofCut ProofCut;
        internal readonly SaveLoadService.ContinuousCommitParent Parent;
        internal readonly string RunId,SlotId,SlotKind,CurrentOwner,PoseOwner,Generation,CaptureText;
        internal readonly long CaptureRevision,SavedAtEpoch;
        OwnedContinuousCapture(RunSession session,RunSession.ContinuousSafeEndTick ticket,SaveLoadService.ContinuousCommitParent parent,string slot,string kind)
        {
            session.RequireContinuousClosedCut(ticket);
            if(!parent.Matches(session.SaveLoadService,session.RunIdInternal,slot))throw new InvalidOperationException("continuous_parent_unbound");
            if(!session.TryCaptureContinuousRuntimeProofCut(ticket,out ProofCut,out string reason))throw new InvalidOperationException(reason);
            if(!ReferenceEquals(parent.Binding.Lease.Snapshot,ProofCut.Lifetime.ResourceLease.Snapshot)||!ProofCut.Lifetime.IsCurrent)throw new InvalidOperationException("continuous_cut_resource_mismatch");
            RunSnapshot run=RunSnapshotAssembler.BuildContinuousCut(session,ticket,parent);
            WorldSnapshot world=WorldSnapshotAssembler.BuildContinuousCut(session,ticket,parent);
            if(run==null||world==null||session.HomeShip==null||session.LifeboatShip==null)throw new InvalidOperationException("complete_world_unavailable");
            if(!session.BuildContinuousGenerationDocumentSet(world,ticket,out GdDict references,out GdArray artifacts,out reason))throw new InvalidOperationException(reason);
            CurrentOwner=world.CurrentLocation.Length==0?"ship_start":world.VisitedShips.GetDictOrEmpty(world.CurrentLocation).GetString("ship_id");
            PoseOwner=session.CurrentOccupancy?.ShipId??CurrentOwner;
            ShipInstance poseShip=session.FindShipByIdInternal(PoseOwner);
            if(CurrentOwner.Length==0||!references.Has(CurrentOwner)||poseShip?.SceneRoot==null||!references.Has(PoseOwner))throw new InvalidOperationException("continuous_pose_owner_missing");
            if(session.Scene!=null&&session.Scene.HasPlayer)
            {Vec3 local=SessionMath.AffineInverse(poseShip.SceneRoot.GlobalTransform)*session.Scene.PlayerPosition;world.PlayerPositionInShip=GdArray.Of((double)local.X,(double)local.Y,(double)local.Z);}
            world.AboardShipId=PoseOwner;
            _homeSystems=session.HomeShip.SystemsManager?.GetSummary()??new GdDict();
            _homePlacement=session.HomeShip.ComponentPlacementSummary.DeepCopy();
            _awaySystems=session.AwayFromStart?session.CurrentShip?.SystemsManager?.GetSummary()??new GdDict():new GdDict();
            _compatibility=parent.CopyCompatibility();
            RunId=session.RunIdInternal;SlotId=slot;SlotKind=kind;Parent=parent;
            CaptureRevision=session.NextCaptureRevision(parent.PriorCaptureRevision);
            CaptureText=CaptureRevision.ToString(CultureInfo.InvariantCulture);Generation=Guid.NewGuid().ToString("N");SavedAtEpoch=parent.SavedAtEpoch;
            var runDto=run.ToDict();var worldDto=world.ToDict();var homePlacementDto=_homePlacement;
            SavePayloadAssembler.NormalizeContinuousPlacementCut(runDto,worldDto,ref homePlacementDto);_homePlacement=homePlacementDto;
            var raw=new GdDict{{"run",runDto},{"world",worldDto},{"references",references},{"artifacts",artifacts},{"compatibility",_compatibility},{"home_systems",_homeSystems},{"home_placement",_homePlacement},{"away_systems",_awaySystems}};
            if(!ContinuousWholeSnapshotBudget.TryPreflightEnvelope(raw,out _,out _,out reason))throw new InvalidOperationException(reason);
            _run=runDto.DeepCopy();_world=worldDto.DeepCopy();_references=references.DeepCopy();_artifacts=artifacts.DeepCopy();
            _homeSystems=_homeSystems.DeepCopy();_homePlacement=_homePlacement.DeepCopy();_awaySystems=_awaySystems.DeepCopy();_compatibility=_compatibility.DeepCopy();
            session.RequireContinuousProofCutSource(ticket,ProofCut);
        }
        internal static bool TryCapture(RunSession session,RunSession.ContinuousSafeEndTick ticket,SaveLoadService.ContinuousCommitParent parent,string slot,string kind,out OwnedContinuousCapture captured,out string reason)
        {
            captured=null;reason="invalid_continuous_cut_request";
            if(session==null||parent==null||kind!=SaveLoadService.ComponentSlotKind(slot))return false;
            try{captured=new OwnedContinuousCapture(session,ticket,parent,slot,kind);reason="";return true;}
            catch(Exception failure){reason="continuous_cut_failed:"+failure.Message;return false;}
        }
        internal bool IsCurrent=>ProofCut.Lifetime.IsCurrent&&Parent.Policy.MatchesCurrentBinding(Parent.Binding);
        internal GdDict CopyRun()=>_run.DeepCopy();internal GdDict CopyWorld()=>_world.DeepCopy();
        internal GdDict CopyReferences()=>_references.DeepCopy();internal GdArray CopyArtifacts()=>_artifacts.DeepCopy();
        internal GdDict CopyCompatibility()=>_compatibility.DeepCopy();internal GdDict CopyHomeSystems()=>_homeSystems.DeepCopy();
        internal GdDict CopyHomePlacement()=>_homePlacement.DeepCopy();internal GdDict CopyAwaySystems()=>_awaySystems.DeepCopy();
    }
}
