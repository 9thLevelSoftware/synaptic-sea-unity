#if SYNAPTIC_DOTNET_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Session
{
    // Explicit diagnostic via env+exact test filter. Driver frames are simulated60Hz, not Unity wall frames.
    [NonParallelizable]
    public class AuxiliaryContinuousMeasurementTests
    {
        const int MaximumFrames=20000, MaximumJobs=6, ChunkRecords=32;
        const double Delta=1.0/60;
        sealed class Row
        {
            internal int Frame,Job,Converted,CaptureUnits,ReplaySteps,Gc0,Gc1,Gc2,Queued,Retained,Active;
            internal long World,Work,Poll,Capture,Setup,Replay,Total,Allocated,OutputBytes,HashBytes,Accepted,ProjectionRetainedUnits,HeapBytes,FinalizationTicks;
            internal double Health,Stamina,Hunger,Thirst;
            internal bool Pressure,Finalization,Rest,Cancelled;
            internal string WorkStatus="none";
            internal GdDict Json()=>new GdDict{{"frame",Frame},{"job",Job},{"work_status",WorkStatus},{"digest_cancelled",Cancelled},{"rest",Rest},{"projection_retained_units",ProjectionRetainedUnits},{"managed_heap_bytes",HeapBytes},{"phase",Job==0?"cold":"warm"},{"world_ticks",World},{"work_ticks",Work},
                {"poll_ticks",Poll},{"capture_ticks",Capture},{"replay_setup_ticks",Setup},{"replay_ticks",Replay},{"total_ticks",Total},{"allocated_bytes",Allocated},
                {"gc0",Gc0},{"gc1",Gc1},{"gc2",Gc2},{"converted_records",Converted},{"capture_units",CaptureUnits},{"replay_records",ReplaySteps},
                {"digest_output_bytes",OutputBytes},{"digest_hash_bytes",HashBytes},{"finalization_step_ticks",FinalizationTicks},{"finalization_allowance",Finalization},{"queue_pressure",Pressure},
                {"accepted",Accepted},{"active",Active},{"queued",Queued},{"retained_chunks",Retained},{"health",Health},{"stamina",Stamina},{"hunger",Hunger},{"thirst",Thirst}};
        }
        sealed class Job : IDisposable
        {
            internal readonly UntrustedAuxReplaySeed Seed;
            internal readonly AuxiliaryWorkRuntime Runtime;
            internal readonly DiagnosticAuxiliaryPairContext Context;
            internal readonly List<UntrustedAuxEvidenceChunk> Proofs=new List<UntrustedAuxEvidenceChunk>(64);
            internal AuxEvidenceReplayCursor Replay;
            internal int Sealed,PressureFrames,WorldFrames,CancelledDigests,ReplaySetupScanned;
            internal bool ReplayDone;
            AuxiliarySealedEvidenceChunk _source;
            UntrustedAuxEvidenceStep[] _converted;
            int _copied;
            UntrustedAuxEvidenceChunk _unsigned;
            UntrustedAuxChunkDigestCursor _digest;
            string _previous;
            readonly bool _cancelOnce;
            internal Job(int index,double duration,bool tiny,DiagnosticVitalsProjectionAdapter vitals,InventoryState inventory,ResourceAuthorityLease lease,bool cancel)
            {
                double progress=tiny?duration-1e-12:0;
                Seed=new UntrustedAuxReplaySeed("diag-run-"+index,"actor","maintenance_fabricator_feed_01",new string('a',64),duration,progress,progress,0);
                _previous=Seed.OriginDigest;_cancelOnce=cancel;
                var values=vitals.Read().Values;
                Context=new DiagnosticAuxiliaryPairContext(0,new AuxiliaryWorkFrame(Delta,values.Stamina,values.MaxStamina,1),inventory,lease);
                Runtime=AuxiliaryWorkRuntime.CreateActualVitalsPaired("diagnostic-home",0,PaidHashContext.BitsV2.Algorithm,Seed,ChunkRecords,new AuxiliaryEvidenceLimits(64,3000,2),vitals,Context);
            }
            internal bool Complete=>Runtime.Snapshot().ProgressSeconds==Seed.Duration;
            internal bool PipeIdle=>_source==null&&_digest==null&&_unsigned==null;
            internal void Custody(Row row)
            {
                if(Runtime.PendingEvidence!=null)
                {
                    var exact=Runtime.PendingEvidence;var status=Runtime.EvidenceJournal.TryTakeCustody(exact,out var ack);
                    if(status==AuxiliaryCustodyStatus.QueueFull){PressureFrames++;row.Pressure=true;Assert.AreSame(exact,Runtime.PendingEvidence);}
                    else {Assert.AreEqual(AuxiliaryCustodyStatus.Accepted,status);Assert.IsTrue(Runtime.TryAcknowledgeEvidenceCustody(ack));}
                }
                if(Runtime.PendingEvidence==null&&Runtime.RetainedStepCount>0&&(Runtime.RetainedStepCount==ChunkRecords||Complete))
                {Assert.IsTrue(Runtime.TryPrepareEvidenceRotation(out var rotation,out string reason),reason);Assert.IsTrue(Runtime.TryInstallEvidenceRotation(rotation));Sealed++;}
            }
            internal void Poll(Row row)
            {
                if(_source==null&&_unsigned==null&&_digest==null&&Runtime.EvidenceJournal.TryAcquireQueuedChunk(out _source))
                {_converted=new UntrustedAuxEvidenceStep[_source.Count];_copied=0;}
                if(_source!=null)
                {
                    int limit=Math.Min(_source.Count,_copied+32);
                    while(_copied<limit)
                    {
                        var s=_source.At(_copied);double ratio=Math.Max(0,Math.Min(1,s.StaminaBefore/Math.Max(1,s.MaxStamina)));
                        _converted[_copied++]=new UntrustedAuxEvidenceStep(s.Sequence,s.RequestedDelta,s.StaminaBefore,s.MaxStamina,s.WoundSpeed,
                            ratio,s.Speed,Seed.Duration-s.ProgressBefore,s.ElapsedSeconds,s.DeltaSeconds,s.StaminaAfter,s.ProgressAfter,s.EligibleAfter);row.Converted++;
                    }
                    if(_copied==_source.Count)
                    {_unsigned=Chunk(_source,_converted,_previous,new string('0',64));_source=null;}
                }
                if(_unsigned==null)return;
                if(_digest==null)Assert.IsTrue(UntrustedAuxChunkDigestCursor.Begin(_unsigned,out _digest,out string reason),reason);
                long output=_digest.OutputBytes,hash=_digest.HashedBytes;bool final=_digest.AwaitingFinalization;row.Finalization|=final;
                long digestTick=Stopwatch.GetTimestamp();var status=_digest.Step(256,2048,2048,final);if(final)row.FinalizationTicks+=Stopwatch.GetTimestamp()-digestTick;row.OutputBytes+=_digest.OutputBytes-output;row.HashBytes+=_digest.HashedBytes-hash;
                Assert.AreNotEqual(DigestCursorStatus.Rejected,status,_digest.Reason);
                if(_cancelOnce&&CancelledDigests==0&&status==DigestCursorStatus.Pending)
                {_digest.Cancel();_digest.Dispose();_digest=null;CancelledDigests++;row.Cancelled=true;return;}
                if(status==DigestCursorStatus.UntrustedResult)
                {
                    string digest=_digest.Result.Digest;
                    var proof=new UntrustedAuxEvidenceChunk(_unsigned.Ordinal,_previous,digest,_unsigned.InitialSequence,_unsigned.ProgressBefore,_unsigned.EligibleBefore,
                        _unsigned.AcceptedBefore,_unsigned.ProgressAfter,_unsigned.EligibleAfter,_unsigned.AcceptedAfter,_converted);
                    Proofs.Add(proof);_previous=digest;_digest.Dispose();_digest=null;_unsigned=null;_converted=null;
                }
            }
            static UntrustedAuxEvidenceChunk Chunk(AuxiliarySealedEvidenceChunk c,UntrustedAuxEvidenceStep[] steps,string previous,string digest)
                =>new UntrustedAuxEvidenceChunk(c.Ordinal,previous,digest,c.FirstSequence,c.ProgressBefore,c.EligibleBefore,c.FirstSequence-1,c.ProgressAfter,c.EligibleAfter,c.LastSequence,steps);
            internal void ReplayFrame(Row row)
            {
                if(!Complete||!PipeIdle||Runtime.PendingEvidence!=null||Runtime.RetainedStepCount!=0||Proofs.Count!=Sealed)return;
                if(Replay==null)
                {
                    long start=Stopwatch.GetTimestamp();Assert.IsTrue(AuxEvidenceReplayCursor.Begin(Seed,Proofs.ToArray(),out Replay,out string reason),reason);
                    row.Setup=Stopwatch.GetTimestamp()-start;ReplaySetupScanned=Runtime.EvidenceJournal.AcceptedStepCount;
                }
                long before=Replay.ReplayedStepCount,output=Replay.DigestOutputBytes,hash=Replay.DigestHashBytes;bool final=Replay.DigestAwaitingFinalization;
                long tick=Stopwatch.GetTimestamp();var status=Replay.Step(32,256,2048,2048,final);row.Replay=Stopwatch.GetTimestamp()-tick;if(final)row.FinalizationTicks+=row.Replay;
                row.ReplaySteps=(int)(Replay.ReplayedStepCount-before);row.OutputBytes+=Replay.DigestOutputBytes-output;row.HashBytes+=Replay.DigestHashBytes-hash;row.Finalization|=final;
                Assert.AreNotEqual(AuxReplayStatus.Rejected,status,Replay.Reason);
                if(status==AuxReplayStatus.UntrustedResult)
                {Assert.AreEqual(Seed.Duration,Replay.ArithmeticResult.Progress);Assert.AreEqual(Runtime.Snapshot().EligibleSteps,Replay.ArithmeticResult.AcceptedSteps);ReplayDone=true;}
            }
            public void Dispose(){_digest?.Dispose();Replay?.Cancel();}
        }
        sealed class Capture : IDisposable
        {
            internal ProjectionPin Pin;
            internal ProjectionCaptureCursor Cursor;
            internal GdDict Expected;
            internal ulong Stamp;
            internal int Started;
            public void Dispose(){Cursor?.Dispose();Pin?.Dispose();}
        }
        [Test]
        public void MeasureActualWorldAndPairedWorkWithBoundedProofAndHistoricalCapture()
        {
            string path=Environment.GetEnvironmentVariable("SYNAPTIC_AUX_MEASUREMENT_JSON");
            if(string.IsNullOrWhiteSpace(path))Assert.Ignore("Diagnostic requires explicit SYNAPTIC_AUX_MEASUREMENT_JSON output path.");
            long driverSetup=Stopwatch.GetTimestamp();var original=ResourceAuthorityPublication.Reader;
            var rows=new Row[MaximumFrames];var jobs=new GdArray();int used=0,captures=0,pressureWorld=0,cancelWorld=0,replayWorld=0;
            try
            {
                ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(new Dictionary<string,string>()));
                Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var lease,out _));var inventory=InventoryState.CreateTracked(new GdDict());
                using var vitals=new DiagnosticVitalsProjectionAdapter(new DiagnosticVitalsValues());
                // Read-only test instrumentation; no private model access/mutation or production API change.
                var registry=(ParticipantProjectionRegistry)typeof(DiagnosticVitalsProjectionAdapter).GetField("_registry",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(vitals);
                driverSetup=Stopwatch.GetTimestamp()-driverSetup;
                Capture capture=null;long frameOrdinal=0;
                try
                {
                    for(int index=0;index<MaximumJobs;index++)
                    {
                        // Real idle recovery between jobs. Never reset/grant the shared actual Vitals model.
                        int rest=0;
                        while(vitals.Read().Values.Stamina<90&&rest++<1200)
                        {
                            Assert.Less(used,MaximumFrames);long allocated=GC.GetAllocatedBytesForCurrentThread();int g0=GC.CollectionCount(0),g1=GC.CollectionCount(1),g2=GC.CollectionCount(2);
                            long total=Stopwatch.GetTimestamp();var row=new Row{Frame=used,Job=index,Rest=true};long tick=Stopwatch.GetTimestamp();Tick(vitals);row.World=Stopwatch.GetTimestamp()-tick;
                            var values=vitals.Read().Values;row.Health=values.Health;row.Stamina=values.Stamina;row.Hunger=values.At(4);row.Thirst=values.At(6);
                            row.ProjectionRetainedUnits=registry.RetainedUnits;row.HeapBytes=GC.GetTotalMemory(false);row.Allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
                            row.Gc0=GC.CollectionCount(0)-g0;row.Gc1=GC.CollectionCount(1)-g1;row.Gc2=GC.CollectionCount(2)-g2;row.Total=Stopwatch.GetTimestamp()-total;rows[used++]=row;frameOrdinal++;
                        }
                        Assert.Less(rest,1200,"ordinary recovery bound");
                        using var job=new Job(index,index%2==0?8:12,index>=4,vitals,inventory,lease,index==1);
                        int local=0;
                        while(!job.ReplayDone)
                        {
                            Assert.Less(used,MaximumFrames);Assert.Less(local++,4000,"finite job driver bound");
                            long allocated=GC.GetAllocatedBytesForCurrentThread();int gc0=GC.CollectionCount(0),gc1=GC.CollectionCount(1),gc2=GC.CollectionCount(2);
                            long total=Stopwatch.GetTimestamp();var row=new Row{Frame=used,Job=index};long tick=Stopwatch.GetTimestamp();
                            ulong beforeStamp=vitals.Read().Stamp;Tick(vitals);job.WorldFrames++;
                            Assert.Greater(vitals.Read().Stamp,beforeStamp);row.World=Stopwatch.GetTimestamp()-tick;
                            tick=Stopwatch.GetTimestamp();
                            if(!job.Complete)
                            {
                                var values=vitals.Read().Values;
                                job.Context.Replace(frameOrdinal,new AuxiliaryWorkFrame(Delta,values.Stamina,values.MaxStamina,1),inventory,lease);
                                if(job.Runtime.TryPrepareActualPair(out var pair,out _,out string reason))
                                {row.WorkStatus="accepted";using(pair){using(var attempt=CommonParticipantGate.BeginAttempt())Assert.IsTrue(pair.TryInstallUnderGate(job.Runtime,attempt,out reason),reason);pair.NotifyAfterGate();}}
                                else {row.WorkStatus=reason;Assert.AreEqual("step_log_full",reason,"Only intentional active-buffer pressure may stop work.");row.Pressure=true;}
                            }
                            job.Custody(row);row.Work=Stopwatch.GetTimestamp()-tick;
                            tick=Stopwatch.GetTimestamp();
                            // Bounded first-job pause forces actual queue pressure; world/work frames continue.
                            if(index!=0||local>180)job.Poll(row);
                            row.Poll=Stopwatch.GetTimestamp()-tick;
                            job.ReplayFrame(row);
                            tick=Stopwatch.GetTimestamp();
                            if(capture==null&&used%120==0)
                            {
                                var snapshot=vitals.Read();capture=new Capture{Expected=snapshot.Values.ExactScratch().GetSummary(),Stamp=snapshot.Stamp,Started=used};
                                Assert.IsTrue(vitals.TryPinPartialDiagnostic(out capture.Pin,out string reason),reason);
                                Assert.IsTrue(capture.Pin.TryCreateCursor(out capture.Cursor,out reason),reason);
                            }
                            if(capture!=null)
                            {
                                var status=capture.Cursor.Advance(1);row.CaptureUnits=capture.Cursor.LastWorkUnits;Assert.LessOrEqual(row.CaptureUnits,8);
                                if(status==ProjectionCursorStatus.Complete)
                                {
                                    using(var result=capture.Cursor.Result)
                                    {
                                        Assert.AreEqual(1,result.NodeCount);Assert.AreEqual(21,result.Node(0).EntryCount);int i=0;
                                        foreach(var entry in capture.Expected){var exact=result.Entry(0,i++);Assert.AreEqual(entry.Key,exact.Key.ToNormalized());Assert.IsTrue(ProjectionScalar.FromNormalized(entry.Value).Equals(exact.Value.Scalar));}
                                        Assert.IsFalse(result.TryBuildWholeWorldSave(out _));
                                    }
                                    Assert.Greater(used,capture.Started);Assert.Greater(vitals.Read().Stamp,capture.Stamp);
                                    capture.Dispose();capture=null;captures++;
                                }
                                else Assert.AreEqual(ProjectionCursorStatus.Pending,status,capture.Cursor.Reason);
                            }
                            row.Capture=Stopwatch.GetTimestamp()-tick;
                            var view=job.Runtime.CaptureActualPair();row.Accepted=view.AcceptedSteps;row.Active=view.ActiveSteps;
                            row.Queued=job.Runtime.EvidenceJournal.QueuedChunkCount;row.Retained=job.Runtime.EvidenceJournal.RetainedChunkCount;
                            row.Health=view.Vitals.Values.Health;row.Stamina=view.Vitals.Values.Stamina;row.Hunger=view.Vitals.Values.At(4);row.Thirst=view.Vitals.Values.At(6);
                            Assert.LessOrEqual(row.Converted,32);Assert.LessOrEqual(row.ReplaySteps,32);Assert.LessOrEqual(row.OutputBytes,4096);Assert.LessOrEqual(row.HashBytes,4096);
                            if(row.Pressure)pressureWorld++;if(job.CancelledDigests>0)cancelWorld++;if(job.Replay!=null)replayWorld++;
                            row.ProjectionRetainedUnits=registry.RetainedUnits;row.HeapBytes=GC.GetTotalMemory(false);
                            row.Allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;row.Gc0=GC.CollectionCount(0)-gc0;row.Gc1=GC.CollectionCount(1)-gc1;row.Gc2=GC.CollectionCount(2)-gc2;
                            row.Total=Stopwatch.GetTimestamp()-total;rows[used++]=row;frameOrdinal++;
                        }
                        var final=job.Runtime.CaptureActualPair();long kept=0;
                        for(int c=0;c<job.Sealed;c++){var chunk=job.Runtime.EvidenceJournal.ReadRetainedChunk(c);kept+=chunk.Count;}
                        Assert.AreEqual(final.AcceptedSteps,kept);Assert.AreEqual(final.AcceptedSteps,job.Replay.ArithmeticResult.AcceptedSteps);
                        jobs.Append(new GdDict{{"job",index},{"duration",job.Seed.Duration},{"tiny_remainder",index>=4},{"world_frames",job.WorldFrames},
                            {"accepted",final.AcceptedSteps},{"retained_accepted",kept},{"chunks",job.Sealed},{"cancelled_digest_restarts",job.CancelledDigests},
                            {"queue_pressure_frames",job.PressureFrames},{"replay_begin_scanned_records",job.ReplaySetupScanned},{"source_nodes",final.Budget.SourceNodes},
                            {"wire_nodes",final.Budget.WireNodes},{"utf8_upper_bound",final.Budget.Utf8Bytes},{"replay_digest_matches",job.Replay.ArithmeticResult.MatchedChunkDigestCount},
                            {"projection_retained_units_after_job",registry.RetainedUnits},{"ordinary_idle_rest_frames",rest}});
                    }
                    Assert.Greater(captures,0);Assert.Greater(pressureWorld,0);Assert.Greater(cancelWorld,0);Assert.Greater(replayWorld,0);
                    Assert.Less(vitals.Read().Values.At(4),100);Assert.Less(vitals.Read().Values.At(6),100);
                    // Actual death refusal is diagnostic damage input, never survival disable or a reward test.
                    Assert.IsTrue(vitals.PrepareDamageHealthAfter(0,out var damage,out string deathReason),deathReason);
                    using(damage)Assert.IsTrue(damage.CommitStandalone(out deathReason),deathReason);
                    using var dead=new Job(6,8,false,vitals,inventory,lease,false);var beforeDead=vitals.Read();
                    Assert.IsFalse(dead.Runtime.TryPrepareActualPair(out _,out _,out deathReason));Assert.AreEqual("dead_actual_vitals",deathReason);
                    Assert.AreEqual(beforeDead.Stamp,vitals.Read().Stamp);Assert.AreEqual(0,dead.Runtime.Snapshot().EligibleSteps);
                    var raw=new GdArray();for(int i=0;i<used;i++)raw.Append(rows[i].Json());
                    var artifact=new GdDict{{"kind","intermediate_actual_auxiliary_measurement_v1"},{"simulation_hz",60},{"wall_clock_continuous_simulation",false},
                        {"unity_frame_claim",false},{"ordinary_save_authority",false},{"paid_reward_authority",false},{"stopwatch_frequency",Stopwatch.Frequency},
                        {"assembly_mvid",typeof(AuxiliaryContinuousMeasurementTests).Assembly.ManifestModule.ModuleVersionId.ToString()},{"source_manifest",Environment.GetEnvironmentVariable("SYNAPTIC_AUX_MEASUREMENT_SOURCE_MANIFEST")??"unspecified"},{"digest_token_allowance",256},{"digest_output_allowance",2048},{"digest_hash_allowance",2048},{"digest_tokens_actually_consumed","not_exposed_by_cursor"},{"driver_setup_ticks",driverSetup},{"raw_frames",used},{"captures",captures},{"pressure_world_frames",pressureWorld},{"cancel_world_frames",cancelWorld},
                        {"replay_world_frames",replayWorld},{"death_refusal",deathReason},{"jobs",jobs},{"summary",Summary(rows,used)},{"rows",raw}};
                    long serialize=Stopwatch.GetTimestamp();string text=GdJson.Stringify(artifact);long serializationTicks=Stopwatch.GetTimestamp()-serialize;
                    // Serialization timing is a sidecar so measuring it does not require serializing the artifact twice.
                    path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,text);
                    File.WriteAllText(path+".serialization.json",GdJson.Stringify(new GdDict{{"serialization_ticks",serializationTicks},{"characters",text.Length},{"stopwatch_frequency",Stopwatch.Frequency}}));
                    TestContext.Progress.WriteLine("Intermediate diagnostic frames="+used+" captures="+captures+" artifact="+path);
                }
                finally{capture?.Dispose();}
            }
            catch(Exception error)
            {
                var partial=new GdArray();for(int i=0;i<used;i++)partial.Append(rows[i].Json());
                string failedPath=Path.GetFullPath(path)+".failed.json";Directory.CreateDirectory(Path.GetDirectoryName(failedPath));
                File.WriteAllText(failedPath,GdJson.Stringify(new GdDict{{"kind","incomplete_diagnostic_measurement"},{"error",error.ToString()},{"completed_rows",used},{"jobs",jobs},{"rows",partial}}));throw;
            }
            finally{ResourceAuthorityPublication.ReplaceReader(original);}
        }
        static void Tick(DiagnosticVitalsProjectionAdapter vitals)
        {Assert.IsTrue(vitals.PrepareTick(Delta,new DiagnosticVitalsTickInput(moving:false),out var tick,out string reason),reason);using(tick)Assert.IsTrue(tick.CommitStandalone(out reason),reason);}
        static GdDict Summary(Row[] rows,int count)
        {
            var result=new GdDict();
            foreach(bool cold in new[]{true,false})
            {
                var subset=rows.Take(count).Where(r=>(r.Job==0)==cold).ToArray();
                var stages=new Dictionary<string,Func<Row,long>>{{"world",r=>r.World},{"work",r=>r.Work},{"poll",r=>r.Poll},{"capture",r=>r.Capture},{"replay_setup",r=>r.Setup},{"replay",r=>r.Replay},{"total",r=>r.Total},{"finalization_step",r=>r.FinalizationTicks}};
                var block=new GdDict();foreach(var stage in stages){var sorted=subset.Select(stage.Value).OrderBy(v=>v).ToArray();var active=sorted.Where(v=>v>0).ToArray();block[stage.Key]=new GdDict{{"samples",sorted.Length},{"min_ticks",sorted[0]},{"nonzero_samples",active.Length},{"nonzero_median_ticks",active.Length==0?0:active[active.Length/2]},{"median_ticks",sorted[sorted.Length/2]},{"p95_ticks",sorted[(int)((sorted.Length-1)*.95)]},{"p99_ticks",sorted[(int)((sorted.Length-1)*.99)]},{"max_ticks",sorted[sorted.Length-1]}};}
                block["allocated_bytes"]=subset.Sum(r=>r.Allocated);block["gc0"]=subset.Sum(r=>r.Gc0);block["gc1"]=subset.Sum(r=>r.Gc1);block["gc2"]=subset.Sum(r=>r.Gc2);result[cold?"cold":"warm"]=block;
            }
            return result;
        }
    }
}

#endif
