// Ported from scripts/procgen/playable_generated_ship.gd @ 96ecb2b0: the authoritative compartment fire (3275-3656),
// fire zones + suppression points + recharge port (4965-5105, 5140-5238, 5318-5392), decompression/threat structure
// damage (5240-5316), the active-model resolvers and room-position helpers (5510-5562, 6040-6058, 5488-5505).
using System;
using System.Collections.Generic;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Session
{
    public sealed partial class RunSession
    {
        // ------------------------------------------------------------------ active-model resolvers
        /// <summary>The systems manager of the ship the player is aboard: the derelict's when away, the lifeboat's (shared) when home.</summary>
        ShipSystemsManager ActiveSystemsManager()
        {
            if (AwayFromStart && CurrentShip != null && CurrentShip.SystemsManager != null)
                return CurrentShip.SystemsManager;
            return ShipSystemsManager;
        }

        FireSuppressionState ActiveFireState()
        {
            if (AwayFromStart && CurrentShip != null)
                return CurrentShip.GetFire();
            return FireSuppressionState;
        }

        HullIntegrityState ActiveHull()
        {
            if (AwayFromStart && CurrentShip != null && CurrentShip != HomeShip)
                return CurrentShip.GetHull();
            return HullIntegrityState;
        }

        WebInfestationState ActiveWeb()
        {
            if (AwayFromStart && CurrentShip != null && CurrentShip != HomeShip)
                return CurrentShip.GetWeb();
            return HullWebState;
        }

        HullIntegrityState HullFor(ShipInstance ship) => ship == null || ship == HomeShip ? HullIntegrityState : ship.GetHull();
        WebInfestationState WebFor(ShipInstance ship) => ship == null || ship == HomeShip ? HullWebState : ship.GetWeb();

        public ShipSystemsManager GetActiveSystemsManager() => ActiveSystemsManager();
        public FireSuppressionState GetActiveFireState() => ActiveFireState();

        // ------------------------------------------------------------------ active fire tick
        /// <summary>Tick the active ship's fire model once and apply its system + module damage (both branches).</summary>
        void TickActiveFire(double delta)
        {
            FireSuppressionState afs = ActiveFireState();
            if (afs == null)
                return;
            if (afs.Tick(delta, BuildFireContext()))
                RefreshFireZones();
            ApplyFireSystemDamage(delta);
            ApplyFireModuleIntegrity(delta);
            if (PlayerFireIntensity() > 0.0)
                TriggerTutorial("hazard_entered", "fire");
        }

        GdDict FireTuning()
        {
            GdDict tuning = LoadJsonDict(SHIP_SUBSYSTEM_TUNING_PATH);
            return tuning.GetDictOrEmpty("fire_suppression");
        }

        void ConfigureDerelictFire(FireSuppressionState fs)
        {
            if (fs == null)
                return;
            GdDict tuning = FireTuning().DeepCopy();
            GdArray vented = AuthoredVentedCompartments();
            if (!vented.IsEmpty)
                tuning["vented_compartments"] = vented;
            fs.Configure(tuning);
        }

        /// <summary>Compartments of rooms whose variant carries a hazard of <paramref name="kind"/> on a mapped role (sorted).</summary>
        GdArray VariantHazardCompartments(string kind)
        {
            var output = new GdDict();
            if (CurrentShip == null)
                return new GdArray();
            GdDict layout = CurrentShip.BuiltLayout;
            if (layout == null || layout.IsEmpty)
                return new GdArray();
            if (!(layout.Get("rooms", new GdArray()) is GdArray rooms))
                return new GdArray();
            var selector = new RoomVariantSelector();
            foreach (object roomObj in rooms)
            {
                if (!(roomObj is GdDict room))
                    continue;
                string role = V.Str(room.Get("room_role", room.Get("role", "")));
                string compartment = V.Str(COMPARTMENT_FOR_ROLE.Get(role, ""));
                if (compartment.Length == 0)
                    continue;
                string variant = V.Str(room.Get("variant", "standard"));
                GdDict sim = selector.EffectsFor(variant).Get("sim", new GdDict()) as GdDict ?? new GdDict();
                GdDict hazard = sim.Get("hazard", new GdDict()) as GdDict ?? new GdDict();
                if (V.Str(hazard.Get("kind", "")) == kind)
                    output[compartment] = true;
            }
            return SortedKeys(output);
        }

        static GdArray SortedKeys(GdDict d)
        {
            var result = new GdArray(d.Keys);
            GdSort.Sort(result);
            return result;
        }

        static string MappedAuthoredCompartmentId(string raw) => FireCompartmentResolver.FromToken(raw);

        static void AppendZoneDicts(GdArray output, object raw)
        {
            if (!(raw is GdArray arr))
                return;
            foreach (object zone in arr)
            {
                if (zone is GdDict)
                    output.Add(zone);
            }
        }

        GdArray AuthoredHazardZoneDicts(string zoneKey)
        {
            var output = new GdArray();
            if (CurrentShip != null && CurrentShip.BuiltLayout != null)
                AppendZoneDicts(output, CurrentShip.BuiltLayout.Get(zoneKey, new GdArray()));
            IShipLoaderView root = CurrentShip?.SceneRoot as IShipLoaderView;
            if (root != null && root.IsValid)
            {
                if (root.LayoutDoc != null)
                    AppendZoneDicts(output, root.LayoutDoc.Get(zoneKey, new GdArray()));
                if (root.GameplayDoc != null)
                    AppendZoneDicts(output, root.GameplayDoc.Get(zoneKey, new GdArray()));
                if (zoneKey == "fire_zones")
                    AppendZoneDicts(output, root.GetFireZoneSpecs());
            }
            return output;
        }

        GdArray AuthoredFireLayoutSources()
        {
            var sources = new GdArray();
            if (CurrentShip != null && CurrentShip.BuiltLayout != null)
                sources.Add(CurrentShip.BuiltLayout);
            IShipLoaderView root = CurrentShip?.SceneRoot as IShipLoaderView;
            if (root != null && root.IsValid && root.LayoutDoc != null)
                sources.Add(root.LayoutDoc);
            return sources;
        }

        string AuthoredFireCompartmentId(GdDict zone) => FireCompartmentResolver.FromZone(zone, AuthoredFireLayoutSources());

        GdArray AuthoredMappedHazardCompartments(string kind)
        {
            string zoneKey = kind == "fire" ? "fire_zones" : "breach_zones";
            var found = new GdDict();
            foreach (object zoneObj in AuthoredHazardZoneDicts(zoneKey))
            {
                var zone = (GdDict)zoneObj;
                string cid = kind == "fire" ? AuthoredFireCompartmentId(zone) : MappedAuthoredCompartmentId(V.Str(zone.Get("compartment_id", "")));
                if (cid.Length == 0)
                    continue;
                found[cid] = true;
            }
            return SortedKeys(found);
        }

        GdArray AuthoredVentedCompartments()
        {
            var found = new GdDict();
            var sources = new List<object>();
            if (CurrentShip != null && CurrentShip.BuiltLayout != null)
                sources.Add(CurrentShip.BuiltLayout.Get("vented_compartments", new GdArray()));
            IShipLoaderView root = CurrentShip?.SceneRoot as IShipLoaderView;
            if (root != null && root.IsValid)
            {
                if (root.LayoutDoc != null)
                    sources.Add(root.LayoutDoc.Get("vented_compartments", new GdArray()));
                if (root.GameplayDoc != null)
                    sources.Add(root.GameplayDoc.Get("vented_compartments", new GdArray()));
            }
            foreach (object raw in sources)
            {
                if (!(raw is GdArray arr))
                    continue;
                foreach (object cidV in arr)
                {
                    string cid = MappedAuthoredCompartmentId(V.Str(cidV));
                    if (cid.Length > 0)
                        found[cid] = true;
                }
            }
            return SortedKeys(found);
        }

        /// <summary>
        /// Pre-seeds environmental fire on a freshly built derelict: variant-forced fires, then the per-seed presence gate
        /// (FIRE_PRESENCE_PERCENT) over damaged, unbreached mapped compartments (cap 2, +1 when wrecked), then the authored
        /// overlay (skipping vented / breached compartments). Deterministic, RNG-free.
        /// </summary>
        void SeedDerelictFire()
        {
            if (!AwayFromStart || CurrentShip == null)
                return;
            if (CurrentShip.FireSeeded)
                return;
            FireSuppressionState fs = CurrentShip.GetFire();
            if (fs == null)
                return;
            ConfigureDerelictFire(fs);
            CurrentShip.FireSeeded = true;
            long seedInt = ShipSeed(CurrentShip);
            foreach (object cid in VariantHazardCompartments("fire"))
                fs.Ignite(V.Str(cid), 1.0);
            if (Math.Abs(GodotHash.StringHash(seedInt + ":fire_presence")) % 100 < FIRE_PRESENCE_PERCENT)
            {
                ShipSystemsManager mgr = ActiveSystemsManager();
                if (mgr != null)
                {
                    HullIntegrityState activeHull = ActiveHull();
                    var breached = new GdDict();
                    if (activeHull != null)
                    {
                        foreach (object cid in activeHull.Compartments.Keys)
                        {
                            if ((activeHull.Compartments[cid] as GdDict ?? new GdDict()).GetBool("breach_open"))
                                breached[V.Str(cid)] = true;
                        }
                    }
                    var candidates = new GdArray();
                    foreach (object cid in FIRE_COMPARTMENT_SYSTEM.Keys)
                    {
                        string sid = V.Str(FIRE_COMPARTMENT_SYSTEM[cid]);
                        if (sid.Length == 0 || breached.Has(V.Str(cid)))
                            continue;
                        ShipSystem sys = mgr.GetSystem(sid);
                        if (sys != null && !sys.IsSelfFunctional())
                            candidates.Add(V.Str(cid));
                    }
                    GdSort.Sort(candidates);
                    long cap = 2 + (ShipConditionClass(CurrentShip) == (long)ShipBlueprint.Condition.Wrecked ? 1 : 0);
                    long lit = 0;
                    foreach (object cid in candidates)
                    {
                        if (lit >= cap)
                            break;
                        fs.Ignite(V.Str(cid), 1.0);
                        lit += 1;
                    }
                }
            }
            HullIntegrityState overlayHull = CurrentShip.GetHull();
            foreach (object cid in AuthoredMappedHazardCompartments("fire"))
            {
                string cidS = V.Str(cid);
                if (fs.IsVented(cidS))
                    continue;
                if (overlayHull != null && overlayHull.Compartments.Has(cidS))
                {
                    if ((overlayHull.Compartments[cidS] as GdDict ?? new GdDict()).GetBool("breach_open"))
                        continue;
                }
                fs.Ignite(cidS, 1.0);
            }
        }

        /// <summary>Away-branch only: force-breach variant/authored breach compartments on the DERELICT's own hull.</summary>
        void SeedDerelictBreaches()
        {
            if (!AwayFromStart || CurrentShip == null)
                return;
            if (CurrentShip.BreachSeeded)
                return;
            CurrentShip.BreachSeeded = true;
            HullIntegrityState hull = CurrentShip.GetHull();
            if (hull == null)
                return;
            bool seededAny = false;
            foreach (object cid in VariantHazardCompartments("breach"))
            {
                if (hull.Compartments.Has(V.Str(cid)))
                {
                    hull.DamageCompartment(V.Str(cid), 1.0, true);
                    seededAny = true;
                }
            }
            foreach (object cid in AuthoredMappedHazardCompartments("breach"))
            {
                if (hull.Compartments.Has(V.Str(cid)))
                {
                    hull.DamageCompartment(V.Str(cid), 1.0, true);
                    seededAny = true;
                }
            }
            if (seededAny)
                EmitMetaHullGroan();
        }

        /// <summary><c>_build_fire_context()</c>: the per-frame context the authoritative fire model ticks against.</summary>
        GdDict BuildFireContext()
        {
            HullIntegrityState activeHull = ActiveHull();
            var breached = new GdArray();
            if (activeHull != null)
            {
                foreach (object cid in activeHull.Compartments.Keys)
                {
                    if ((activeHull.Compartments[cid] as GdDict ?? new GdDict()).GetBool("breach_open"))
                        breached.Add(V.Str(cid));
                }
            }
            var damaged = new GdArray();
            bool oxygenPresent = true;
            double powered = 0.0;
            bool arcArcing = false;
            if (!AwayFromStart)
            {
                if (ShipSystemsManager != null)
                {
                    foreach (object cid in FIRE_COMPARTMENT_SYSTEM.Keys)
                    {
                        string sid = V.Str(FIRE_COMPARTMENT_SYSTEM[cid]);
                        if (sid.Length == 0)
                            continue;
                        ShipSystem sys = ShipSystemsManager.GetSystem(sid);
                        if (sys != null && !sys.IsSelfFunctional())
                            damaged.Add(cid);
                    }
                }
                if (LifeSupportExpandedState != null)
                    oxygenPresent = LifeSupportExpandedState.OxygenPercent > OXYGEN_MIN_FOR_FIRE;
                powered = PowerGridState != null ? PowerGridState.GetAllocationRatio("stations") : 0.0;
                if (ElectricalArcState != null)
                    arcArcing = ElectricalArcState.CurrentPhaseValue == (long)ElectricalArcState.Phase.ARCING;
            }
            var closed = new GdArray();
            foreach (SealedHatch h in SealedHatches)
            {
                if (!h.IsValid || h.Bypassed)
                    continue;
                string a = h.CompartmentA;
                string b = h.CompartmentB;
                if (a.Length == 0 || b.Length == 0 || a == b)
                    continue;
                closed.Add(GdString.Less(a, b) ? a + "|" + b : b + "|" + a);
            }
            return new GdDict
            {
                { "powered_ratio", powered },
                { "ship_oxygen_present", oxygenPresent },
                { "breached_compartments", breached },
                { "damaged_compartments", damaged },
                { "closed_links", closed },
                { "arc_arcing", arcArcing },
            };
        }

        /// <summary>Seeds home fires in damaged, unbreached compartments on a genuine fresh build (never on restore).</summary>
        void SeedFiresFromDamage()
        {
            if (AwayFromStart)
                return;
            if (FireSuppressionState == null)
                return;
            GdDict ctx = BuildFireContext();
            var breached = new GdDict();
            foreach (object c in ctx.GetArrayOrEmpty("breached_compartments"))
                breached[V.Str(c)] = true;
            if (!ctx.GetBool("ship_oxygen_present", true))
                return;
            // Unity port (C4): the home hazard dial scales the seeded fire intensity (clamped by Ignite; 1.0 for "standard").
            double intensity = 1.0 * HomeHazardModifier();
            foreach (object cid in ctx.GetArrayOrEmpty("damaged_compartments"))
            {
                if (!breached.Has(V.Str(cid)))
                    FireSuppressionState.Ignite(V.Str(cid), intensity);
            }
        }

        /// <summary>M7-B Task 8: intensity of the fire the player stands in (within 2.0 of a fire-zone node), else 0.</summary>
        double PlayerFireIntensity()
        {
            if (!HasPlayer || ActiveFireState() == null)
                return 0.0;
            if (_inTick && _frame.InFireZoneCompartment != null)
                return _frame.InFireZoneCompartment.Length == 0 ? 0.0 : ActiveFireState().GetIntensity(_frame.InFireZoneCompartment);
            foreach (KeyValuePair<string, SessionZone> kv in FireZoneNodes)
            {
                SessionZone z = kv.Value;
                if (ToGlobal(z.Parent, z.LocalPosition).DistanceTo(PlayerPos) <= 2.0)
                {
                    string compartmentId = z.CompartmentOrRoomId;
                    if (compartmentId.Length == 0)
                        continue;
                    return ActiveFireState().GetIntensity(compartmentId);
                }
            }
            return 0.0;
        }

        void ApplyFireSystemDamage(double delta)
        {
            FireSuppressionState afs = ActiveFireState();
            if (afs == null || ActiveSystemsManager() == null)
                return;
            foreach (object cid in afs.GetBurningCompartments())
            {
                string sid = V.Str(FIRE_COMPARTMENT_SYSTEM.Get(V.Str(cid), ""));
                if (sid.Length == 0)
                    continue;
                double intensity = afs.GetIntensity(V.Str(cid));
                ActiveSystemsManager().DamageSystem(sid, FIRE_SYSTEM_DAMAGE_PER_SECOND * intensity * delta);
            }
        }

        /// <summary>PKG-B2.1b: fire intensity damages structural wall modules in mapped rooms.</summary>
        void ApplyFireModuleIntegrity(double delta)
        {
            if (ModuleIntegrityMap == null || delta <= 0.0)
                return;
            FireSuppressionState afs = ActiveFireState();
            if (afs == null)
                return;
            var burning = new GdDict();
            foreach (object cid in afs.GetBurningCompartments())
                burning[V.Str(cid)] = afs.GetIntensity(V.Str(cid));
            if (burning.IsEmpty)
                return;
            GdDict layout = new GdDict();
            if (Loader != null && Loader.IsValid)
                layout = Loader.GetLayoutCopy();
            if (layout.IsEmpty && AwayFromStart && CurrentShip != null && CurrentShip.BuiltLayout != null)
                layout = CurrentShip.BuiltLayout;
            if (layout.IsEmpty)
                return;
            if (ModuleIntegrityMap.Size() == 0)
                ModuleIntegrityConsequences.SeedMapFromCompiledLayout(ModuleIntegrityMap, layout);
            double resist = HubStructureDamageResist();
            double rate = ModuleIntegrityConsequences.FIRE_MODULE_DAMAGE_PER_INTENSITY * (1.0 - resist);
            GdArray changed = ModuleIntegrityConsequences.ApplyFireDamage(ModuleIntegrityMap, layout, burning, COMPARTMENT_FOR_ROLE, delta, rate);
            if (changed.IsEmpty)
                return;
            ApplyModuleIntegrityScene(changed);
            ApplyIntegrityNavGaps();
        }

        /// <summary>Hull breaches + module wall breaches (atmosphere links).</summary>
        long DerivedBreachCount()
        {
            long hullN = 0;
            if (HullIntegrityState != null)
                hullN = HullIntegrityState.GetBreachCount();
            long moduleN = 0;
            if (ModuleIntegrityMap != null)
                moduleN = ModuleIntegrityMap.CountWallBreaches();
            return hullN + moduleN;
        }

        public long GetDerivedBreachCount() => DerivedBreachCount();

        // ------------------------------------------------------------------ fire zones
        void BuildFireZones()
        {
            ClearFireZones();
            FireSuppressionState afs = ActiveFireState();
            if (afs == null)
                return;
            GdArray burning = afs.GetBurningCompartments();
            if (burning.IsEmpty)
                return;
            bool useLifeboat = !AwayFromStart && LifeboatShip != null && RootValid(LifeboatShip.SceneRoot);
            List<GdDict> layoutZones = useLifeboat ? new List<GdDict>() : AwayFireLayoutZones();
            List<Vec3> positions = useLifeboat ? LifeboatLocalRepairPositions() : DistributedRoomPositions();
            if (positions.Count == 0 && layoutZones.Count == 0)
                return;
            var claimed = new HashSet<int>();
            var mappedRows = new Dictionary<string, List<GdDict>>(StringComparer.Ordinal);
            foreach (object cid in burning)
            {
                string cidS = V.Str(cid);
                for (int zi = 0; zi < layoutZones.Count; zi++)
                {
                    if (claimed.Contains(zi))
                        continue;
                    GdDict row = layoutZones[zi];
                    GdDict spec = row.Get("spec", null) as GdDict ?? new GdDict();
                    string specCid = AuthoredFireCompartmentId(spec);
                    if (specCid.Length == 0 || specCid != cidS)
                        continue;
                    if (!mappedRows.ContainsKey(cidS))
                        mappedRows[cidS] = new List<GdDict>();
                    mappedRows[cidS].Add(new GdDict { { "position", row["position"] }, { "spec", spec } });
                    claimed.Add(zi);
                }
            }
            int leftoverIdx = 0;
            int fallbackI = 0;
            foreach (object cid in burning)
            {
                string cidS = V.Str(cid);
                List<GdDict> placements = mappedRows.TryGetValue(cidS, out List<GdDict> m) ? m : new List<GdDict>();
                if (placements.Count == 0)
                {
                    while (leftoverIdx < layoutZones.Count && (claimed.Contains(leftoverIdx) || FireLayoutZoneMappedCid(layoutZones[leftoverIdx]).Length > 0))
                        leftoverIdx += 1;
                    if (leftoverIdx < layoutZones.Count)
                    {
                        GdDict row = layoutZones[leftoverIdx];
                        placements.Add(new GdDict { { "position", row["position"] }, { "spec", row.Get("spec", null) as GdDict ?? new GdDict() } });
                        claimed.Add(leftoverIdx);
                        leftoverIdx += 1;
                    }
                    else
                    {
                        if (positions.Count == 0)
                            continue;
                        placements.Add(new GdDict { { "position", positions[fallbackI % positions.Count] }, { "spec", new GdDict() } });
                        fallbackI += 1;
                    }
                }
                for (int placementI = 0; placementI < placements.Count; placementI++)
                {
                    GdDict placement = placements[placementI];
                    Vec3 pos = (Vec3)placement["position"];
                    GdDict layoutSpec = placement.Get("spec", null) as GdDict ?? new GdDict();
                    var zone = new SessionZone
                    {
                        Kind = "fire",
                        ZoneId = cidS,
                        NodeName = "FireZone_" + cidS + "_" + placementI,
                        LocalPosition = pos,
                        CompartmentOrRoomId = cidS,
                        VisualState = "burning",
                        Parent = ActiveShipAttachRoot(),
                    };
                    zone.Meta["fire_zone_marker_index"] = (long)placementI;
                    if (!layoutSpec.IsEmpty)
                    {
                        zone.Meta["fire_zone_layout_id"] = V.Str(layoutSpec.Get("zone_id", ""));
                        zone.Meta["fire_zone_layout_kind"] = V.Str(layoutSpec.Get("kind", ""));
                    }
                    string zoneKey = placementI == 0 ? cidS : cidS + "#" + placementI;
                    FireZoneNodes[zoneKey] = zone;
                    Events.RaiseZoneSpawned(zone);
                }
            }
        }

        /// <summary>Layout-declared fire zones of the BOARDED derelict as {position, spec} rows (empty at home).</summary>
        List<GdDict> AwayFireLayoutZones()
        {
            var output = new List<GdDict>();
            if (!AwayFromStart || CurrentShip == null)
                return output;
            if (!(CurrentShip.SceneRoot is IShipLoaderView root) || !root.IsValid)
                return output;
            IReadOnlyList<Vec3> markers = root.GetFireZoneMarkers();
            GdArray specs = root.GetFireZoneSpecs() ?? new GdArray();
            for (int i = 0; i < markers.Count; i++)
            {
                if (markers[i] == Vec3.Inf)
                    continue;
                GdDict spec = i < specs.Count && specs[i] is GdDict s ? s : new GdDict();
                output.Add(new GdDict { { "position", markers[i] }, { "spec", spec } });
            }
            return output;
        }

        string FireLayoutZoneMappedCid(GdDict row)
        {
            if (row == null)
                return "";
            GdDict spec = row.Get("spec", null) as GdDict ?? new GdDict();
            return AuthoredFireCompartmentId(spec);
        }

        /// <summary>
        /// <c>_attach_zone_to_active_ship(node)</c>: the parent root for zone-like nodes (derelict root when away, else the
        /// lifeboat root, else the coordinator's repair_point_root at origin = null).
        /// </summary>
        IShipSceneRoot ActiveShipAttachRoot()
        {
            if (AwayFromStart && CurrentShip != null && RootValid(CurrentShip.SceneRoot))
                return CurrentShip.SceneRoot;
            if (LifeboatShip != null && RootValid(LifeboatShip.SceneRoot))
                return LifeboatShip.SceneRoot;
            return null;
        }

        void ClearFireZones()
        {
            foreach (SessionZone z in FireZoneNodes.Values)
                Events.RaiseZoneDespawned(z);
            FireZoneNodes.Clear();
        }

        void RefreshFireZones()
        {
            var burning = new GdDict();
            FireSuppressionState afs = ActiveFireState();
            if (afs != null)
            {
                foreach (object cid in afs.GetBurningCompartments())
                    burning[V.Str(cid)] = true;
            }
            var rendered = new GdDict();
            foreach (SessionZone zone in FireZoneNodes.Values)
                rendered[zone.CompartmentOrRoomId] = true;
            GdArray burningKeys = SortedKeys(burning);
            GdArray renderedKeys = SortedKeys(rendered);
            if (!V.VariantEquals(burningKeys, renderedKeys))
            {
                BuildFireZones();
                BuildFireSuppressionPoints();
            }
        }

        // ------------------------------------------------------------------ fire suppression points + recharge port
        void BuildFireSuppressionPoints()
        {
            ClearFireSuppressionPoints();
            FireSuppressionState afs = ActiveFireState();
            if (afs == null)
                return;
            GdArray burning = afs.GetBurningCompartments();
            if (burning.IsEmpty)
                return;
            bool useLifeboat = !AwayFromStart && LifeboatShip != null && RootValid(LifeboatShip.SceneRoot);
            List<Vec3> positions = useLifeboat ? LifeboatLocalRepairPositions() : DistributedRoomPositions();
            if (positions.Count == 0)
                return;
            int idx = 0;
            foreach (object cid in burning)
            {
                Vec3 pos = positions[idx % positions.Count];
                idx += 1;
                var fp = new FireSuppressionPoint();
                fp.Configure(V.Str(cid), afs, ExtinguisherState, InventoryState, PlayerProgression, pos, 4.0, "fire_extinguisher", 1.8);
                fp.FireExtinguished += OnFireExtinguished;
                fp.ExtinguishBlocked += OnExtinguishBlocked;
                fp.CompartmentVented += OnCompartmentVented;
                fp.Parent = ActiveShipAttachRoot();
                FireSuppressionPoints.Add(Spawn(fp));
            }
        }

        void ClearFireSuppressionPoints()
        {
            foreach (FireSuppressionPoint fp in FireSuppressionPoints)
                Despawn(fp);
            FireSuppressionPoints.Clear();
        }

        void OnFireExtinguished(string compartmentId)
        {
            RefreshFireZones();
            EmitTrainingEvent("decontaminate_zone", compartmentId);
            PlaySfx(AudioEventSeam.SFX_TOOL_USE);
        }

        /// <summary>Fire B2: deliberate vent — decompression teeth on the matching hull compartment and wall modules.</summary>
        void OnCompartmentVented(string compartmentId)
        {
            SetHazardFeedbackLine("Emergency vent: " + compartmentId + " (decompression risk)");
            HullIntegrityState hull = ActiveHull();
            if (hull != null && hull.Compartments.Has(compartmentId))
            {
                hull.DamageCompartment(compartmentId, 0.0, true);
                EmitMetaHullGroan();
            }
            ApplyDecompressionModuleDamage(compartmentId);
            RefreshFireZones();
            RefreshOxygenState(false, 0.0);
        }

        void OnExtinguishBlocked(string compartmentId, string reason)
        {
            SetHazardFeedbackLine("Extinguish blocked (" + compartmentId + "): " + HazardBlockReasonText(reason));
            PlaySfx(AudioEventSeam.UI_PANEL_CLOSE);
        }

        static string HazardBlockReasonText(string reason)
        {
            switch (reason)
            {
                case "missing_extinguisher": return "no fire extinguisher";
                case "missing_sealant": return "no hull sealant";
                case "no_charge": return "extinguisher empty";
                case "not_burning": return "no fire here";
                case "not_breached": return "no open breach";
                case "extinguish_failed": return "extinguish failed";
                default: return "cannot complete";
            }
        }

        void SetHazardFeedbackLine(string text)
        {
            _lastLootFeedbackLine = text;
            RefreshTrackerSystemStatusLines();
        }

        void BuildExtinguisherRechargePort()
        {
            ClearExtinguisherRechargePort();
            if (ExtinguisherState == null)
                return;
            bool useLifeboat = !AwayFromStart && LifeboatShip != null && RootValid(LifeboatShip.SceneRoot);
            List<Vec3> positions = useLifeboat ? LifeboatLocalRepairPositions() : DistributedRoomPositions();
            Vec3 pos = positions.Count > 0 ? positions[0] : new Vec3(0.0f, (float)PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0.0f);
            var port = new ExtinguisherRechargePort();
            port.Configure(ExtinguisherState, pos, 1.8);
            port.Parent = ActiveShipAttachRoot();
            ExtinguisherRechargePort = Spawn(port);
        }

        void ClearExtinguisherRechargePort()
        {
            if (ExtinguisherRechargePort != null)
                Despawn(ExtinguisherRechargePort);
            ExtinguisherRechargePort = null;
        }

        /// <summary>Force-ignite a compartment in the home fire model (was <c>force_ignite_compartment_for_validation</c>).</summary>
        public bool ForceIgniteCompartment(string compartmentId, double intensity = 1.0)
        {
            if (FireSuppressionState == null)
                return false;
            bool ok = FireSuppressionState.Ignite(compartmentId, intensity);
            RefreshFireZones();
            return ok;
        }

        /// <summary>Ignite in the ACTIVE fire state (derelict's when away).</summary>
        public bool ForceIgniteActiveCompartment(string compartmentId, double intensity = 1.0)
        {
            FireSuppressionState afs = ActiveFireState();
            if (afs == null)
                return false;
            bool ok = afs.Ignite(compartmentId, intensity);
            RefreshFireZones();
            return ok;
        }

        // ------------------------------------------------------------------ structure damage
        double HubStructureDamageResist()
        {
            if (AwayFromStart)
                return 0.0;
            if (ShipModificationState != null)
                return ShipModificationState.StructureDamageResist();
            return 0.0;
        }

        void ApplyDecompressionModuleDamage(string compartmentId)
        {
            if (ModuleIntegrityMap == null || string.IsNullOrEmpty(compartmentId))
                return;
            GdDict layout = ActiveLayoutForWork();
            if (layout.IsEmpty && ModuleIntegrityMap.Size() == 0)
                return;
            if (ModuleIntegrityMap.Size() == 0 && !layout.IsEmpty)
                ModuleIntegrityConsequences.SeedMapFromCompiledLayout(ModuleIntegrityMap, layout);
            double resist = HubStructureDamageResist();
            double amount = ModuleDamageRouter.DEFAULT_DECOMPRESSION_AMOUNT * (1.0 - resist);
            GdArray changed = ModuleDamageRouter.ApplyDecompressionToCompartment(ModuleIntegrityMap, layout, compartmentId, COMPARTMENT_FOR_ROLE, amount);
            if (!changed.IsEmpty)
                ApplyModuleIntegrityScene(changed);
        }

        /// <summary>REQ-MI-004: a threat structure strike on a module (was <c>apply_threat_structure_damage_for_validation</c>).</summary>
        public GdDict ApplyThreatStructureDamage(string moduleId, double amount = 0.35)
        {
            if (ModuleIntegrityMap == null)
                ModuleIntegrityMap = new ModuleIntegrityMap();
            GdDict res = ModuleDamageRouter.ApplyThreatStructureHit(ModuleIntegrityMap, moduleId, amount, "", HubStructureDamageResist());
            if (res.GetBool("ok"))
            {
                ApplyModuleIntegrityScene(GdArray.Of(moduleId));
                InterruptWorkOnDamage();
            }
            return res;
        }

        /// <summary>Hull tendril structure strike: damage the nearest wall module to the threat (fallback: first registered).</summary>
        void OnThreatStructureAttack(ThreatAIState threat, double amount)
        {
            if (threat == null || amount <= 0.0 || ModuleIntegrityMap == null)
                return;
            Vec3 pos = Vec3.Zero;
            if (threat.WorldPosition.Count >= 3)
                pos = new Vec3(V.F64(threat.WorldPosition[0]), V.F64(threat.WorldPosition[1]), V.F64(threat.WorldPosition[2]));
            GdDict layout = ActiveLayoutForWork();
            GdDict nearest = NearestWorkableWallModule(layout, pos, 6.0);
            string mid = V.Str(nearest.Get("module_id", ""));
            if (mid.Length == 0)
            {
                List<string> ids = ModuleIntegrityMap.ModuleIds();
                if (ids.Count == 0)
                    return;
                mid = ids[0];
            }
            ApplyThreatStructureDamage(mid, amount);
        }

        /// <summary>Combat damage callback (DamagePipeline.on_player_damaged) — REQ-WA-003.</summary>
        void OnPlayerCombatDamaged(double damage, GdDict ev)
        {
            if (damage > 0.0)
            {
                InterruptWorkOnDamage();
                PlaySfx(AudioEventSeam.SFX_COMBAT_HIT);
                // Unity port (E1): combat damage opens wounds (Godot never applied WoundState.suggest_from_damage).
                ApplyWoundFromCombatDamage(damage, ev);
            }
        }

        // ------------------------------------------------------------------ room-position helpers
        /// <summary>
        /// <c>_distributed_room_positions()</c>: objective spec positions of the active loader, else its loot spec positions
        /// (loader-local).
        /// </summary>
        List<Vec3> DistributedRoomPositions()
        {
            var output = new List<Vec3>();
            IShipLoaderView activeLoader = AwayFromStart && CurrentShip != null ? CurrentShip.SceneRoot as IShipLoaderView : Loader;
            if (activeLoader != null && activeLoader.IsValid)
            {
                foreach (object specObj in activeLoader.GetObjectiveSpecsCopy())
                {
                    if (specObj is GdDict spec && spec.Get("position", null) is Vec3 p)
                        output.Add(p);
                }
            }
            if (output.Count == 0 && activeLoader != null && activeLoader.IsValid)
            {
                foreach (object specObj in activeLoader.GetLootContainerSpecsCopy())
                {
                    if (specObj is GdDict spec && spec.Get("position", null) is Vec3 p)
                        output.Add(p);
                }
            }
            return output;
        }

        /// <summary>LIFEBOAT-LOCAL positions from the built lifeboat room nodes (+ spawn height).</summary>
        List<Vec3> LifeboatLocalRepairPositions()
        {
            var output = new List<Vec3>();
            if (LifeboatShip == null || !RootValid(LifeboatShip.SceneRoot))
                return output;
            if (!(LifeboatShip.SceneRoot is IShipInteriorView view))
                return output;
            foreach (Vec3 p in view.StructureRoomLocalPositions())
                output.Add(p + new Vec3(0.0f, (float)PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0.0f));
            return output;
        }

        /// <summary>HOME-LOCAL station positions from the home ship's room nodes (+ spawn height).</summary>
        List<Vec3> HomeLocalStationPositions()
        {
            var output = new List<Vec3>();
            if (HomeShip == null || !RootValid(HomeShip.SceneRoot))
                return output;
            if (!(HomeShip.SceneRoot is IShipInteriorView view))
                return output;
            foreach (Vec3 p in view.StructureRoomLocalPositions())
                output.Add(p + new Vec3(0.0f, (float)PLAYER_SPAWN_HEIGHT_ABOVE_NAV_FLOOR, 0.0f));
            return output;
        }

        void EmitMetaHullGroan() => PlaySfx(AudioEventSeam.META_HULL_GROAN);
    }
}
