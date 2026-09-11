// Ported from scripts/systems/demo_scope_gate.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-RL-006 demo scope gate. Blocklist semantics: in a <c>demo</c> build, features listed in the
    /// manifest are blocked; everything else (and every feature in dev/release) is allowed. The empty
    /// feature id is always rejected. Entries may carry <c>params</c> enforcement caps (Tranche 6).
    /// </summary>
    public class DemoScopeGate : IStatusLineProvider
    {
        public const string DemoKind = "demo";
        public const string FullKind = "release";
        public const string DevKind = "dev";

        GdDict _manifest = new GdDict();
        IBuildKindProvider _buildMetadata = null;
        readonly GdArray _features = new GdArray();
        readonly GdDict _paramsByFeature = new GdDict();

        public void Configure(GdDict manifest, IBuildKindProvider buildMetadata)
        {
            _manifest = manifest ?? new GdDict();
            _buildMetadata = buildMetadata;
            _features.Clear();
            _paramsByFeature.Clear();
            object listVariant = _manifest.Get("demo_blocked_features", new GdArray());
            if (listVariant is GdArray list)
            {
                foreach (var entry in list)
                {
                    if (!(entry is GdDict entryDict)) continue;
                    string featureId = V.Str(entryDict.Get("feature_id", ""));
                    if (featureId.Length == 0) continue;
                    _features.Add(featureId);
                    object paramsVariant = entryDict.Get("params", new GdDict());
                    if (paramsVariant is GdDict p && !p.IsEmpty)
                        _paramsByFeature[featureId] = p.DeepCopy();
                }
            }
        }

        /// <summary>Machine-readable enforcement caps for a manifest entry; empty when none.</summary>
        public GdDict GetParams(string featureId)
        {
            object paramsVariant = _paramsByFeature.Get(featureId, new GdDict());
            if (!(paramsVariant is GdDict p)) return new GdDict();
            return p.DeepCopy();
        }

        public bool IsAllowed(string featureId)
        {
            if (string.IsNullOrEmpty(featureId)) return false;
            // Unknown feature_id is always rejected; the gate must be explicit.
            if (!_features.Contains(featureId))
            {
                // If we are in dev/release build, every feature is allowed.
                if (_buildMetadata == null) return true;
                string kind = _buildMetadata.GetBuildKind();
                if (kind != DemoKind) return true;
                // In demo, a feature not in the manifest IS allowed.
                return true;
            }
            // Feature is in the demo-blocked list. Blocked only when in demo.
            if (_buildMetadata == null) return true;
            string activeKind = _buildMetadata.GetBuildKind();
            if (activeKind == DemoKind) return false;
            return true;
        }

        public bool IsBlocked(string featureId) => !IsAllowed(featureId);

        public GdArray ListBlocked() => _features.ShallowCopy();

        public GdArray ListAllowedInDemo()
        {
            if (_buildMetadata == null) return new GdArray();
            if (_buildMetadata.GetBuildKind() != DemoKind) return new GdArray();
            // The gate does not know the full feature surface; callers compare externally.
            return new GdArray();
        }

        public int GetBlockedCount() => _features.Count;

        public GdDict GetSummary()
        {
            string kind = "";
            if (_buildMetadata != null) kind = _buildMetadata.GetBuildKind();
            return new GdDict
            {
                { "build_kind", kind },
                { "manifest_features", _features.ShallowCopy() },
                { "blocked_count", _features.Count },
            };
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            if (_buildMetadata != null) lines.Add("Build: " + _buildMetadata.GetBuildKind());
            lines.Add(InfraCompat.Fmt("Demo-blocked features: {0}", _features.Count));
            return lines;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();
    }
}
