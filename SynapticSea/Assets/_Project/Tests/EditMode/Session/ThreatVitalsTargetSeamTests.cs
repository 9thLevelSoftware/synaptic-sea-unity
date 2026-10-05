using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using SynapticSea.Tests.Systems;
namespace SynapticSea.Tests.Session
{
#if SYNAPTIC_DOTNET_TESTS
    [NonParallelizable]
#endif
    public class ThreatVitalsTargetSeamTests : InfraDataTestBase
    {
        sealed class Proxy : IDamageVitalsTarget
        {
            internal readonly VitalsState Actual=new VitalsState{Health=100};internal int Writes;
            public double Health {get=>Actual.Health;set{Writes++;Actual.Health=value;}}
        }
        [Test] public void OrdinaryEnemyAttackUsesTypedTargetOnceWithUnchangedDamage()
        {
            var runtime=new ThreatRuntime();var threat=new ThreatAIState();
            threat.Configure(new GdDict{{"instance_id","enemy"},{"room_id","a"},{"health",100.0},{"max_health",100.0},
                {"world_position",GdArray.Of(0.0,0.0,0.0)},{"attack_range",2.0},{"attack_damage",10.0},{"anchored",true},
                {"noise_sensitivity",10.0},{"telegraph_seconds",.2},{"flee_threshold",0.0}});
            runtime.Threats.Add(threat);var proxy=new Proxy();
            for(int i=0;i<2;i++){runtime.SetPlayerSignals(1,1,1,false,"a");runtime.TickThreats(.25,proxy,null,new GdDict{{"durability",0.0}},new Vec3(1,0,0));}
            Assert.AreEqual(90,proxy.Actual.Health);Assert.AreEqual(1,proxy.Writes);Assert.AreEqual(1,runtime.DamagePipeline.ProcessedHits);
        }
    }
}
