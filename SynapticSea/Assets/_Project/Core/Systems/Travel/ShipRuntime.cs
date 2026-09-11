// Ported from scripts/systems/ship_runtime.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Contracts;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// The optional per-runtime model ShipRuntime snapshots under "component_manifest" (Godot: a
    /// ComponentPlacementState probed with <c>has_method("get_summary"/"apply_summary")</c>). ComponentPlacementState's
    /// C# port should implement it.
    /// </summary>
    public interface IComponentManifestModel
    {
        GdDict GetSummary();
        bool ApplySummary(GdDict summary);
    }

    /// <summary>
    /// The GDScript <c>configure(ship_inst, opts)</c> options dictionary. It carries model references and a Callable,
    /// which a GdDict cannot hold, so the options are typed; keys map 1:1 (is_home, hull_override, web_override,
    /// contact_boost_provider, module_integrity, component_placement).
    /// </summary>
    public sealed class ShipRuntimeOptions
    {
        public bool IsHome;
        public HullIntegrityState HullOverride;
        public WebInfestationState WebOverride;

        /// <summary>Callable() -> bool: contact boost for hub web growth (attached derelict docked).</summary>
        public Func<bool> ContactBoostProvider;

        public ModuleIntegrityMap ModuleIntegrity;
        public IComponentManifestModel ComponentPlacement;
    }

    /// <summary>
    /// Per-ship simulation context (pre-polish PKG-A1a strangler). Owns the advance / catch-up seam formerly inlined
    /// on PlayableGeneratedShip. The coordinator still owns scene tree, player, UI, and hub-expanded recompute; this
    /// class is the per-ship systems + web/hull tick entry point. Hub ships may inject coordinator-owned hull/web
    /// models via Configure() because the home ship historically keeps those on the coordinator, not ShipInstance.
    /// </summary>
    public class ShipRuntime : ISnapshotable
    {
        public const double CATCHUP_SUBSTEP_SECONDS = 5.0;
        public const double MAX_CATCHUP_SECONDS = 1800.0;

        // PKG-A3 tick bands (accumulators; no balance retune of rates themselves).
        public const double SLOW_INTERVAL_SECONDS = 0.35;
        public const double LAZY_INTERVAL_SECONDS = 3.0;

        public ShipInstance Ship;
        public bool IsHome = false;

        /// <summary>HullIntegrityState override for hub; null → Ship.GetHull().</summary>
        public HullIntegrityState HullOverride;

        /// <summary>WebInfestationState override for hub; null → Ship.GetWeb().</summary>
        public WebInfestationState WebOverride;

        /// <summary>Callable() -> bool: contact boost for hub web growth (attached derelict docked).</summary>
        public Func<bool> ContactBoostProvider;

        /// <summary>PKG-B2.1b: optional ModuleIntegrityMap owned by this runtime.</summary>
        public ModuleIntegrityMap ModuleIntegrity;

        /// <summary>PKG-D6.1: optional ComponentPlacementState owned by this runtime.</summary>
        public IComponentManifestModel ComponentPlacement;

        double _slowAcc = 0.0;
        double _lazyAcc = 0.0;

        // Diagnostic counters (smokes / balance tools).
        public long FrameBandFires = 0;
        public long SlowBandFires = 0;
        public long LazyBandFires = 0;

        public void Configure(ShipInstance shipInst, ShipRuntimeOptions opts = null)
        {
            opts = opts ?? new ShipRuntimeOptions();
            Ship = shipInst;
            IsHome = opts.IsHome;
            HullOverride = opts.HullOverride;
            WebOverride = opts.WebOverride;
            ContactBoostProvider = opts.ContactBoostProvider;
            ModuleIntegrity = opts.ModuleIntegrity;
            ComponentPlacement = opts.ComponentPlacement;
            _slowAcc = 0.0;
            _lazyAcc = 0.0;
            FrameBandFires = 0;
            SlowBandFires = 0;
            LazyBandFires = 0;
        }

        public ShipInstance GetShip() => Ship;

        HullIntegrityState ResolveHull()
        {
            if (HullOverride != null)
                return HullOverride;
            if (Ship != null)
                return Ship.GetHull();
            return null;
        }

        WebInfestationState ResolveWeb()
        {
            if (WebOverride != null)
                return WebOverride;
            if (Ship != null)
                return Ship.GetWeb();
            return null;
        }

        bool ContactBoost()
        {
            if (!IsHome)
                return false;
            if (ContactBoostProvider != null)
                return ContactBoostProvider();
            return false;
        }

        /// <summary>
        /// Advance ONE ship's systems manager + biomatter-web hull damage (FRAME band).
        /// Does NOT tick fire, expanded hub recompute, player, or UI.
        /// </summary>
        public void Advance(double delta, double worldTime)
        {
            if (Ship == null || delta < 0.0)
                return;
            FrameBandFires += 1;
            Ship.LastSimTime = worldTime;
            ShipSystemsManager systemsManager = Ship.SystemsManager;
            if (systemsManager != null)
                systemsManager.Advance(delta);
            WebInfestationState web = ResolveWeb();
            HullIntegrityState hull = ResolveHull();
            if (web == null || hull == null)
                return;
            bool contact = ContactBoost();
            double dmg = web.Tick(delta, contact);
            if (dmg <= 0.0)
                return;
            GdDict compartments = hull.Compartments;
            if (compartments == null)
                return;
            foreach (object cid in new List<object>(compartments.Keys))
                hull.DamageCompartment(V.Str(cid), dmg);
        }

        /// <summary>
        /// PKG-A3: accumulate delta and report which bands should fire this call.
        /// Returns { "frame": true, "slow": bool, "lazy": bool, "slow_dt": float, "lazy_dt": float }.
        /// </summary>
        public GdDict PollBands(double delta)
        {
            var result = new GdDict
            {
                { "frame", true },
                { "slow", false },
                { "lazy", false },
                { "slow_dt", 0.0 },
                { "lazy_dt", 0.0 },
            };
            if (delta <= 0.0)
            {
                result["frame"] = false;
                return result;
            }
            _slowAcc += delta;
            _lazyAcc += delta;
            if (_slowAcc >= SLOW_INTERVAL_SECONDS)
            {
                result["slow"] = true;
                result["slow_dt"] = _slowAcc;
                _slowAcc = 0.0;
                SlowBandFires += 1;
            }
            if (_lazyAcc >= LAZY_INTERVAL_SECONDS)
            {
                result["lazy"] = true;
                result["lazy_dt"] = _lazyAcc;
                _lazyAcc = 0.0;
                LazyBandFires += 1;
            }
            return result;
        }

        /// <summary>
        /// Fast-forward an absent ship by world_time - last_sim_time in capped sub-steps. Home ships are never
        /// catch-up targets (always present). PKG-A3: prefer LAZY quanta for inactive catch-up when gap is large;
        /// still bounded by CATCHUP_SUBSTEP_SECONDS so model rates stay stable.
        /// </summary>
        public void CatchUp(double worldTime)
        {
            if (Ship == null || IsHome)
                return;
            double last = Ship.LastSimTime;
            double dt = Math.Min(worldTime - last, MAX_CATCHUP_SECONDS);
            if (dt <= 0.0)
                return;
            Ship.LastSimTime = worldTime;
            double quantum = Math.Min(CATCHUP_SUBSTEP_SECONDS, LAZY_INTERVAL_SECONDS);
            while (dt > 0.0)
            {
                double step = Math.Min(quantum, dt);
                Advance(step, worldTime);
                LazyBandFires += 1;
                dt -= step;
            }
        }

        /// <summary>
        /// PKG-A1b: compose one ship's runtime state for higher-level snapshots. RunSnapshot/WorldSnapshot still own
        /// top-level schema; this is the per-ship composition unit (ship_summary + last_sim_time + extension slots).
        /// </summary>
        public GdDict ToSnapshot()
        {
            if (Ship == null)
                return new GdDict();
            string shipId = Ship.ShipId;
            double lastSimTime = Ship.LastSimTime;
            var output = new GdDict
            {
                { "schema", "ship_runtime_v1" },
                { "ship_id", shipId },
                { "last_sim_time", lastSimTime },
                { "is_home", IsHome },
                { "module_integrity", new GdDict() },
                { "component_manifest", new GdDict() },
            };
            output["ship_summary"] = Ship.GetSummary();
            if (ModuleIntegrity != null)
            {
                output["module_integrity"] = ModuleIntegrity.GetSummary();
            }
            else
            {
                GdDict shipMi = Ship.ModuleIntegritySummary;
                if (shipMi != null && !shipMi.IsEmpty)
                    output["module_integrity"] = shipMi.DeepCopy();
            }
            if (ComponentPlacement != null)
            {
                output["component_manifest"] = ComponentPlacement.GetSummary();
            }
            else
            {
                GdDict shipCp = Ship.ComponentPlacementSummary;
                if (shipCp != null && !shipCp.IsEmpty)
                    output["component_manifest"] = shipCp.DeepCopy();
            }
            return output;
        }

        public void FromSnapshot(GdDict data)
        {
            if (Ship == null || data == null || data.IsEmpty)
                return;
            if (data.Has("last_sim_time"))
                Ship.LastSimTime = V.F64(data.Get("last_sim_time", Ship.LastSimTime));
            if (data.Has("ship_summary"))
            {
                object summary = data.Get("ship_summary", new GdDict());
                if (summary is GdDict summaryDict && !summaryDict.IsEmpty)
                    Ship.ApplySummary(summaryDict);
            }
            if (data.Has("module_integrity"))
            {
                object mi = data.Get("module_integrity", new GdDict());
                if (mi is GdDict miDict)
                {
                    if (ModuleIntegrity != null)
                        ModuleIntegrity.ApplySummary(miDict);
                    // PKG-D6.1: always mirror onto ShipInstance sparse pack for revisit.
                    Ship.ModuleIntegritySummary = miDict.DeepCopy();
                }
            }
            if (data.Has("component_manifest"))
            {
                object cp = data.Get("component_manifest", new GdDict());
                if (cp is GdDict cpDict)
                {
                    if (ComponentPlacement != null)
                        ComponentPlacement.ApplySummary(cpDict);
                    Ship.ComponentPlacementSummary = cpDict.DeepCopy();
                }
            }
        }

        /// <summary>Compose multiple runtimes into one dictionary for multi-ship persistence tests.</summary>
        public static GdDict ComposeRuntimeSnapshots(IEnumerable<ShipRuntime> runtimes)
        {
            var ships = new GdArray();
            if (runtimes != null)
            {
                foreach (ShipRuntime rt in runtimes)
                {
                    if (rt != null)
                        ships.Add(rt.ToSnapshot());
                }
            }
            return new GdDict { { "schema", "ship_runtime_bundle_v1" }, { "ships", ships } };
        }
    }
}
