// Ported from scripts/systems/skill_tree_state.gd @ 96ecb2b0
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// REQ-PM-003 / REQ-PM-004 / ADR-0033 skill-tree model.
    ///
    /// Loads the skill catalog (<c>data/player/skills.json</c>), the skill book catalog
    /// (<c>data/player/skill_books.json</c>), and the per-skill prerequisite table
    /// (<c>data/player/skill_tree.json</c>). Exposes <c>can_unlock(skill_id)</c> and <c>unlock(skill_id)</c>
    /// against a <see cref="PlayerProgressionState"/> + a <see cref="MetaProgressionState"/> (the latter for
    /// cross-run book carries).
    ///
    /// Pure: no scene tree, no RNG. The tree panel UI reads from this model and the panels'
    /// <c>get_status_lines()</c> methods emit accessibility text.
    /// </summary>
    /// <remarks>
    /// The duck-typed <c>progression.has_method("get_skill_level")</c> / <c>has_method("has_read_book")</c> and
    /// <c>meta_state.has_method("is_codex_entry_unlocked")</c> checks become <see cref="ISkillLevelSource"/>,
    /// <see cref="IBookReadSource"/>, and <see cref="ICodexEntrySource"/>.
    /// </remarks>
    public class SkillTreeState : IStatusLineProvider
    {
        /// <summary>The <c>has_read_book(book_id)</c> seam (PlayerProgressionState implements it).</summary>
        public interface IBookReadSource
        {
            bool HasReadBook(string bookId);
        }

        /// <summary>The <c>is_codex_entry_unlocked(entry_id)</c> seam (MetaProgressionState implements it).</summary>
        public interface ICodexEntrySource
        {
            bool IsCodexEntryUnlocked(string entryId);
        }

        public const string DEFAULT_SKILLS_PATH = "res://data/player/skills.json";
        public const string DEFAULT_BOOKS_PATH = "res://data/player/skill_books.json";
        public const string DEFAULT_PREREQS_PATH = "res://data/player/skill_tree.json";

        GdDict _skillsCatalog = new GdDict();  // skill_id -> {category, display_name}
#pragma warning disable CS0414 // kept for parity: GDScript stores it but never reads it back
        GdDict _booksCatalog = new GdDict();   // book_id -> {target_skill, book_xp, unlocks_skill}
#pragma warning restore CS0414
        readonly GdDict _prereqs = new GdDict(); // skill_id -> {requires: [...], book_prerequisite: ""}
        readonly GdDict _unlocked = new GdDict(); // skill_id -> true (idempotent set)

        public static GdDict LoadSkillsCatalog(string path = DEFAULT_SKILLS_PATH) => PlayerProgressionState.LoadSkillsCatalog(path);

        public static GdDict LoadBooksCatalog(string path = DEFAULT_BOOKS_PATH) => PlayerProgressionState.LoadBooksCatalog(path);

        /// <summary>
        /// Loads the prerequisite table. Returns false if the file is missing or malformed; the tree still works
        /// without prereqs (every skill is unlockable from level 0).
        /// </summary>
        public bool LoadPrerequisites(string path = DEFAULT_PREREQS_PATH)
        {
            _prereqs.Clear();
            if (!CatalogRegistry.Exists(path))
                return false;
            object parsed = CatalogRegistry.Load(path);
            if (!(parsed is GdDict parsedDict))
                return false;
            object variant = parsedDict.Get("skill_prerequisites", new GdArray());
            if (!(variant is GdArray entries))
                return false;
            foreach (object entryVariant in entries)
            {
                if (!(entryVariant is GdDict entry))
                    continue;
                string sid = V.Str(entry.Get("skill_id", ""));
                if (sid.Length == 0)
                    continue;
                object requiresRaw = entry.Get("requires", new GdArray());
                var requires = new GdArray();
                if (requiresRaw is GdArray requiresArr)
                {
                    foreach (object reqVariant in requiresArr)
                    {
                        if (!(reqVariant is GdDict req))
                            continue;
                        requires.Append(new GdDict
                        {
                            { "skill_id", V.Str(req.Get("skill_id", "")) },
                            { "min_level", V.I64(req.Get("min_level", 1L)) },
                        });
                    }
                }
                _prereqs[sid] = new GdDict
                {
                    { "requires", requires },
                    { "book_prerequisite", V.Str(entry.Get("book_prerequisite", "")) },
                };
            }
            return true;
        }

        /// <summary>
        /// Sets the in-memory catalogs. The book catalog is read at unlock-time so live updates (player picks up a
        /// new book) work without re-loading.
        /// </summary>
        public void Configure(GdDict skillsCatalog, GdDict booksCatalog)
        {
            _skillsCatalog = skillsCatalog ?? new GdDict();
            _booksCatalog = booksCatalog ?? new GdDict();
        }

        public void SetBooksCatalog(GdDict booksCatalog)
        {
            _booksCatalog = booksCatalog ?? new GdDict();
        }

        /// <summary>Returns the list of prerequisite skills for <paramref name="skillId"/>, or [] when none are recorded.</summary>
        public GdArray GetPrerequisites(string skillId)
        {
            if (!_prereqs.Has(skillId))
                return new GdArray();
            var entry = (GdDict)_prereqs[skillId];
            return ((GdArray)entry.Get("requires", new GdArray())).DeepCopy();
        }

        /// <summary>Returns the book id required to unlock <paramref name="skillId"/>, or "" when no book is required.</summary>
        public string GetBookPrerequisite(string skillId)
        {
            if (!_prereqs.Has(skillId))
                return "";
            return V.Str(((GdDict)_prereqs[skillId]).Get("book_prerequisite", ""));
        }

        /// <summary>Returns true when <paramref name="skillId"/> is a known skill in the catalog.</summary>
        public bool IsKnownSkill(string skillId) => _skillsCatalog.Has(skillId);

        /// <summary>
        /// Domain 6: true when the skill is tree-gated (has a prerequisite entry in skill_tree.json). Base skills
        /// return false and are always trainable.
        /// </summary>
        public bool IsGated(string skillId) => _prereqs.Has(skillId);

        /// <summary>Returns the unlocked set (idempotent copy).</summary>
        public GdDict GetUnlocked() => _unlocked.ShallowCopy();

        public bool IsUnlocked(string skillId) => _unlocked.Has(skillId) && V.Bool(_unlocked[skillId]);

        /// <summary>
        /// Checks whether <paramref name="skillId"/> is currently unlockable, given a PlayerProgressionState (for level
        /// checks) and a MetaProgressionState (for book reads when books survive across runs).
        ///
        /// Rules:
        ///   1. Skill must be known.
        ///   2. Skill must not already be unlocked.
        ///   3. Every prerequisite skill must have level &gt;= its min_level.
        ///   4. If a book_prerequisite is recorded, the book must be read in the progression's <c>books_read</c> set.
        ///
        /// Returns a Dictionary {can: bool, reason: String, missing: Array} so the UI can show why a skill is locked.
        /// </summary>
        public GdDict CanUnlock(string skillId, object progression, object metaState = null)
        {
            if (!IsKnownSkill(skillId))
                return new GdDict { { "can", false }, { "reason", "unknown_skill" }, { "missing", new GdArray() } };
            if (IsUnlocked(skillId))
                return new GdDict { { "can", false }, { "reason", "already_unlocked" }, { "missing", new GdArray() } };
            var missing = new GdArray();
            GdArray prereqs = GetPrerequisites(skillId);
            var levelSource = progression as ISkillLevelSource;
            foreach (object reqVariant in prereqs)
            {
                var req = (GdDict)reqVariant;
                string reqSid = V.Str(req.Get("skill_id", ""));
                long minLevel = V.I64(req.Get("min_level", 1L));
                if (levelSource == null)
                {
                    missing.Append(new GdDict { { "type", "skill_level" }, { "skill_id", reqSid }, { "min_level", minLevel }, { "current", 0L } });
                    continue;
                }
                long current = levelSource.GetSkillLevel(reqSid);
                if (current < minLevel)
                    missing.Append(new GdDict { { "type", "skill_level" }, { "skill_id", reqSid }, { "min_level", minLevel }, { "current", current } });
            }
            string bookPrereq = GetBookPrerequisite(skillId);
            if (bookPrereq.Length != 0)
            {
                bool read = false;
                if (progression is IBookReadSource books)
                    read = books.HasReadBook(bookPrereq);
                else if (metaState is ICodexEntrySource codex)
                    // Cross-run book carries: codex entry with the same id counts.
                    read = codex.IsCodexEntryUnlocked(bookPrereq);
                if (!read)
                    missing.Append(new GdDict { { "type", "book" }, { "book_id", bookPrereq } });
            }
            if (missing.IsEmpty)
                return new GdDict { { "can", true }, { "reason", "ok" }, { "missing", new GdArray() } };
            return new GdDict { { "can", false }, { "reason", "missing_prereqs" }, { "missing", missing } };
        }

        /// <summary>Records an unlock. Idempotent. Returns true on a state change, false when already unlocked or skill is unknown.</summary>
        public bool Unlock(string skillId)
        {
            if (!IsKnownSkill(skillId))
                return false;
            if (IsUnlocked(skillId))
                return false;
            _unlocked[skillId] = true;
            return true;
        }

        /// <summary>
        /// Returns every skill in the catalog (id + display_name + category + prereq count + unlocked flag) for the
        /// skill tree panel.
        /// </summary>
        public GdArray GetSkillEntries()
        {
            var output = new GdArray();
            foreach (object sidKey in _skillsCatalog.Keys)
            {
                string sid = V.Str(sidKey);
                GdDict entry = (V.Dict(_skillsCatalog[sidKey]) ?? new GdDict()).ShallowCopy();
                GdArray prereqs = GetPrerequisites(sid);
                output.Append(new GdDict
                {
                    { "skill_id", sidKey },
                    { "category", V.Str(entry.Get("category", "")) },
                    { "display_name", V.Str(entry.Get("display_name", sidKey)) },
                    { "prereq_count", (long)prereqs.Count },
                    { "book_prerequisite", GetBookPrerequisite(sid) },
                    { "unlocked", IsUnlocked(sid) },
                });
            }
            output.SortCustom((a, b) => GdString.Less(V.Str(((GdDict)a).Get("skill_id", "")), V.Str(((GdDict)b).Get("skill_id", ""))));
            return output;
        }

        /// <summary>Reset for a fresh run. The <c>unlocked</c> set is per-run; the catalogs persist.</summary>
        public void ResetUnlocks()
        {
            _unlocked.Clear();
        }

        public GdDict ToDict()
        {
            return new GdDict
            {
                { "unlocked", _unlocked.ShallowCopy() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null)
                return false;
            _unlocked.Clear();
            object variant = summary.Get("unlocked", new GdDict());
            if (variant is GdDict unlockedDict)
            {
                foreach (var kv in unlockedDict)
                {
                    if (V.Bool(kv.Value))
                        _unlocked[V.Str(kv.Key)] = true;
                }
            }
            return true;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "unlocked_count", (long)_unlocked.Count },
                { "unlocked", _unlocked.ShallowCopy() },
            };
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            var lines = new List<string>();
            lines.Add("Skill Tree: " + GdString.FormatInt(_unlocked.Count) + " / " + GdString.FormatInt(_skillsCatalog.Count) + " unlocked");
            return lines;
        }
    }
}
