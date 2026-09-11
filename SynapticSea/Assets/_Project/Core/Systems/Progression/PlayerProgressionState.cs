// Ported from scripts/systems/player_progression_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Pure player-progression model: skill levels (0..MAX), per-skill XP toward the next level, the
    /// skill->category map used to apply class XP multipliers, cross-training counters, and a books_read set.
    /// No scene tree, no RNG. Deterministic per XP sequence.
    /// </summary>
    /// <remarks>
    /// Schema: <c>progression-2</c>. The cross_training and books_read fields are additive: older summaries
    /// (progression-1) load with empty defaults and <see cref="ApplySummary"/> never overwrites an existing
    /// cross_training entry with empty data unless the source explicitly says so.
    /// </remarks>
    public class PlayerProgressionState : ISkillLevelSource
    {
        public const long MAX_SKILL_LEVEL = 10;
        public const string DEFAULT_SKILLS_PATH = "res://data/player/skills.json";
        public const string DEFAULT_BOOKS_PATH = "res://data/player/skill_books.json";
        public const double CROSS_TRAINING_PENALTY = 0.5;
        public const string SCHEMA_VERSION = "progression-2";

        public string ClassId = "";
        public GdDict Skills = new GdDict();            // skill_id -> int level
        public GdDict SkillXp = new GdDict();           // skill_id -> int xp toward next level
        public GdDict CrossTraining = new GdDict();     // skill_id -> int raw XP earned off-category
        public GdDict BooksRead = new GdDict();         // book_id -> true (idempotent set)
        public GdDict SkillXpFractional = new GdDict(); // skill_id -> float carry preserved across fractional XP grants
        GdDict _xpMultipliers = new GdDict();           // category -> float (from the class)
        readonly GdDict _skillCategory = new GdDict();  // skill_id -> category (from the catalog)
        GdDict _bookCatalog = new GdDict();             // book_id -> {target_skill, book_xp, unlocks_skill}

        /// <summary>Loads skills.json into { skill_id -> {category, display_name} }.</summary>
        public static GdDict LoadSkillsCatalog(string path = DEFAULT_SKILLS_PATH)
        {
            var output = new GdDict();
            object parsed = CatalogRegistry.Load(path);
            if (!(parsed is GdDict root))
                return output;
            object skillsVariant = root.Get("skills", new GdArray());
            if (!(skillsVariant is GdArray skillsArr))
                return output;
            foreach (object entry in skillsArr)
            {
                if (!(entry is GdDict e))
                    continue;
                string sid = V.Str(e.Get("skill_id", ""));
                if (sid.Length == 0)
                    continue;
                output[sid] = new GdDict
                {
                    { "category", V.Str(e.Get("category", "")) },
                    { "display_name", V.Str(e.Get("display_name", sid)) },
                };
            }
            return output;
        }

        /// <summary>Loads skill_books.json into { book_id -> {target_skill, book_xp, unlocks_skill} }.</summary>
        public static GdDict LoadBooksCatalog(string path = DEFAULT_BOOKS_PATH)
        {
            var output = new GdDict();
            if (!CatalogRegistry.Exists(path))
                return output;
            object parsed = CatalogRegistry.Load(path);
            if (!(parsed is GdDict root))
                return output;
            object booksVariant = root.Get("books", new GdArray());
            if (!(booksVariant is GdArray booksArr))
                return output;
            foreach (object entry in booksArr)
            {
                if (!(entry is GdDict e))
                    continue;
                string bid = V.Str(e.Get("book_id", ""));
                if (bid.Length == 0)
                    continue;
                output[bid] = new GdDict
                {
                    { "target_skill", V.Str(e.Get("target_skill", "")) },
                    { "book_xp", V.I64(e.Get("book_xp", 0L)) },
                    { "unlocks_skill", V.Str(e.Get("unlocks_skill", "")) },
                };
            }
            return output;
        }

        public static long XpForNextLevel(long level) => (level + 1) * 100;

        /// <summary>
        /// Seeds skills from class_def.starting_skills (every catalog skill present, default 0), records
        /// skill->category, the class multipliers, resets all XP and cross-training to 0, and primes the book catalog.
        /// </summary>
        public void Configure(ClassDefinition classDef, GdDict skillsCatalog, GdDict booksCatalog = null)
        {
            Skills.Clear();
            SkillXp.Clear();
            CrossTraining.Clear();
            BooksRead.Clear();
            SkillXpFractional.Clear();
            _skillCategory.Clear();
            _xpMultipliers = new GdDict();
            ClassId = "";
            _bookCatalog = booksCatalog ?? new GdDict();
            if (classDef != null)
            {
                ClassId = classDef.ClassId;
                _xpMultipliers = classDef.XpMultipliers.ShallowCopy();
            }
            if (skillsCatalog != null)
            {
                foreach (object sid in skillsCatalog.Keys)
                {
                    _skillCategory[sid] = V.Str(skillsCatalog.GetDictOrEmpty(sid).Get("category", ""));
                    Skills[sid] = 0L;
                    SkillXp[sid] = 0L;
                    CrossTraining[sid] = 0L;
                    SkillXpFractional[sid] = 0.0;
                }
            }
            if (classDef != null)
            {
                foreach (var kv in classDef.StartingSkills)
                {
                    if (Skills.Has(kv.Key))
                        Skills[kv.Key] = GdMath.Clampi(V.I64(kv.Value), 0, MAX_SKILL_LEVEL);
                }
            }
        }

        public string GetClassId() => ClassId;

        public long GetSkillLevel(string skillId) => V.I64(Skills.Get(skillId, 0L));

        public long GetSkillXp(string skillId) => V.I64(SkillXp.Get(skillId, 0L));

        public long GetCrossTraining(string skillId) => V.I64(CrossTraining.Get(skillId, 0L));

        public long GetCrossTrainingTotal()
        {
            long total = 0;
            foreach (object v in CrossTraining.Values)
                total += V.I64(v);
            return total;
        }

        public bool HasReadBook(string bookId) => BooksRead.Has(bookId) && V.Bool(BooksRead[bookId]);

        /// <summary>
        /// Applies the class category multiplier to <paramref name="amount"/>, banks it, and levels the skill up
        /// on the curve (capped at MAX_SKILL_LEVEL). Returns true if the level changed. Unknown skill -> false.
        /// When <paramref name="isCrossTraining"/> is true the raw amount is also recorded in the cross_training counter.
        /// </summary>
        public bool GrantXp(string skillId, long amount, bool isCrossTraining = false)
        {
            if (!Skills.Has(skillId))
                return false;
            if (amount <= 0)
                return false;
            if (isCrossTraining)
            {
                CrossTraining[skillId] = V.I64(CrossTraining.Get(skillId, 0L)) + amount;
                amount = (long)GdMath.Round((double)amount * CROSS_TRAINING_PENALTY);
            }
            string category = V.Str(_skillCategory.Get(skillId, ""));
            double mult = V.F64(_xpMultipliers.Get(category, 1.0));
            double carry = V.F64(SkillXpFractional.Get(skillId, 0.0));
            double effectiveTotal = ((double)amount * mult) + carry;
            long effective = (long)Math.Floor(effectiveTotal);
            SkillXpFractional[skillId] = effectiveTotal - (double)effective;
            long level = V.I64(Skills[skillId]);
            if (level >= MAX_SKILL_LEVEL)
            {
                SkillXp[skillId] = 0L;
                SkillXpFractional[skillId] = 0.0;
                return false;
            }
            SkillXp[skillId] = V.I64(SkillXp[skillId]) + effective;
            bool changed = false;
            while (level < MAX_SKILL_LEVEL && V.I64(SkillXp[skillId]) >= XpForNextLevel(level))
            {
                SkillXp[skillId] = V.I64(SkillXp[skillId]) - XpForNextLevel(level);
                level += 1;
                changed = true;
            }
            Skills[skillId] = level;
            if (level >= MAX_SKILL_LEVEL)
            {
                SkillXp[skillId] = 0L;
                SkillXpFractional[skillId] = 0.0;
            }
            return changed;
        }

        /// <summary>
        /// Reads a skill book. The book's <c>book_xp</c> is granted to <c>target_skill</c> (with the same class
        /// multipliers) and the book is recorded in books_read. Reading the same book twice is a no-op (returns false).
        /// Returns true on first read.
        /// </summary>
        public bool GrantXpFromBook(string bookId)
        {
            if (!_bookCatalog.Has(bookId))
                return false;
            if (BooksRead.Has(bookId) && V.Bool(BooksRead[bookId]))
                return false;
            GdDict entry = _bookCatalog.GetDictOrEmpty(bookId);
            string target = V.Str(entry.Get("target_skill", ""));
            if (target.Length == 0 || !Skills.Has(target))
            {
                // Still record the book so a corrupted skill id doesn't silently re-fire later; but don't grant XP to a missing skill.
                BooksRead[bookId] = true;
                return false;
            }
            long xp = V.I64(entry.Get("book_xp", 0L));
            BooksRead[bookId] = true;
            if (xp <= 0)
                return true;
            // Books are direct, intentional training: never flagged as cross-training.
            GrantXp(target, xp, false);
            return true;
        }

        /// <summary>
        /// Reloads the XP multipliers for the current class_id from the class catalog. Empties them when the
        /// class is unknown (GrantXp then falls back to a 1.0 multiplier per category).
        /// </summary>
        void ReloadClassMultipliers()
        {
            Dictionary<string, ClassDefinition> classes = ClassDefinition.LoadAll();
            if (classes.TryGetValue(ClassId, out ClassDefinition def))
                _xpMultipliers = def.XpMultipliers.ShallowCopy();
            else
                _xpMultipliers = new GdDict();
        }

        /// <summary>Re-binds the book catalog at runtime.</summary>
        public void SetBooksCatalog(GdDict booksCatalog)
        {
            _bookCatalog = booksCatalog ?? new GdDict();
        }

        /// <summary>Returns a copy of the book catalog for read-only inspection.</summary>
        public GdDict GetBooksCatalog() => _bookCatalog.DeepCopy();

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "schema", SCHEMA_VERSION },
                { "class_id", ClassId },
                { "skills", Skills.ShallowCopy() },
                { "skill_xp", SkillXp.ShallowCopy() },
                { "skill_xp_fractional", SkillXpFractional.ShallowCopy() },
                { "cross_training", CrossTraining.ShallowCopy() },
                { "books_read", BooksRead.ShallowCopy() },
            };
        }

        /// <summary>
        /// Restores class_id/skills/skill_xp/cross_training/books_read from a GetSummary() dict. Skills/xp are
        /// overwritten per-key (unknown keys ignored). Missing cross_training or books_read keys default to empty.
        /// Returns true if anything changed.
        /// </summary>
        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty)
                return false;
            bool changed = false;
            string newClass = V.Str(summary.Get("class_id", ClassId));
            if (newClass != ClassId)
            {
                ClassId = newClass;
                // Reload the class XP multipliers for the restored class (PR #4 review).
                ReloadClassMultipliers();
                changed = true;
            }
            object skillsVariant = summary.Get("skills", new GdDict());
            if (skillsVariant is GdDict skillsDict)
            {
                foreach (var kv in skillsDict)
                {
                    if (Skills.Has(kv.Key))
                    {
                        long lvl = GdMath.Clampi(V.I64(kv.Value), 0, MAX_SKILL_LEVEL);
                        if (lvl != V.I64(Skills[kv.Key]))
                        {
                            Skills[kv.Key] = lvl;
                            changed = true;
                        }
                    }
                }
            }
            object xpVariant = summary.Get("skill_xp", new GdDict());
            if (xpVariant is GdDict xpDict)
            {
                foreach (var kv in xpDict)
                {
                    if (SkillXp.Has(kv.Key))
                    {
                        long xp = Math.Max(0L, V.I64(kv.Value));
                        if (xp != V.I64(SkillXp[kv.Key]))
                        {
                            SkillXp[kv.Key] = xp;
                            changed = true;
                        }
                    }
                }
            }
            // Mirror GrantXp's cap behavior: a maxed skill carries no pending XP.
            foreach (object sid in Skills.Keys)
            {
                if (V.I64(Skills[sid]) >= MAX_SKILL_LEVEL && V.I64(SkillXp.Get(sid, 0L)) != 0)
                {
                    SkillXp[sid] = 0L;
                    changed = true;
                }
            }
            object fracVariant = summary.Get("skill_xp_fractional", null);
            if (fracVariant is GdDict fracDict)
            {
                foreach (var kv in fracDict)
                {
                    if (SkillXpFractional.Has(kv.Key))
                    {
                        double frac = GdMath.Clampf(V.F64(kv.Value), 0.0, 0.999999);
                        if (Math.Abs(frac - V.F64(SkillXpFractional.Get(kv.Key, 0.0))) > 0.000001)
                        {
                            SkillXpFractional[kv.Key] = frac;
                            changed = true;
                        }
                    }
                }
            }
            foreach (object sid in Skills.Keys)
            {
                if (V.I64(Skills[sid]) >= MAX_SKILL_LEVEL && Math.Abs(V.F64(SkillXpFractional.Get(sid, 0.0))) > 0.000001)
                {
                    SkillXpFractional[sid] = 0.0;
                    changed = true;
                }
            }
            // progression-2 fields: only overwrite when the summary actually contains them.
            object ctVariant = summary.Get("cross_training", null);
            if (ctVariant is GdDict ctDict)
            {
                foreach (var kv in ctDict)
                {
                    if (CrossTraining.Has(kv.Key))
                    {
                        long ct = Math.Max(0L, V.I64(kv.Value));
                        if (ct != V.I64(CrossTraining.Get(kv.Key, 0L)))
                        {
                            CrossTraining[kv.Key] = ct;
                            changed = true;
                        }
                    }
                }
            }
            object brVariant = summary.Get("books_read", null);
            if (brVariant is GdDict brDict)
            {
                foreach (var kv in brDict)
                {
                    if (V.Bool(kv.Value) && !BooksRead.Has(kv.Key))
                    {
                        BooksRead[kv.Key] = true;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        public List<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Class: " + ClassId);
            foreach (object sid in Skills.Keys)
            {
                long lvl = V.I64(Skills[sid]);
                long xp = V.I64(SkillXp.Get(sid, 0L));
                long ct = V.I64(CrossTraining.Get(sid, 0L));
                string suffix = lvl >= MAX_SKILL_LEVEL
                    ? " (max)"
                    : " xp=" + GdString.FormatInt(xp) + "/" + GdString.FormatInt(XpForNextLevel(lvl));
                string ctSuffix = ct > 0 ? " cross=" + GdString.FormatInt(ct) : "";
                lines.Add("  - " + V.Str(sid) + " L" + GdString.FormatInt(lvl) + suffix + ctSuffix);
            }
            return lines;
        }
    }
}
