// Ported from scripts/systems/fire_suppression_state.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Authoritative, compartment-keyed, persist-until-extinguished fire model (ADR-0041).
    /// Fire is a SYMPTOM of unrepaired system damage: a compartment ignites only when its mapped system is damaged
    /// AND it has oxygen, and re-ignites until repaired or vented. Pure model; the coordinator renders passable
    /// fire-zone nodes from <c>active_fires</c>. <see cref="ApplySummary"/> restores dynamic state and, since the
    /// topology round-trip fix, the structural tunables too.
    /// </summary>
    public sealed class FireSuppressionState : ISimModel, ITickable, IStatusLineProvider
    {
        public const double DEFAULT_SUPPRESSANT_UNITS = 100.0;
        public const double DEFAULT_SUPPRESSION_RATE = 25.0;
        public const double DEFAULT_POWER_THRESHOLD = 0.5;
        public const double DEFAULT_SPREAD_RATE = 0.15;
        public const double DEFAULT_IGNITION_RATE = 0.2;
        public const double DEFAULT_CASCADE_RATE = 0.5;
        public const string DEFAULT_ARC_COMPARTMENT = "engineering";
        public const double MIN_INTENSITY = 0.1;
        public const double MAX_INTENSITY = 10.0;
        // Powered suppression removes suppression_rate_per_second * SUPPRESSION_INTENSITY_FACTOR intensity/sec
        // (~1.0/s at defaults: 25.0 * 0.04).
        public const double SUPPRESSION_INTENSITY_FACTOR = 0.04;
        public const double SUPPRESSANT_DRAIN_PER_SECOND = 0.5;

        public GdArray Compartments = new GdArray();          // Array[String]
        public GdDict ActiveFires = new GdDict();              // compartment_id -> intensity (float)
        public double SuppressantUnits = DEFAULT_SUPPRESSANT_UNITS;
        public double SuppressionRatePerSecond = DEFAULT_SUPPRESSION_RATE;
        public double PowerThreshold = DEFAULT_POWER_THRESHOLD;
        public GdDict Adjacency = new GdDict();                // compartment_id -> Array[String]
        public double SpreadRatePerSecond = DEFAULT_SPREAD_RATE;
        public double IgnitionRatePerSecond = DEFAULT_IGNITION_RATE;
        public double CascadeRatePerSecond = DEFAULT_CASCADE_RATE;
        public string ArcCompartment = DEFAULT_ARC_COMPARTMENT;

        public GdDict SpreadProgress = new GdDict();           // compartment_id -> float accumulator
        public GdDict IgnitionProgress = new GdDict();         // compartment_id -> float accumulator
        public double CascadeProgress = 0.0;
        // Fire B2: deliberate vents and door-gated spread (closed bulkhead links block adjacency).
        public GdDict VentedCompartments = new GdDict();       // compartment_id -> true
        public GdDict ClosedLinks = new GdDict();              // "a|b" sorted key -> true

        public void Configure(GdDict config)
        {
            if (config == null) config = new GdDict();
            Compartments.Clear();
            foreach (object entry in config.GetArrayOrEmpty("compartments"))
                Compartments.Append(V.Str(entry));
            ActiveFires.Clear();
            SpreadProgress.Clear();
            IgnitionProgress.Clear();
            CascadeProgress = 0.0;
            VentedCompartments.Clear();
            ClosedLinks.Clear();
            if (config.Get("closed_links", new GdArray()) is GdArray closedArr)
            {
                foreach (object pair in closedArr)
                {
                    if (pair is GdArray p && p.Count >= 2)
                        SetLinkClosed(V.Str(p[0]), V.Str(p[1]), true);
                }
            }
            if (config.Get("vented_compartments", new GdArray()) is GdArray ventedArr)
            {
                foreach (object cid in ventedArr) SetVented(V.Str(cid), true);
            }
            SuppressantUnits = Math.Max(0.0, V.F64(config.Get("suppressant_units", DEFAULT_SUPPRESSANT_UNITS)));
            SuppressionRatePerSecond = Math.Max(0.1, V.F64(config.Get("suppression_rate_per_second", DEFAULT_SUPPRESSION_RATE)));
            PowerThreshold = GdMath.Clampf(V.F64(config.Get("power_threshold", DEFAULT_POWER_THRESHOLD)), 0.05, 1.0);
            SpreadRatePerSecond = Math.Max(0.0, V.F64(config.Get("spread_rate_per_second", DEFAULT_SPREAD_RATE)));
            IgnitionRatePerSecond = Math.Max(0.0, V.F64(config.Get("ignition_rate_per_second", DEFAULT_IGNITION_RATE)));
            CascadeRatePerSecond = Math.Max(0.0, V.F64(config.Get("cascade_rate_per_second", DEFAULT_CASCADE_RATE)));
            ArcCompartment = V.Str(config.Get("arc_compartment", DEFAULT_ARC_COMPARTMENT));
            Adjacency.Clear();
            if (config.Get("adjacency", new GdDict()) is GdDict adj)
            {
                foreach (var kv in adj)
                {
                    var neighbours = new GdArray();
                    if (kv.Value is GdArray list)
                        foreach (object n in list) neighbours.Append(V.Str(n));
                    Adjacency[V.Str(kv.Key)] = neighbours;
                }
            }
        }

        public bool Ignite(string compartmentId, double intensity = 1.0)
        {
            if (string.IsNullOrEmpty(compartmentId)) return false;
            ActiveFires[compartmentId] = GdMath.Clampf(V.F64(ActiveFires.Get(compartmentId, 0.0)) + intensity, MIN_INTENSITY, MAX_INTENSITY);
            return true;
        }

        public bool Extinguish(string compartmentId)
        {
            if (!ActiveFires.Has(compartmentId)) return false;
            ActiveFires.Erase(compartmentId);
            SpreadProgress.Erase(compartmentId);
            // Clear stale spread accumulators around the extinguished fire, but only for neighbours that no longer
            // have ANY burning adjacent source (Gemini PR #42).
            foreach (object adj in Adjacent(compartmentId))
            {
                if (!HasBurningNeighbour(V.Str(adj))) SpreadProgress.Erase(adj);
            }
            return true;
        }

        public bool IsBurning(string compartmentId) => ActiveFires.Has(compartmentId);

        public GdArray GetBurningCompartments() => new GdArray(ActiveFires.Keys);

        public double GetIntensity(string compartmentId) => V.F64(ActiveFires.Get(compartmentId, 0.0));

        public long GetActiveFireCount() => ActiveFires.Count;

        /// <summary>Sum of active fire intensities (Fire B2: drives oxygen consumption).</summary>
        public double GetTotalIntensity()
        {
            double total = 0.0;
            foreach (var kv in ActiveFires) total += V.F64(kv.Value);
            return total;
        }

        /// <summary>Fire B2: deliberate vent (vacuum without a hull rupture). Returns true when state changed.</summary>
        public bool SetVented(string compartmentId, bool vented = true)
        {
            if (string.IsNullOrEmpty(compartmentId)) return false;
            if (vented)
            {
                if (VentedCompartments.Has(compartmentId)) return false;
                VentedCompartments[compartmentId] = true;
                if (ActiveFires.Has(compartmentId)) Extinguish(compartmentId);
                return true;
            }
            if (!VentedCompartments.Has(compartmentId)) return false;
            VentedCompartments.Erase(compartmentId);
            return true;
        }

        public bool IsVented(string compartmentId) => VentedCompartments.Has(compartmentId);

        public bool DeliberateVent(string compartmentId) => SetVented(compartmentId, true);

        /// <summary>Fire B2: door/bulkhead gating. Closed links block spread in both directions.</summary>
        public void SetLinkClosed(string a, string b, bool closed = true)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == b) return;
            string key = LinkKey(a, b);
            if (closed)
                ClosedLinks[key] = true;
            else
                ClosedLinks.Erase(key);
        }

        public bool IsLinkClosed(string a, string b) => ClosedLinks.Has(LinkKey(a, b));

        static string LinkKey(string a, string b) => GdString.Less(a, b) ? a + "|" + b : b + "|" + a;

        public bool Tick(double delta, GdDict context)
        {
            if (delta <= 0.0) return false;
            if (context == null) context = new GdDict();
            bool changed = false;
            GdDict breached = ToSet(context.Get(SimKeys.BreachedCompartments, new GdArray()));
            GdDict damaged = ToSet(context.Get(SimKeys.DamagedCompartments, new GdArray()));
            bool shipOxygen = V.Bool(context.Get(SimKeys.ShipOxygenPresent, true));
            double poweredRatio = V.F64(context.Get(SimKeys.PoweredRatio, 0.0));
            bool arcArcing = V.Bool(context.Get(SimKeys.ArcArcing, false));
            // Optional per-tick closed-link override (merges onto model closed_links).
            GdDict ctxClosed = ToSet(context.Get(SimKeys.ClosedLinks, new GdArray()));

            // 1. Vent / oxygen-loss extinguish (breach OR deliberate vent).
            foreach (object cid in new List<object>(ActiveFires.Keys))
            {
                if (!HasOxygen(V.Str(cid), shipOxygen, breached))
                {
                    Extinguish(V.Str(cid));
                    changed = true;
                }
            }

            // 2. Powered auto-suppression.
            if (poweredRatio >= PowerThreshold && SuppressantUnits > 0.0 && !ActiveFires.IsEmpty)
            {
                foreach (object cid in new List<object>(ActiveFires.Keys))
                {
                    double reduced = V.F64(ActiveFires[cid]) - SuppressionRatePerSecond * SUPPRESSION_INTENSITY_FACTOR * delta;
                    SuppressantUnits = Math.Max(0.0, SuppressantUnits - SUPPRESSANT_DRAIN_PER_SECOND * delta);
                    if (reduced <= 0.0)
                        Extinguish(V.Str(cid));
                    else
                        ActiveFires[cid] = reduced;
                    changed = true;
                }
            }

            // 3. Spread to oxygenated, non-burning adjacent compartments (door-gated).
            var spreadIgnites = new List<object>();
            foreach (object cid in new List<object>(ActiveFires.Keys))
            {
                double intensity = V.F64(ActiveFires[cid]);
                foreach (object adj in Adjacent(V.Str(cid)))
                {
                    string adjId = V.Str(adj);
                    if (ActiveFires.Has(adj) || !HasOxygen(adjId, shipOxygen, breached)
                        || IsLinkClosed(V.Str(cid), adjId) || ctxClosed.Has(LinkKey(V.Str(cid), adjId)))
                    {
                        SpreadProgress.Erase(adj);
                        continue;
                    }
                    double p = V.F64(SpreadProgress.Get(adj, 0.0)) + SpreadRatePerSecond * delta * intensity;
                    if (p >= 1.0)
                    {
                        spreadIgnites.Add(adj);
                        SpreadProgress.Erase(adj);
                    }
                    else
                    {
                        SpreadProgress[adj] = p;
                    }
                }
            }
            foreach (object adj in spreadIgnites)
            {
                if (!ActiveFires.Has(adj))
                {
                    ActiveFires[adj] = 1.0;
                    changed = true;
                }
            }

            // 4. Ignition from unrepaired damage (re-ignites until repaired/vented).
            foreach (object cidVariant in Compartments)
            {
                string cid = V.Str(cidVariant);
                bool ignitable = damaged.Has(cid) && HasOxygen(cid, shipOxygen, breached) && !ActiveFires.Has(cid);
                if (ignitable)
                {
                    double p2 = V.F64(IgnitionProgress.Get(cid, 0.0)) + IgnitionRatePerSecond * delta;
                    if (p2 >= 1.0)
                    {
                        ActiveFires[cid] = 1.0;
                        IgnitionProgress.Erase(cid);
                        changed = true;
                    }
                    else
                    {
                        IgnitionProgress[cid] = p2;
                    }
                }
                else if (IgnitionProgress.Has(cid))
                {
                    IgnitionProgress.Erase(cid);
                }
            }

            // 5. Arc cascade.
            if (arcArcing && !ActiveFires.Has(ArcCompartment) && HasOxygen(ArcCompartment, shipOxygen, breached))
            {
                CascadeProgress += CascadeRatePerSecond * delta;
                if (CascadeProgress >= 1.0)
                {
                    ActiveFires[ArcCompartment] = 1.0;
                    CascadeProgress = 0.0;
                    changed = true;
                }
            }
            else
            {
                CascadeProgress = 0.0;
            }

            return changed;
        }

        public GdDict GetSummary()
        {
            return new GdDict
            {
                { "compartments", Compartments.ShallowCopy() },
                { "active_fires", ActiveFires.DeepCopy() },
                { "suppressant_units", SuppressantUnits },
                { "suppression_rate_per_second", SuppressionRatePerSecond },
                { "power_threshold", PowerThreshold },
                { "adjacency", Adjacency.DeepCopy() },
                { "spread_rate_per_second", SpreadRatePerSecond },
                { "ignition_rate_per_second", IgnitionRatePerSecond },
                { "cascade_rate_per_second", CascadeRatePerSecond },
                { "arc_compartment", ArcCompartment },
                { "spread_progress", SpreadProgress.DeepCopy() },
                { "ignition_progress", IgnitionProgress.DeepCopy() },
                { "cascade_progress", CascadeProgress },
                { "vented_compartments", new GdArray(VentedCompartments.Keys) },
                { "closed_links", new GdArray(ClosedLinks.Keys) },
            };
        }

        public bool ApplySummary(GdDict summary)
        {
            if (summary == null || summary.IsEmpty) return false;
            bool changed = false;
            if (summary.Get("active_fires") is GdDict fires && !V.VariantEquals(fires, ActiveFires))
            {
                ActiveFires = fires.DeepCopy();
                changed = true;
            }
            if (summary.Get("spread_progress") is GdDict sp && !V.VariantEquals(sp, SpreadProgress))
            {
                SpreadProgress = sp.DeepCopy();
                changed = true;
            }
            if (summary.Get("ignition_progress") is GdDict ip && !V.VariantEquals(ip, IgnitionProgress))
            {
                IgnitionProgress = ip.DeepCopy();
                changed = true;
            }
            double newSuppressant = V.F64(summary.Get("suppressant_units", SuppressantUnits));
            if (Math.Abs(newSuppressant - SuppressantUnits) > 0.001)
            {
                SuppressantUnits = newSuppressant;
                changed = true;
            }
            double newCascade = V.F64(summary.Get("cascade_progress", CascadeProgress));
            if (Math.Abs(newCascade - CascadeProgress) > 0.001)
            {
                CascadeProgress = newCascade;
                changed = true;
            }
            // Tunables (round-trip but rarely change at runtime).
            if (summary.Has("arc_compartment") && V.Str(summary["arc_compartment"]) != ArcCompartment)
            {
                ArcCompartment = V.Str(summary["arc_compartment"]);
                changed = true;
            }
            // Round-trip spread topology + rate tunables (per-ship derelict fire restores straight from its summary).
            if (summary.Get("compartments") is GdArray comps)
            {
                var newComps = new GdArray();
                foreach (object c in comps) newComps.Append(V.Str(c));
                if (!V.VariantEquals(newComps, Compartments))
                {
                    Compartments = newComps;
                    changed = true;
                }
            }
            if (summary.Get("adjacency") is GdDict adj)
            {
                var newAdj = new GdDict();
                foreach (var kv in adj)
                {
                    var neighbours = new GdArray();
                    if (kv.Value is GdArray lst)
                        foreach (object n in lst) neighbours.Append(V.Str(n));
                    newAdj[V.Str(kv.Key)] = neighbours;
                }
                if (!V.VariantEquals(newAdj, Adjacency))
                {
                    Adjacency = newAdj;
                    changed = true;
                }
            }
            if (summary.Has("suppression_rate_per_second"))
            {
                double newSuppRate = Math.Max(0.1, V.F64(summary["suppression_rate_per_second"]));
                if (Math.Abs(newSuppRate - SuppressionRatePerSecond) > 0.001)
                {
                    SuppressionRatePerSecond = newSuppRate;
                    changed = true;
                }
            }
            if (summary.Has("power_threshold"))
            {
                double newThreshold = GdMath.Clampf(V.F64(summary["power_threshold"]), 0.05, 1.0);
                if (Math.Abs(newThreshold - PowerThreshold) > 0.001)
                {
                    PowerThreshold = newThreshold;
                    changed = true;
                }
            }
            if (summary.Has("spread_rate_per_second"))
            {
                double newSpreadRate = Math.Max(0.0, V.F64(summary["spread_rate_per_second"]));
                if (Math.Abs(newSpreadRate - SpreadRatePerSecond) > 0.001)
                {
                    SpreadRatePerSecond = newSpreadRate;
                    changed = true;
                }
            }
            if (summary.Has("ignition_rate_per_second"))
            {
                double newIgnitionRate = Math.Max(0.0, V.F64(summary["ignition_rate_per_second"]));
                if (Math.Abs(newIgnitionRate - IgnitionRatePerSecond) > 0.001)
                {
                    IgnitionRatePerSecond = newIgnitionRate;
                    changed = true;
                }
            }
            if (summary.Has("cascade_rate_per_second"))
            {
                double newCascadeRate = Math.Max(0.0, V.F64(summary["cascade_rate_per_second"]));
                if (Math.Abs(newCascadeRate - CascadeRatePerSecond) > 0.001)
                {
                    CascadeRatePerSecond = newCascadeRate;
                    changed = true;
                }
            }
            // Fire B2: round-trip deliberate vents + model-level closed bulkhead links.
            if (summary.Get("vented_compartments") is GdArray ventedRaw)
            {
                var newVented = new GdDict();
                foreach (object cid in ventedRaw)
                {
                    string c = V.Str(cid);
                    if (c.Length != 0) newVented[c] = true;
                }
                if (!V.VariantEquals(newVented, VentedCompartments))
                {
                    VentedCompartments = newVented;
                    changed = true;
                }
            }
            if (summary.Get("closed_links") is GdArray closedRaw)
            {
                var newClosed = new GdDict();
                foreach (object key in closedRaw)
                {
                    string k = V.Str(key);
                    if (k.Length != 0) newClosed[k] = true;
                }
                if (!V.VariantEquals(newClosed, ClosedLinks))
                {
                    ClosedLinks = newClosed;
                    changed = true;
                }
            }
            return changed;
        }

        IReadOnlyList<string> IStatusLineProvider.GetStatusLines() => GetStatusLines();

        public List<string> GetStatusLines()
        {
            var lines = new List<string>
            {
                "Fire Suppression fires=" + GdString.FormatInt(GetActiveFireCount()) + " suppressant=" + GdString.FormatFixed(SuppressantUnits, 1),
            };
            foreach (var kv in ActiveFires)
                lines.Add("Fire " + V.Str(kv.Key) + " intensity=" + GdString.FormatFixed(V.F64(kv.Value), 2));
            return lines;
        }

        GdArray Adjacent(string compartmentId)
        {
            object v = Adjacency.Get(compartmentId, new GdArray());
            return v as GdArray ?? new GdArray();
        }

        /// <summary>True if any compartment adjacent to <paramref name="compartmentId"/> is currently burning.</summary>
        bool HasBurningNeighbour(string compartmentId)
        {
            foreach (object other in Adjacent(compartmentId))
            {
                if (ActiveFires.Has(other)) return true;
            }
            return false;
        }

        bool HasOxygen(string compartmentId, bool shipOxygen, GdDict breached)
        {
            // Fire B2: deliberate vents count as vacuum for ignition/spread/extinguish.
            return shipOxygen && !breached.Has(compartmentId) && !VentedCompartments.Has(compartmentId);
        }

        static GdDict ToSet(object listVariant)
        {
            var outSet = new GdDict();
            if (listVariant is GdArray list)
                foreach (object entry in list) outSet[V.Str(entry)] = true;
            return outSet;
        }
    }
}
