using System;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public sealed class ProofPackageWriterTests
    {
        [Test] public void CanonicalWriterMatchesRealCodecUtf8AndOwnedRoundtripWithoutAdmission()
        {
            string digest=new string('a',64);
            var package=new GdDict{{"package_version",1L},{"owner_profile",AuxiliaryProofOwnerProfile.Profile},{"hash_algorithm",PaidHashContext.BitsV2.Algorithm},
                {"run_id","r\"\\\n\u0001é😀"},{"actor_id","actor"},{"current_owner",new GdDict{{"schema_version",7L},{"positive_zero",0.0},{"negative_zero",BitConverter.Int64BitsToDouble(long.MinValue)},{"counter",long.MaxValue}}},
                {"current_owner_digest",digest},{"current_resource_digest",digest},{"snapshot_content_sha256",digest},{"origins",new GdDict()},{"resources",new GdDict{{digest,new GdDict()}}},{"proofs",new GdDict()}};
            var outer=new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",ComponentDomainCodec.Encode(package,ComponentDomainCodec.BitExactSchema)}};
            Assert.IsTrue(ProofPackageCodec.TryOwnEnvelope(outer,out var input,out _));
            Assert.IsTrue(ProofPackageCanonicalWriter.TryWrite(input,out string text,out string reason));Assert.AreEqual("owned_unadmitted_output",reason);
            Assert.AreEqual(PaidSnapshotCodec.Stringify((GdDict)OrdinalWireObjects(outer)),text);Assert.IsTrue(ProofPackageCodec.TryRead(text,out var roundtrip,out _));
            var owner=(GdDict)roundtrip.CopyPackage().Get("current_owner");Assert.AreEqual(long.MinValue,BitConverter.DoubleToInt64Bits((double)owner.Get("negative_zero")));Assert.AreEqual(long.MaxValue,owner.Get("counter"));
            Assert.IsFalse(DomainBundle.TryCreate(owner,out _,out _));
        }
        // Stock codec is insertion ordered; canonical transport orders objects only.
        // Tagged dictionary-entry arrays retain their original semantic ordering.
        private static object OrdinalWireObjects(object value)
        {
            if(value is GdDict dict)
            {
                var sorted=new GdDict();
                foreach(var pair in dict.OrderBy(p=>(string)p.Key,StringComparer.Ordinal))
                    sorted[pair.Key]=OrdinalWireObjects(pair.Value);
                return sorted;
            }
            if(value is GdArray array)
            {
                var preserved=new GdArray();
                foreach(var item in array) preserved.Add(OrdinalWireObjects(item));
                return preserved;
            }
            return value;
        }
        [Test] public void NullInputNeverProducesOutput()=>Assert.IsFalse(ProofPackageCanonicalWriter.TryWrite(null,out _,out _));
    }
}
