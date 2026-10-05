using System;
using System.Globalization;
using System.Text;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Transport ownership is never full owner/proof admission.
    internal sealed class OwnedProofPackageInput
    {
        readonly GdDict _package;
        internal readonly long SemanticNodes,WireNodes;
        internal readonly int WireDepth;
        internal OwnedProofPackageInput(GdDict package,long semantic,long wire,int depth)
        { _package=package.DeepCopy();SemanticNodes=semantic;WireNodes=wire;WireDepth=depth; }
        internal GdDict CopyPackage()=>_package.DeepCopy();
    }
    internal static class ProofPackageCodec
    {
        internal static bool TryRead(string text,out OwnedProofPackageInput input,out string reason)
        {
            input=null;if(!ProofPackageJsonReader.TryRead(text,out var outer,out reason))return false;
            return TryOwnEnvelope(outer,out input,out reason);
        }
        internal static bool TryOwnEnvelope(GdDict outer,out OwnedProofPackageInput input,out string reason)
        {
            input=null;reason="invalid_proof_package_envelope";
            try
            {
                if(!AuxiliaryProofOwnerProfile.Exact(outer,"schema","codec")||outer.Get("schema") as string!=AuxiliaryProofOwnerProfile.OuterSchema||!(outer.Get("codec") is GdDict encoded))return false;
                long wire=0,bytes=0;int depth=0;BoundWire(outer,0,ref wire,ref depth,ref bytes);
                if(encoded.Get("schema") as string!=ComponentDomainCodec.BitExactSchema||!ComponentDomainCodec.TryDecode(encoded,out var package,out reason))return false;
                long semantic=8;int semanticDepth=0;Count(package,2,128,100000,ref semantic,ref semanticDepth);
                if(!AuxiliaryProofOwnerProfile.ExactPackage(package)||!(package.Get("package_version") is long v)||v!=1||
                    package.Get("owner_profile") as string!=AuxiliaryProofOwnerProfile.Profile||package.Get("hash_algorithm") as string!=PaidHashContext.BitsV2.Algorithm||
                    !(package.Get("current_owner") is GdDict)||!(package.Get("origins") is GdDict)||!(package.Get("resources") is GdDict resources)||resources.Count!=1||!(package.Get("proofs") is GdDict)||
                    !AuxiliaryProofOwnerProfile.Digest(package.Get("current_owner_digest"))||!AuxiliaryProofOwnerProfile.Digest(package.Get("current_resource_digest"))||!AuxiliaryProofOwnerProfile.Digest(package.Get("snapshot_content_sha256")))return false;
                input=new OwnedProofPackageInput(package,semantic,wire,depth);reason="owned_unadmitted";return true;
            }
            catch(ArgumentException){reason="proof_package_bound";return false;}catch(OverflowException){reason="proof_package_bound";return false;}
        }
        internal static bool TryOwnPaidEnvelope(GdDict paid,out OwnedProofPackageInput input,out string reason)
        {
            input=null;reason="proof_paid_envelope_bound";
            try
            {
                if(!AuxiliaryProofOwnerProfile.Exact(paid,"schema_version","save_mode","domain")||!(paid.Get("domain") is GdDict outer))return false;
                long wire=0,bytes=0;int depth=0;BoundWire(paid,0,ref wire,ref depth,ref bytes);
                if(!TryOwnEnvelope(outer,out var packageInput,out reason))return false;
                var package=packageInput.CopyPackage();long semantic=14;int semanticDepth=0;
                Count(package,3,128,100000,ref semantic,ref semanticDepth);
                input=new OwnedProofPackageInput(package,semantic,wire,depth);reason="owned_unadmitted_paid_envelope";return true;
            }
            catch(ArgumentException){reason="proof_paid_envelope_bound";return false;}catch(OverflowException){reason="proof_paid_envelope_bound";return false;}
        }
        static void AddBytes(ref long bytes,long extra)
        { bytes=checked(bytes+extra);if(bytes>ProofPackageJsonReader.MaximumBytes)throw new ArgumentException("byte_bound"); }
        static void BoundWire(object value,int at,ref long nodes,ref int depth,ref long bytes)
        {
            if(at>386||checked(++nodes)>549999)throw new ArgumentException("wire_bound");depth=Math.Max(depth,at);
            if(value is GdDict d)
            {
                AddBytes(ref bytes,2+Math.Max(0,d.Count-1)+d.Count);
                foreach(var pair in d){if(!(pair.Key is string))throw new ArgumentException("key_type");BoundWire(pair.Key,at+1,ref nodes,ref depth,ref bytes);BoundWire(pair.Value,at+1,ref nodes,ref depth,ref bytes);}return;
            }
            if(value is GdArray a)
            { AddBytes(ref bytes,2+Math.Max(0,a.Count-1));foreach(var item in a)BoundWire(item,at+1,ref nodes,ref depth,ref bytes);return; }
            if(value is string text)
            {
                if(text.Length>65536)throw new ArgumentException("string_bound");long count=2+new UTF8Encoding(false,true).GetByteCount(text);
                foreach(char c in text)if(c=='"'||c=='\\'||c=='\b'||c=='\f'||c=='\n'||c=='\r'||c=='\t')count++;else if(c<32)count+=5;
                AddBytes(ref bytes,count);return;
            }
            if(value==null){AddBytes(ref bytes,4);return;}if(value is bool b){AddBytes(ref bytes,b?4:5);return;}
            if(value is long n){AddBytes(ref bytes,n.ToString(CultureInfo.InvariantCulture).Length);return;}
            throw new ArgumentException("wire_scalar");
        }
        static void Count(object value,int at,int maxDepth,long maxNodes,ref long nodes,ref int depth)
        {
            if(at>maxDepth||checked(++nodes)>maxNodes)throw new ArgumentException("bound");depth=Math.Max(depth,at);
            if(value is GdDict d)foreach(var pair in d){Count(pair.Key,at+1,maxDepth,maxNodes,ref nodes,ref depth);Count(pair.Value,at+1,maxDepth,maxNodes,ref nodes,ref depth);}
            else if(value is GdArray a)foreach(var item in a)Count(item,at+1,maxDepth,maxNodes,ref nodes,ref depth);
        }
    }
}
