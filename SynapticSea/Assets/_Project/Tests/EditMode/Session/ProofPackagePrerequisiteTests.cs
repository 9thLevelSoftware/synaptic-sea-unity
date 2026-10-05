using System;
using System.Collections.Generic;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Session;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
namespace SynapticSea.Tests.Session
{
    public sealed class ProofPackagePrerequisiteTests
    {
        static readonly string Digest=new string('a',64);
        static GdDict Package()=>new GdDict{{"package_version",1L},{"owner_profile",AuxiliaryProofOwnerProfile.Profile},{"hash_algorithm",PaidHashContext.BitsV2.Algorithm},{"run_id","run"},{"actor_id","actor"},{"current_owner",new GdDict{{"schema_version",7L}}},{"current_owner_digest",Digest},{"current_resource_digest",Digest},{"snapshot_content_sha256",Digest},{"origins",new GdDict()},{"resources",new GdDict{{Digest,new GdDict()}}},{"proofs",new GdDict()}};
        static GdDict Wire(GdDict package)=>new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",ComponentDomainCodec.Encode(package,ComponentDomainCodec.BitExactSchema)}};
        static long Count(object value,int at,out int depth)
        {
            long n=1;depth=at;if(value is GdDict d)foreach(var p in d){n+=Count(p.Key,at+1,out int a);n+=Count(p.Value,at+1,out int b);depth=Math.Max(depth,Math.Max(a,b));}
            else if(value is GdArray arr)foreach(var item in arr){n+=Count(item,at+1,out int m);depth=Math.Max(depth,m);}return n;
        }
        [Test] public void ClosedTransportRoundtripOwnsCopiesButNeverAdmitsSchemaSeven()
        {
            var raw=Package();var wire=Wire(raw);string text=PaidSnapshotCodec.Stringify(wire);
            Assert.IsTrue(ProofPackageCodec.TryRead(text,out var input,out string reason));Assert.AreEqual("owned_unadmitted",reason);
            Assert.AreEqual(Count(wire,0,out int depth),input.WireNodes);Assert.AreEqual(depth,input.WireDepth);
            var semanticOuter=new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",new GdDict{{"schema",ComponentDomainCodec.BitExactSchema},{"value",raw}}}};
            Assert.AreEqual(Count(semanticOuter,0,out _),input.SemanticNodes);
            raw["run_id"]="changed";var copy=input.CopyPackage();Assert.AreEqual("run",copy.Get("run_id"));copy["run_id"]="changed_again";Assert.AreEqual("run",input.CopyPackage().Get("run_id"));
            Assert.IsFalse(DomainBundle.TryCreate(input.CopyPackage().Get("current_owner") as GdDict,out _,out _));
            Assert.IsFalse(PaidHashContext.TryFromOwner(new GdDict{{"schema_version",7L},{"feature_schema",5L},{"hash_algorithm",PaidHashContext.BitsV2.Algorithm},{"auxiliary_proof_format",1L}},out _,out _));
        }
        [Test] public void UnknownOuterProfileAndExtraKeysRefuse()
        {
            var package=Package();package["unexpected"]=true;Assert.IsFalse(ProofPackageCodec.TryOwnEnvelope(Wire(package),out _,out _));
            package=Package();package["package_version"]=2L;Assert.IsFalse(ProofPackageCodec.TryOwnEnvelope(Wire(package),out _,out _));
            package=Package();package["owner_profile"]="caller_trusted";Assert.IsFalse(ProofPackageCodec.TryOwnEnvelope(Wire(package),out _,out _));
            var wire=Wire(Package());wire["schema"]=ComponentDomainCodec.BitExactSchema;Assert.IsFalse(ProofPackageCodec.TryOwnEnvelope(wire,out _,out _));
        }
        [TestCase("{\"x\":1,\"x\":2}")][TestCase("{\"x\":01}")][TestCase("{\"x\":1e999}")][TestCase("{\"x\":9223372036854775808}")][TestCase("{\"x\":\"\\ud800\"}")][TestCase("{} garbage")]
        public void BoundedParserRejectsAmbiguousOrMalformedInput(string text)=>Assert.IsFalse(ProofPackageJsonReader.TryRead(text,out _,out _));
        [Test] public void ParserEscapesDepthAndBytesAreBounded()
        {
            Assert.IsTrue(ProofPackageJsonReader.TryRead("{\"x\":\"é\\n\\u0001\\ud83d\\ude00\"}",out var d,out _));Assert.AreEqual("é\n\u0001😀",d.Get("x"));
            Assert.IsFalse(ProofPackageJsonReader.TryRead(new string(' ',4*1024*1024+1),out _,out _));
            string deep="{\"x\":"+new string('[',387)+"0"+new string(']',387)+"}";Assert.IsFalse(ProofPackageJsonReader.TryRead(deep,out _,out _));
            string oversized="{\"x\":\""+new string('x',65537)+"\"}";Assert.IsFalse(ProofPackageJsonReader.TryRead(oversized,out _,out _));
        }
        [Test] public void DirectEnvelopeCannotBypassStringOrEscapedUtf8Budget()
        {
            var raw=Package();raw["run_id"]=new string('x',65537);Assert.IsFalse(ProofPackageCodec.TryOwnEnvelope(Wire(raw),out _,out _));
            raw=Package();var strings=new GdDict();for(int i=0;i<11;i++)strings["entry"+i]=new string('\u0001',65536);raw["proofs"]=strings;
            Assert.IsFalse(ProofPackageCodec.TryOwnEnvelope(Wire(raw),out _,out _));
        }
        [Test] public void ResourceWitnessesIncludeDeclaredAbsentEmptyAndMembershipWithDistinctHashes()
        {
            var old=CoreServices.Resources;
            try
            {
                var texts=new Dictionary<string,string>{{"res://diagnostic/value.json","{\"value\":1}"},{"res://diagnostic/missing.json",null}};
                var dirs=new Dictionary<string,IReadOnlyList<string>>{{"res://diagnostic",new[]{"value.json"}},{"res://diagnostic/empty",Array.Empty<string>()},{"res://absent",null}};
                var authority=new ImmutableResourceAuthority(texts,dirs);ResourceAuthorityPublication.Publish(authority);Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var lease,out _));
                Assert.IsTrue(ProofResourceBinding.TryCapture(lease,out var binding,out _));Assert.AreEqual(authority.ContentSha256,binding.SnapshotContentSha256);Assert.AreNotEqual(binding.SnapshotContentSha256,binding.ResourceCapsuleDigest);
                var capsule=binding.CopyCapsule();Assert.AreEqual(3,((GdArray)capsule.Get("directories")).Count);Assert.IsTrue(authority.DirExists("res://diagnostic/empty"));Assert.IsFalse(authority.DirExists("res://absent"));Assert.Throws<InvalidOperationException>(()=>authority.DirExists("res://undeclared"));
                var package=Package();package["snapshot_content_sha256"]=binding.SnapshotContentSha256;package["current_resource_digest"]=binding.ResourceCapsuleDigest;package["resources"]=new GdDict{{binding.ResourceCapsuleDigest,capsule}};
                Assert.IsTrue(binding.MatchesPackage(package,out _));
                ((GdDict)((GdArray)capsule.Get("directories"))[0])["present"]=true;Assert.IsFalse(binding.MatchesPackage(package,out _));
                ResourceAuthorityPublication.Publish(new ImmutableResourceAuthority(texts,dirs));Assert.IsFalse(binding.MatchesPackage(package,out _));Assert.IsFalse(ProofResourceBinding.TryCapture(lease,out _,out _));
            }
            finally{CoreServices.Resources=old;}
        }
    }
}
