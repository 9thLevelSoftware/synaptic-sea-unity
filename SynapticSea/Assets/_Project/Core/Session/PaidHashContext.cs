using System;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    /// <summary>Immutable owner-declared hash authority. No ambient or imported caller preference.</summary>
    public sealed class PaidHashContext
    {
        public static readonly PaidHashContext Legacy = new PaidHashContext(PaidCraftingState.LegacyHashAlgorithm);
        public static readonly PaidHashContext BitsV2 = new PaidHashContext(PaidCraftingState.BitExactHashAlgorithm);
        public string Algorithm { get; }
        PaidHashContext(string algorithm) { Algorithm = algorithm; }
        public string Hash(object value) => PaidCraftingState.Hash(value, Algorithm);
        public bool Equal(object a, object b) => ReferenceEquals(this, Legacy) ? V.VariantEquals(a,b) : Hash(a)==Hash(b);
        public static bool TryFromOwner(GdDict owner, out PaidHashContext context, out string reason)
        {
            context=null; reason="invalid_paid_hash_binding";
            if(owner==null || !(owner.Get("schema_version") is long schema)) return false;
            if(schema>=1 && schema<=5)
            {
                if(owner.Has("hash_algorithm") || owner.Has("feature_schema")) return false;
                context=Legacy; reason="ok"; return true;
            }
            if(schema==7)
            {
                if(!CheckpointProofAdmission.HasClosedContext || !AuxiliaryProofOwnerProfile.IsBinding(owner)) return false;
                context=BitsV2;reason="ok";return true;
            }
            if(schema!=6 || !(owner.Get("feature_schema") is long feature) || feature<3 || feature>5 ||
                !(owner.Get("hash_algorithm") is string algorithm) || algorithm!=BitsV2.Algorithm) return false;
            context=BitsV2; reason="ok"; return true;
        }
        public static PaidHashContext FromOwner(GdDict owner)
        {
            if(!TryFromOwner(owner,out var context,out var reason)) throw new ArgumentException(reason);
            return context;
        }
        public static long FeatureSchema(GdDict owner)
        {
            FromOwner(owner);
            return (owner.GetInt("schema_version")==6 || owner.GetInt("schema_version")==7) ? owner.GetInt("feature_schema") : owner.GetInt("schema_version");
        }
        internal static bool SameBinding(GdDict a,GdDict b) => TryFromOwner(a,out var ac,out _) &&
            TryFromOwner(b,out var bc,out _) && ReferenceEquals(ac,bc) &&
            (ReferenceEquals(ac,Legacy) || (a.GetInt("schema_version")==b.GetInt("schema_version") && FeatureSchema(a)==FeatureSchema(b)));
    }
}
