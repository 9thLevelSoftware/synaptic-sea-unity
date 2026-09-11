// Ported from scripts/systems/hallucination_director.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Deterministic, pure-data scheduler for sanity-driven hallucinations (ADR-0042). Maps sanity to a tier (0..3)
    /// and schedules discrete manifestation events with NO RNG: selection is a seeded integer hash, so the same
    /// (seed, step, inputs) always yields the same stream. PKG-C3.3: kinds/entries load from
    /// <see cref="ManifestationPool"/> when available; narrative force-trigger hooks for rooms and audio logs.
    /// </summary>
    public sealed class HallucinationDirector : ISimModel
    {
        public const double TIER_UNEASE = 40.0;      // sanity < 40 -> tier 1
        public const double TIER_DISTORTION = 25.0;  // sanity < 25 -> tier 2
        public const double TIER_BREAKDOWN = 15.0;   // sanity < 15 -> tier 3

        public const double DEFAULT_HEALTH_DRAIN = 0.5;
        public const double DEFAULT_STAMINA_RECOVERY_MULT = 0.5;

        /// <summary>Fallback kind schedule if the pool fails to load (legacy ADR-0042 baseline). Never mutate.</summary>
        public static readonly GdDict KIND_CONFIG_FALLBACK = new GdDict
        {
            { "ambient", new GdDict { { "min_tier", 1L }, { "interval", 6.0 }, { "interval_t3", 4.0 }, { "max", 2L }, { "max_t3", 2L }, { "ttl", 3.0 } } },
            { "hud", new GdDict { { "min_tier", 2L }, { "interval", 5.0 }, { "interval_t3", 3.0 }, { "max", 3L }, { "max_t3", 3L }, { "ttl", 2.5 } } },
            { "phantom", new GdDict { { "min_tier", 2L }, { "interval", 8.0 }, { "interval_t3", 3.5 }, { "max", 1L }, { "max_t3", 3L }, { "ttl", 12.0 } } },
        };

        public long RngSeed = 0;
        public long Step = 0;
        public double HealthDrainPerSecond = DEFAULT_HEALTH_DRAIN;
        public double StaminaRecoveryMult = DEFAULT_STAMINA_RECOVERY_MULT;

        /// <summary>[{ id, kind, position (Vec3), ttl, entry_id, caption, audio_event }]</summary>
        public GdArray ActiveEvents = new GdArray();
        long _nextId = 1;
        GdDict _spawnTimers = new GdDict();  // kind -> float
        long _currentTier = 0;
        public ManifestationPool Pool = null;
        GdDict _kindConfig = new GdDict();

        public void Configure(GdDict config = null)
        {
            if (config == null) config = new GdDict();
            RngSeed = V.I64(config.Get("seed", 0L));
            Step = 0;
            HealthDrainPerSecond = Math.Max(0.0, V.F64(config.Get("health_drain_per_second", DEFAULT_HEALTH_DRAIN)));
            StaminaRecoveryMult = GdMath.Clampf(V.F64(config.Get("stamina_recovery_mult", DEFAULT_STAMINA_RECOVERY_MULT)), 0.0, 1.0);
            ActiveEvents.Clear();
            _spawnTimers.Clear();
            _nextId = 1;
            _currentTier = 0;
            LoadPool(V.Bool(config.Get("load_pool", true)));
        }

        void LoadPool(bool enabled)
        {
            Pool = null;
            _kindConfig = KIND_CONFIG_FALLBACK.DeepCopy();
            if (!enabled) return;
            var p = new ManifestationPool();
            if (p.LoadDefault())
            {
                Pool = p;
                _kindConfig = p.KindScheduleConfig();
                if (_kindConfig.IsEmpty) _kindConfig = KIND_CONFIG_FALLBACK.DeepCopy();
            }
        }

        /// <summary>Context keys: sanity, in_safe_zone, anchor_positions (Array of Vec3). Returns true when events changed.</summary>
        public bool Tick(double delta, GdDict context)
        {
            if (delta <= 0.0) return false;
            if (context == null) context = new GdDict();
            bool changed = false;
            double sanity = V.F64(context.Get(SimKeys.Sanity, 100.0));
            bool inSafeZone = V.Bool(context.Get(SimKeys.InSafeZone, false));
            GdArray anchors = context.Get(SimKeys.AnchorPositions, new GdArray()) as GdArray ?? new GdArray();
            _currentTier = TierFor(sanity);

            if (inSafeZone) _currentTier = 0;
            if (_currentTier == 0)
            {
                if (!ActiveEvents.IsEmpty)
                {
                    ActiveEvents.Clear();
                    changed = true;
                }
                _spawnTimers.Clear();
                Step += 1;
                return changed;
            }

            for (int i = ActiveEvents.Count - 1; i >= 0; i--)
            {
                var ev = (GdDict)ActiveEvents[i];
                ev["ttl"] = V.F64(ev["ttl"]) - delta;
                if (V.F64(ev["ttl"]) <= 0.0)
                {
                    ActiveEvents.RemoveAt(i);
                    changed = true;
                }
            }

            if (anchors.IsEmpty)
            {
                Step += 1;
                return changed;
            }

            foreach (object kind in new List<object>(_kindConfig.Keys))
            {
                var cfg = (GdDict)_kindConfig[kind];
                if (_currentTier < V.I64(cfg["min_tier"])) continue;
                double interval = _currentTier >= 3 ? V.F64(cfg["interval_t3"]) : V.F64(cfg["interval"]);
                long cap = _currentTier >= 3 ? V.I64(cfg["max_t3"]) : V.I64(cfg["max"]);
                _spawnTimers[kind] = V.F64(_spawnTimers.Get(kind, 0.0)) + delta;
                if (V.F64(_spawnTimers[kind]) >= interval && CountKind(V.Str(kind)) < cap)
                {
                    _spawnTimers[kind] = V.F64(_spawnTimers[kind]) - interval;
                    int idx = (int)PickIndex(V.Str(kind), anchors.Count);
                    string entryId = "";
                    string caption = "";
                    string audioEvent = "";
                    if (Pool != null)
                    {
                        long h = unchecked(RngSeed * 1103515245L + Step * 12345L + GodotHash.StringHash(V.Str(kind)));
                        entryId = Pool.PickEntryId(V.Str(kind), _currentTier, h);
                        if (entryId.Length != 0)
                        {
                            GdDict ent = Pool.GetEntry(entryId);
                            caption = V.Str(ent.Get("caption", ""));
                            audioEvent = V.Str(ent.Get("audio_event", ""));
                        }
                    }
                    ActiveEvents.Append(new GdDict
                    {
                        { "id", _nextId },
                        { "kind", V.Str(kind) },
                        { "position", anchors[idx] },
                        { "ttl", V.F64(cfg["ttl"]) },
                        { "entry_id", entryId },
                        { "caption", caption },
                        { "audio_event", audioEvent },
                    });
                    _nextId += 1;
                    changed = true;
                }
            }

            Step += 1;
            return changed;
        }

        /// <summary>PKG-C3.3: force a catalog entry by id (room/audio-log narrative). Returns the event id or -1.</summary>
        public long ForceTrigger(string entryId, object position = null, double ttlOverride = -1.0)
        {
            if (Pool == null || string.IsNullOrEmpty(entryId)) return -1;
            if (!Pool.HasEntry(entryId)) return -1;
            GdDict ent = Pool.GetEntry(entryId);
            string kind = V.Str(ent.Get("kind", ""));
            if (kind.Length == 0) return -1;
            double ttl = V.F64(ent.Get("ttl", -1.0));
            if (ttl <= 0.0 && _kindConfig.Has(kind))
                ttl = V.F64(((GdDict)_kindConfig[kind]).Get("ttl", 3.0));
            if (ttlOverride > 0.0) ttl = ttlOverride;
            if (ttl <= 0.0) ttl = 3.0;
            object pos = position;
            if (pos == null) pos = Vec3.Zero;
            var ev = new GdDict
            {
                { "id", _nextId },
                { "kind", kind },
                { "position", pos },
                { "ttl", ttl },
                { "entry_id", entryId },
                { "caption", V.Str(ent.Get("caption", "")) },
                { "audio_event", V.Str(ent.Get("audio_event", "")) },
                { "forced", true },
            };
            ActiveEvents.Append(ev);
            _nextId += 1;
            return V.I64(ev["id"]);
        }

        /// <summary>Force all narrative entries bound to a room id. Returns the count spawned.</summary>
        public long ForceRoomTriggers(string roomId, object position = null)
        {
            if (Pool == null) return 0;
            GdArray ids = Pool.ForceEntriesForRoom(roomId);
            long n = 0;
            foreach (object eid in ids)
            {
                if (ForceTrigger(V.Str(eid), position) >= 0) n += 1;
            }
            return n;
        }

        /// <summary>Force all narrative entries bound to an audio log id. Returns the count spawned.</summary>
        public long ForceAudioLogTriggers(string logId, object position = null)
        {
            if (Pool == null) return 0;
            GdArray ids = Pool.ForceEntriesForAudioLog(logId);
            long n = 0;
            foreach (object eid in ids)
            {
                if (ForceTrigger(V.Str(eid), position) >= 0) n += 1;
            }
            return n;
        }

        public long GetTier() => _currentTier;

        public GdArray GetActiveEvents(string kind = "")
        {
            if (string.IsNullOrEmpty(kind)) return ActiveEvents.DeepCopy();
            var outArr = new GdArray();
            foreach (object e in ActiveEvents)
            {
                var ed = (GdDict)e;
                if (V.Str(ed["kind"]) == kind) outArr.Append(ed.DeepCopy());
            }
            return outArr;
        }

        public void RemoveEvent(long id)
        {
            for (int i = 0; i < ActiveEvents.Count; i++)
            {
                if (V.I64(((GdDict)ActiveEvents[i])["id"]) == id)
                {
                    ActiveEvents.RemoveAt(i);
                    return;
                }
            }
        }

        public GdDict GetDirectTeeth()
        {
            if (_currentTier >= 3)
                return new GdDict { { "health_drain_per_second", HealthDrainPerSecond }, { "stamina_recovery_mult", StaminaRecoveryMult } };
            return new GdDict { { "health_drain_per_second", 0.0 }, { "stamina_recovery_mult", 1.0 } };
        }

        public double GetFxIntensity() => GdMath.Clampf(_currentTier / 3.0, 0.0, 1.0);

        public GdDict GetSummary()
        {
            var eventsOut = new GdArray();
            foreach (object e in ActiveEvents)
            {
                if (!(e is GdDict ed)) continue;
                GdDict copy = ed.DeepCopy();
                object pos = copy.Get("position");
                if (pos is Vec3 v)
                    copy["position"] = GdArray.Of((double)v.X, (double)v.Y, (double)v.Z);
                eventsOut.Append(copy);
            }
            var timersOut = new GdDict();
            foreach (var kv in _spawnTimers)
                timersOut[V.Str(kv.Key)] = Math.Max(0.0, V.F64(kv.Value));
            return new GdDict
            {
                { "seed", RngSeed },
                { "step", Step },
                { "health_drain_per_second", HealthDrainPerSecond },
                { "stamina_recovery_mult", StaminaRecoveryMult },
                { "active_events", eventsOut },
                { "spawn_timers", timersOut },
                { "current_tier", _currentTier },
                { "pool_loaded", Pool != null },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            // PKG-D8 / C3.3: ensure the kind schedule is loaded before restoring spawn_timers
            // (apply_summary may run without a prior configure() on a fresh director).
            if (_kindConfig.IsEmpty) LoadPool(true);
            RngSeed = V.I64(summary.Get("seed", RngSeed));
            Step = V.I64(summary.Get("step", Step));
            HealthDrainPerSecond = Math.Max(0.0, V.F64(summary.Get("health_drain_per_second", HealthDrainPerSecond)));
            StaminaRecoveryMult = GdMath.Clampf(V.F64(summary.Get("stamina_recovery_mult", StaminaRecoveryMult)), 0.0, 1.0);
            if (summary.Get("active_events") is GdArray rawEvents)
            {
                ActiveEvents.Clear();
                long maxId = 0;
                foreach (object raw in rawEvents)
                {
                    if (!(raw is GdDict rawDict)) continue;
                    GdDict e = rawDict.DeepCopy();
                    object pos = e.Get("position");
                    if (pos is GdArray pa && pa.Count >= 3)
                    {
                        if (!(V.IsNumber(pa[0]) && V.IsNumber(pa[1]) && V.IsNumber(pa[2]))) continue;
                        e["position"] = new Vec3(V.F64(pa[0]), V.F64(pa[1]), V.F64(pa[2]));
                    }
                    else if (!(pos is Vec3))
                    {
                        continue;
                    }
                    ActiveEvents.Append(e);
                    maxId = Math.Max(maxId, V.I64(e.Get("id", 0L)));
                }
                _nextId = maxId + 1;
            }
            if (summary.Get("spawn_timers") is GdDict timers)
            {
                _spawnTimers.Clear();
                foreach (var kv in timers)
                {
                    string kind = V.Str(kv.Key);
                    if (!_kindConfig.Has(kind)) continue;
                    if (V.IsNumber(kv.Value)) _spawnTimers[kind] = Math.Max(0.0, V.F64(kv.Value));
                }
            }
            _currentTier = V.I64(summary.Get("current_tier", _currentTier));
            return true;
        }

        static long TierFor(double sanity)
        {
            if (sanity < TIER_BREAKDOWN) return 3;
            if (sanity < TIER_DISTORTION) return 2;
            if (sanity < TIER_UNEASE) return 1;
            return 0;
        }

        long CountKind(string kind)
        {
            long n = 0;
            foreach (object e in ActiveEvents)
            {
                if (V.Str(((GdDict)e)["kind"]) == kind) n += 1;
            }
            return n;
        }

        long PickIndex(string kind, long count)
        {
            if (count <= 0) return 0;
            long h = unchecked(RngSeed * 1103515245L + Step * 12345L + GodotHash.StringHash(kind));
            h = (h ^ (h >> 16)) & 0x7fffffff;
            return h % count;
        }
    }
}
