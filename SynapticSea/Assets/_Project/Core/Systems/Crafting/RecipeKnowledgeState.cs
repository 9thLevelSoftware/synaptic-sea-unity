// Ported from scripts/systems/recipe_knowledge_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// PKG-B2.4a: known/unknown recipe gating + reverse-engineer discovery.
    /// Recipes without knowledge_source (or source=starter) are known by default
    /// for backward compatibility with the existing 60-recipe catalog.
    /// </summary>
    public sealed class RecipeKnowledgeState
    {
        public const string SOURCE_STARTER = "starter";
        public const string SOURCE_BOOK = "book";
        public const string SOURCE_CODEX = "codex";
        public const string SOURCE_REVERSE = "reverse_engineer";

        /// <summary>recipe_id -> true when learned.</summary>
        GdDict _known = new GdDict();
        /// <summary>component_id -> dismantle count for reverse-engineer.</summary>
        GdDict _dismantleCounts = new GdDict();
        /// <summary>reverse_engineer: recipe_id -> {component_id, need}.</summary>
        readonly GdDict _reverseTargets = new GdDict();

        public void Clear()
        {
            _known.Clear();
            _dismantleCounts.Clear();
            _reverseTargets.Clear();
        }

        /// <summary>Seed knowledge from full recipe catalog dict (recipe_id -> recipe).</summary>
        public void SeedFromRecipes(GdDict recipes)
        {
            _reverseTargets.Clear();
            if (recipes == null) return;
            foreach (object rid in new List<object>(recipes.Keys))
            {
                if (!(recipes[rid] is GdDict r)) continue;
                string source = V.Str(r.Get("knowledge_source", SOURCE_STARTER));
                if (source.Length == 0 || source == SOURCE_STARTER)
                {
                    _known[V.Str(rid)] = true;
                }
                else if (source == SOURCE_REVERSE)
                {
                    long need = Math.Max(1L, V.I64(r.Get("reverse_engineer_count", 3L)));
                    string comp = V.Str(r.Get("reverse_engineer_component", ""));
                    _reverseTargets[V.Str(rid)] = new GdDict { { "component_id", comp }, { "need", need } };
                }
                // book/codex start unknown
            }
        }

        public bool IsKnown(string recipeId) => V.Bool(_known.Get(recipeId, false));

        public bool Learn(string recipeId)
        {
            if (string.IsNullOrEmpty(recipeId)) return false;
            if (_known.Has(recipeId)) return false;
            _known[recipeId] = true;
            return true;
        }

        public GdArray LearnFromBook(string bookId, GdDict recipes) =>
            LearnFromSource(recipes, SOURCE_BOOK, "knowledge_book_id", bookId);

        public GdArray LearnFromCodex(string codexId, GdDict recipes) =>
            LearnFromSource(recipes, SOURCE_CODEX, "knowledge_codex_id", codexId);

        GdArray LearnFromSource(GdDict recipes, string source, string idKey, string id)
        {
            var learned = new GdArray();
            if (recipes == null) return learned;
            foreach (object rid in new List<object>(recipes.Keys))
            {
                if (!(recipes[rid] is GdDict r)) continue;
                if (V.Str(r.Get("knowledge_source", "")) != source) continue;
                if (V.Str(r.Get(idKey, "")) != id) continue;
                if (Learn(V.Str(rid))) learned.Add(V.Str(rid));
            }
            return learned;
        }

        /// <summary>Register dismantling a component; unlock reverse-engineer recipes when count met.</summary>
        public GdArray RegisterDismantle(string componentId)
        {
            if (string.IsNullOrEmpty(componentId)) return new GdArray();
            _dismantleCounts[componentId] = V.I64(_dismantleCounts.Get(componentId, 0L)) + 1;
            long count = V.I64(_dismantleCounts[componentId]);
            var learned = new GdArray();
            foreach (object rid in new List<object>(_reverseTargets.Keys))
            {
                if (IsKnown(V.Str(rid))) continue;
                var tgt = _reverseTargets[rid] as GdDict ?? new GdDict();
                if (V.Str(tgt.Get("component_id", "")) != componentId) continue;
                if (count >= V.I64(tgt.Get("need", 3L)))
                {
                    if (Learn(V.Str(rid))) learned.Add(V.Str(rid));
                }
            }
            return learned;
        }

        public long KnownCount() => _known.Count;

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "known", _known.DeepCopy() },
                { "dismantle_counts", _dismantleCounts.DeepCopy() },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            if (summary.Get("known", new GdDict()) is GdDict k) _known = k.DeepCopy();
            if (summary.Get("dismantle_counts", new GdDict()) is GdDict d) _dismantleCounts = d.DeepCopy();
            return true;
        }
    }
}
