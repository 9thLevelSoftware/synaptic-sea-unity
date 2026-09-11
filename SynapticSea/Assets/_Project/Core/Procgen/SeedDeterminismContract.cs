// Ported from scripts/procgen/seed_determinism_contract.gd @ 96ecb2b0
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Asserts the procgen pipeline produces deterministic, byte-identical output for any
    /// (seed, archetype, biome, difficulty) tuple. <see cref="Fnv1a64"/> fingerprints the
    /// <c>JSON.stringify(layout, "  ")</c> text; <see cref="AssertLayoutMatch"/> runs the pipeline twice and compares.
    /// </summary>
    public static class SeedDeterminismContract
    {
        const long OffsetNegative = -3750763034362895579; // 0xcbf29ce484222325 as int64
        const long PrimePositive = 1099511628211;         // 0x100000001b3
        const long Mask32 = 0xFFFFFFFF;

        /// <summary>
        /// FNV-1a 64 over the text's Unicode code points (<c>String.unicode_at</c>, not UTF-8 bytes); the signed int64
        /// reinterpretation of the canonical unsigned hash.
        /// </summary>
        public static long Fnv1a64(string text)
        {
            long h = OffsetNegative;
            if (string.IsNullOrEmpty(text)) return h;
            int i = 0;
            while (i < text.Length)
            {
                long c = V.NextCodePoint(text, ref i);
                h = h ^ c;
                h = Umul64(h, PrimePositive);
            }
            return h;
        }

        /// <summary>Low 64 bits of an unsigned 64-bit multiply (the GDScript split-and-add), as signed int64.</summary>
        static long Umul64(long aUnsigned, long bUnsigned)
        {
            unchecked
            {
                long aLo = aUnsigned & Mask32;
                long aHi = (aUnsigned >> 32) & Mask32;
                long bLo = bUnsigned & Mask32;
                long bHi = (bUnsigned >> 32) & Mask32;
                long p0 = aLo * bLo;
                long p1 = aLo * bHi;
                long p2 = aHi * bLo;
                long mid = p1 + p2;
                long shiftedMid = mid << 32;
                return p0 + shiftedMid;
            }
        }

        /// <summary>Runs the pipeline twice from the same inputs and compares the stringified layouts.</summary>
        public static GdDict AssertLayoutMatch(ShipBlueprint blueprint, GdDict archetype, string biomeId, string difficultyId, string kitId = "")
        {
            GdDict layoutA = RunPipeline(blueprint, archetype, biomeId, difficultyId, kitId);
            GdDict layoutB = RunPipeline(blueprint, archetype, biomeId, difficultyId, kitId);
            return CompareLayouts(layoutA, layoutB, biomeId, difficultyId);
        }

        /// <summary>Runs the pipeline once and records its hash (always <c>match=true</c>).</summary>
        public static GdDict RecordGolden(ShipBlueprint blueprint, GdDict archetype, string biomeId, string difficultyId, string kitId = "")
        {
            GdDict layout = RunPipeline(blueprint, archetype, biomeId, difficultyId, kitId);
            string text = GdJson.Stringify(layout, "  ");
            long hash = Fnv1a64(text);
            long length = CodePointLength(text);
            return new GdDict
            {
                { "match", true },
                { "hash_a", hash },
                { "hash_b", hash },
                { "byte_equal", true },
                { "length_a", length },
                { "length_b", length },
                { "diff_first_char", -1L },
                { "biome_id", biomeId },
                { "difficulty_id", difficultyId },
                { "golden_text_length", length },
                { "golden_hash", hash },
            };
        }

        /// <summary>
        /// Stringifies the contract pipeline's layout exactly as <see cref="RecordGolden"/> hashes it
        /// (<c>JSON.stringify(layout, "  ")</c>; engine vectors render as "(x, y)" strings).
        /// </summary>
        public static string GoldenText(ShipBlueprint blueprint, GdDict archetype, string biomeId, string difficultyId, string kitId = "") =>
            GdJson.Stringify(RunPipeline(blueprint, archetype, biomeId, difficultyId, kitId), "  ");

        /// <summary>
        /// One pipeline run (template / room assigner / cell layout / wall-door resolver / serializer + optional
        /// encounter injection with the contract's built-in biome/difficulty defaults). This is a different pipeline
        /// from <see cref="ShipLayoutGenerator.GenerateWithOptions"/>.
        /// </summary>
        public static GdDict RunPipeline(ShipBlueprint blueprint, GdDict archetype, string biomeId, string difficultyId, string kitId)
        {
            if (blueprint == null) return new GdDict();
            archetype = archetype ?? new GdDict();
            biomeId = biomeId ?? "";
            difficultyId = difficultyId ?? "";

            // Stage 1: template selection.
            TopologyTemplate template = new TemplateSelector().Select(blueprint, archetype);
            if (template == null) return new GdDict();

            // Stage 2: room assigner.
            var roomPlan = new RoomAssigner().Assign(template, blueprint, archetype);
            if (roomPlan.Count == 0) return new GdDict();

            // Stage 3: cell layout engine.
            GdDict cellGrid = new CellLayoutEngine().Layout(roomPlan, template, blueprint.SeedValue);
            if (cellGrid.GetDictOrEmpty("rooms").IsEmpty) return new GdDict();

            // Stage 4: wall door resolver.
            GdDict geometry = new WallDoorResolver().Resolve(cellGrid, roomPlan);

            // Stage 5: layout serializer.
            string archetypeName = V.Str(archetype.Get("name", V.Str(archetype.Get("template", "default"))));
            GdDict layout = new LayoutSerializer().Serialize(cellGrid, geometry, roomPlan, template.Id, blueprint.SeedValue, archetypeName);
            if (layout.IsEmpty) return new GdDict();

            // Stage 6: encounter injection (REQs PG-005..007).
            if (biomeId.Length != 0 || difficultyId.Length != 0)
            {
                BiomeProfile biome = BiomeProfile.FromDict(DefaultBiome(biomeId));
                DifficultyProfile difficulty = DifficultyProfile.FromDict(DefaultDifficulty(difficultyId));
                layout = new EncounterInjector().Inject(layout, biome, difficulty, blueprint.SeedValue);
            }

            return layout;
        }

        static long CodePointLength(string text)
        {
            long n = 0;
            int i = 0;
            while (i < text.Length)
            {
                V.NextCodePoint(text, ref i);
                n++;
            }
            return n;
        }

        static GdDict CompareLayouts(GdDict layoutA, GdDict layoutB, string biomeId, string difficultyId)
        {
            string textA = GdJson.Stringify(layoutA, "  ");
            string textB = GdJson.Stringify(layoutB, "  ");
            long hashA = Fnv1a64(textA);
            long hashB = Fnv1a64(textB);
            bool byteEqual = textA == textB;

            long diffFirstChar = -1;
            if (!byteEqual)
            {
                // Index in code points (GDScript String indexing).
                int ia = 0, ib = 0;
                long index = 0;
                while (ia < textA.Length && ib < textB.Length)
                {
                    int ca = V.NextCodePoint(textA, ref ia);
                    int cb = V.NextCodePoint(textB, ref ib);
                    if (ca != cb)
                    {
                        diffFirstChar = index;
                        break;
                    }
                    index++;
                }
                long lengthA = CodePointLength(textA), lengthB = CodePointLength(textB);
                if (diffFirstChar == -1 && lengthA != lengthB) diffFirstChar = System.Math.Min(lengthA, lengthB);
            }

            return new GdDict
            {
                { "match", byteEqual && hashA == hashB },
                { "hash_a", hashA },
                { "hash_b", hashB },
                { "byte_equal", byteEqual },
                { "length_a", CodePointLength(textA) },
                { "length_b", CodePointLength(textB) },
                { "diff_first_char", diffFirstChar },
                { "biome_id", biomeId },
                { "difficulty_id", difficultyId },
            };
        }

        /// <summary>Minimal biome dictionary for <paramref name="biomeId"/> (the contract does not read the catalog).</summary>
        public static GdDict DefaultBiome(string biomeId)
        {
            if (string.IsNullOrEmpty(biomeId)) biomeId = "abyssal_synaptic_sea";
            switch (biomeId)
            {
                case "breach_field":
                    return new GdDict
                    {
                        { "id", "breach_field" },
                        { "hazard_modifier", 1.4 },
                        { "loot_quality_modifier", 1.1 },
                        { "encounter_density_modifier", 1.3 },
                        { "ambient_intensity", 0.85 },
                        { "encounter_table_id", "biomatter_lurker" },
                    };
                case "dead_fleet":
                    return new GdDict
                    {
                        { "id", "dead_fleet" },
                        { "hazard_modifier", 1.1 },
                        { "loot_quality_modifier", 1.4 },
                        { "encounter_density_modifier", 0.8 },
                        { "ambient_intensity", 1.1 },
                        { "encounter_table_id", "derelict_pirate" },
                    };
                default:
                    return new GdDict
                    {
                        { "id", "abyssal_synaptic_sea" },
                        { "hazard_modifier", 1.0 },
                        { "loot_quality_modifier", 1.0 },
                        { "encounter_density_modifier", 1.0 },
                        { "ambient_intensity", 1.0 },
                        { "encounter_table_id", "biomatter_lurker" },
                    };
            }
        }

        public static GdDict DefaultDifficulty(string difficultyId)
        {
            if (string.IsNullOrEmpty(difficultyId)) difficultyId = "standard";
            switch (difficultyId)
            {
                case "hardened":
                    return new GdDict
                    {
                        { "id", "hardened" },
                        { "hazard_modifier", 1.4 },
                        { "loot_quality_modifier", 0.85 },
                        { "encounter_density_modifier", 1.3 },
                        { "ambient_intensity", 1.0 },
                    };
                case "deep_dive":
                    return new GdDict
                    {
                        { "id", "deep_dive" },
                        { "hazard_modifier", 1.7 },
                        { "loot_quality_modifier", 1.1 },
                        { "encounter_density_modifier", 1.6 },
                        { "ambient_intensity", 1.0 },
                    };
                default:
                    return new GdDict
                    {
                        { "id", "standard" },
                        { "hazard_modifier", 1.0 },
                        { "loot_quality_modifier", 1.0 },
                        { "encounter_density_modifier", 1.0 },
                        { "ambient_intensity", 1.0 },
                    };
            }
        }
    }
}
