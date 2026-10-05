using System;
using System.Collections.Generic;
using System.Threading;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    public partial class SaveLoadService
    {
        sealed class ContinuousWorkerTerminalEpoch
        {
            int _retired;
            internal bool Retired => Volatile.Read(ref _retired)!=0;
            internal void Retire()=>Interlocked.Exchange(ref _retired,1);
        }
        // Main-thread-only map. Workers retain just their immutable adapter and atomic epoch.
        readonly Dictionary<string,ContinuousWorkerTerminalEpoch> _continuousWorkerEpochs=new Dictionary<string,ContinuousWorkerTerminalEpoch>(StringComparer.Ordinal);
        void RetireContinuousWorkerRun(string run)
        {if(_continuousWorkerEpochs.TryGetValue(run,out var epoch))epoch.Retire();}
        SaveCommitCoordinator CreateContinuousWorkerCoordinator(string run)
        {
            if(!_continuousWorkerEpochs.TryGetValue(run,out var epoch))
            {_continuousWorkerEpochs.Add(run,epoch=new ContinuousWorkerTerminalEpoch());}
            if(_componentTerminalRuns.Contains(run))epoch.Retire();
            var authority=new ContinuousWorkerTerminalAuthority(Storage,run,Admission(run),AdmissionVersion,CreationKind,
                PaidCraftingEnabled,_authorizedDiagnosticNewRun==run,epoch);
            return SaveCommitCoordinator.CreateContinuousReader(Storage,GenerationRoot,authority,ComponentCompatibility(),
                _continuousReaderBinding,_continuousReaderPolicy);
        }
        sealed class ContinuousWorkerTerminalAuthority : ISaveGenerationTerminalAuthority
        {
            readonly IStorage _storage;
            readonly string _run,_admission,_version,_creation;
            readonly bool _paid,_explicitNew;
            readonly ContinuousWorkerTerminalEpoch _epoch;
            internal ContinuousWorkerTerminalAuthority(IStorage storage,string run,string admission,string version,string creation,
                bool paid,bool explicitNew,ContinuousWorkerTerminalEpoch epoch)
            {_storage=storage;_run=run;_admission=admission;_version=version;_creation=creation;_paid=paid;_explicitNew=explicitNew;_epoch=epoch;}
            public GdDict Query(string run,string slot)
            {
                GdDict Answer(bool ok,string status)=>new GdDict{{"ok",ok},{"run_id",run},{"slot_id",slot},{"status",status},{"legacy_witnesses",new GdArray()}};
                if(run!=_run)return Answer(false,"unbound");
                if(_epoch.Retired)return Answer(true,"terminal");
                bool admitted=_explicitNew;
                if(_storage.FileExists(_admission))
                {
                    string text=_storage.ReadText(_admission);
                    var declaration=_paid?PaidSnapshotCodec.Parse(text):GdJson.ParseString(text) as GdDict;
                    if(declaration==null||declaration.GetString("schema_version")!=_version||declaration.GetString("run_id")!=run||declaration.GetString("creation_kind")!=_creation)
                        return Answer(false,"ambiguous");
                    admitted=true;
                }
                if(!admitted)return Answer(false,"unbound");
                string status=OriginalRunAuthorityFromStorage(_storage,run);
                return Answer(true,_epoch.Retired?"terminal":status);
            }
        }
    }
}
