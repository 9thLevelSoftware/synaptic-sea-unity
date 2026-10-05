using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>Unwired prototype. Full generation identity; no guessed world/marker salt or floating point input.</summary>
    public sealed class FirstAwayGenerationInputs
    {
        public const string Profile = "first_away_salvage_v1";
        public long CandidateSeed { get; }
        public long WorldSeed { get; }
        public long Size { get; }
        public long Condition { get; }
        public string MarkerId { get; }
        public string OwnerId { get; }
        public string Biome { get; }
        public string Difficulty { get; }
        public long SectorX { get; }
        public long SectorY { get; }
        public long MarkerIndex { get; }

        public FirstAwayGenerationInputs(long candidateSeed, long worldSeed, long size, long condition,
            string markerId, string ownerId, string biome, string difficulty)
        {
            if (candidateSeed != 42 && candidateSeed != 777) throw new ArgumentException("unsupported preferred candidate");
            if (size < 0 || size > 2 || condition < 0 || condition > 2) throw new ArgumentException("unsupported size/condition");
            if (biome != "breach_field" || difficulty != "standard") throw new ArgumentException("unsupported first-away context");
            if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("missing owning ship identity");
            string[] parts = (markerId ?? "").Split(':');
            if (parts.Length != 3 || !ParseSector(parts[0], out long x) || !ParseSector(parts[1], out long y) || !ParseSector(parts[2], out long z) || z < 0 || z >= SynapticSea.Core.Systems.MarkerGenerator.MARKERS_PER_CELL || x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue)
                throw new ArgumentException("marker must be canonical integer sector_x:sector_y:nonnegative_index identity");
            CandidateSeed = candidateSeed; WorldSeed = worldSeed; Size = size; Condition = condition;
            MarkerId = markerId; OwnerId = ownerId; Biome = biome; Difficulty = difficulty;
            SectorX = x; SectorY = y; MarkerIndex = z;
        }
        static bool ParseSector(string text, out long value) => long.TryParse(text, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value) && value.ToString(CultureInfo.InvariantCulture) == text;
        public GdDict ToDict() => new GdDict { { "profile", Profile }, { "candidate_seed", CandidateSeed }, { "world_seed", WorldSeed },
            { "size", Size }, { "condition", Condition }, { "marker_id", MarkerId }, { "owner_id", OwnerId }, { "biome", Biome },
            { "difficulty", Difficulty }, { "sector_x", SectorX }, { "sector_y", SectorY }, { "marker_index", MarkerIndex } };
        internal void Write(BinaryWriter writer)
        {
            writer.Write(Profile); writer.Write(CandidateSeed); writer.Write(WorldSeed); writer.Write(Size); writer.Write(Condition);
            writer.Write(MarkerId); writer.Write(OwnerId); writer.Write(Biome); writer.Write(Difficulty);
            writer.Write(SectorX); writer.Write(SectorY); writer.Write(MarkerIndex);
        }
        /// <summary>Named SHA-256 integer substreams over complete typed identity; no global RNG or floating point hashing.</summary>
        public uint Substream(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("missing substream identity");
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, FirstAwayGenerationDescriptor.StrictUtf8, true))
                { Write(writer); writer.Write(name); }
                byte[] digest = FirstAwayGenerationDescriptor.Digest(stream.ToArray());
                return (uint)digest[0] | ((uint)digest[1] << 8) | ((uint)digest[2] << 16) | ((uint)digest[3] << 24);
            }
        }
    }

    public sealed class FirstAwayCatalogIdentity
    {
        public string Revision { get; }
        public string Sha256 { get; }
        public FirstAwayCatalogIdentity(string revision, string sha256)
        {
            if (string.IsNullOrWhiteSpace(revision) || !FirstAwayGenerationDescriptor.ValidHash(sha256)) throw new ArgumentException("invalid authored catalog identity");
            Revision = revision; Sha256 = sha256;
        }
    }

    /// <summary>Prototype binding only: no admission, persistence wiring or proof that geometry/materialized content is playable.</summary>
    public sealed class FirstAwayGenerationDescriptor
    {
        internal static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        readonly FirstAwayGenerationInputs _inputs;
        readonly SortedDictionary<string, FirstAwayCatalogIdentity> _catalogs;
        readonly byte[] _layout, _gameplay;
        public string Provider { get; }
        public string BindingSha256 { get; }
        public string LayoutSha256 { get; }
        public string GameplaySha256 { get; }
        public byte[] LayoutBytes => (byte[])_layout.Clone();
        public byte[] GameplayBytes => (byte[])_gameplay.Clone();
        public FirstAwayGenerationDescriptor(FirstAwayGenerationInputs inputs, string provider,
            IDictionary<string, FirstAwayCatalogIdentity> catalogs, byte[] layout, byte[] gameplay)
        {
            if (inputs == null || string.IsNullOrWhiteSpace(provider) || catalogs == null || catalogs.Count == 0
                || layout == null || layout.Length == 0 || gameplay == null || gameplay.Length == 0) throw new ArgumentException("incomplete descriptor binding");
            StrictUtf8.GetString(layout); StrictUtf8.GetString(gameplay);
            _inputs = inputs; Provider = provider; _catalogs = new SortedDictionary<string, FirstAwayCatalogIdentity>(StringComparer.Ordinal);
            foreach (var row in catalogs)
            {
                if (string.IsNullOrWhiteSpace(row.Key) || row.Value == null) throw new ArgumentException("missing catalog identity");
                _catalogs.Add(row.Key, new FirstAwayCatalogIdentity(row.Value.Revision, row.Value.Sha256));
            }
            _layout = (byte[])layout.Clone(); _gameplay = (byte[])gameplay.Clone();
            LayoutSha256 = Hash(_layout); GameplaySha256 = Hash(_gameplay);
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, StrictUtf8, true))
                {
                    writer.Write("first-away-generation-descriptor-1"); inputs.Write(writer); writer.Write(provider); writer.Write(_catalogs.Count);
                    foreach (var row in _catalogs) { writer.Write(row.Key); writer.Write(row.Value.Revision); writer.Write(row.Value.Sha256); }
                    writer.Write(LayoutSha256); writer.Write(GameplaySha256);
                }
                BindingSha256 = Hash(stream.ToArray());
            }
        }
        public GdDict Snapshot()
        {
            var catalogs = new GdDict();
            foreach (var row in _catalogs) catalogs[row.Key] = new GdDict { { "revision", row.Value.Revision }, { "sha256", row.Value.Sha256 } };
            return new GdDict { { "schema", "first-away-generation-descriptor-1" }, { "inputs", _inputs.ToDict() }, { "provider", Provider },
                { "catalogs", catalogs }, { "layout_sha256", LayoutSha256 }, { "gameplay_sha256", GameplaySha256 }, { "binding_sha256", BindingSha256 } };
        }
        /// <summary>Exact metadata/key/type check against expected identity and raw bytes; never trusts a mutable preview snapshot.</summary>
        public bool Matches(FirstAwayGenerationInputs inputs, string provider, IDictionary<string, FirstAwayCatalogIdentity> catalogs, byte[] layout, byte[] gameplay)
        {
            try { return BindingSha256 == new FirstAwayGenerationDescriptor(inputs, provider, catalogs, layout, gameplay).BindingSha256; }
            catch (ArgumentException) { return false; }
        }
        public bool MatchesSnapshot(GdDict snapshot) => Exact(snapshot, Snapshot());
        static bool Exact(object actual, object expected)
        {
            if (actual == null || expected == null) return actual == expected;
            if (actual.GetType() != expected.GetType()) return false;
            if (actual is GdDict a && expected is GdDict b)
                return a.Count == b.Count && b.Keys.All(key => a.Has(key) && Exact(a[key], b[key]));
            return actual.Equals(expected);
        }
        internal static bool ValidHash(string text) => text != null && text.Length == 64 && text.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        internal static byte[] Digest(byte[] bytes) { using (var sha = SHA256.Create()) return sha.ComputeHash(bytes); }
        internal static string Hash(byte[] bytes) => BitConverter.ToString(Digest(bytes)).Replace("-", "").ToLowerInvariant();
    }
}
