using System;
using System.Collections.Generic;
using System.Globalization;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Versioned gameplay load capacity; not Newtons or a rigid-body acceleration model.</summary>
    public static class AssemblyMobility
    {
        public static List<Vec3> Floors(GdDict layout)
        {
            var result = new List<Vec3>(); var seen = new HashSet<Vec3>();
            var records = layout.GetDictOrEmpty("structural_plan").GetArrayOrEmpty("floor_placements");
            if (records.Count > 0)
            {
                foreach (var value in records)
                    if (value is GdDict record) Add(record.Get("position", record.Get("world_position")), seen, result);
            }
            else foreach (var value in layout.GetArrayOrEmpty("rooms"))
                if (value is GdDict room)
                    foreach (var row in room.GetArrayOrEmpty("structural_placements"))
                        if (row is GdDict record && record.GetString("module_id").Contains("floor"))
                            Add(record.Get("world_position", record.Get("position")), seen, result);
            return result;
        }
        static void Add(object value, HashSet<Vec3> seen, List<Vec3> list)
        {
            Vec3 p = value is Vec3 v ? v : Vec3.FromArray(value, Vec3.Inf);
            if (value is string text)
            {
                var parts = text.Trim().TrimStart('(').TrimEnd(')').Split(',');
                if (parts.Length == 3 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                    && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)) p = new Vec3(x, y, z);
            }
            if (Finite(p.X) && Finite(p.Y) && Finite(p.Z) && seen.Add(p)) list.Add(p);
        }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static GdDict CreateSpecification(ShipInstance ship, bool installation)
        {
            double area = Floors(ship.BuiltLayout).Count * 16.0;
            return new GdDict { { "version", 1L }, { "area_m2", area }, { "dry_mass_kg", area * 100 },
                { "engine_id", installation ? "propulsion:" + ship.ShipId : "" },
                { "rated_supported_kg", installation ? area * 125 : 0.0 } };
        }
        public static bool ValidSpecification(GdDict spec)
        {
            double area=spec.GetFloat("area_m2"), mass=spec.GetFloat("dry_mass_kg"), rating=spec.GetFloat("rated_supported_kg");
            return spec.GetInt("version")==1 && Finite(area) && area>0 && Finite(mass) && mass>0
                && Finite(rating) && rating>=0 && (rating==0 || spec.GetString("engine_id").Length>0);
        }
        public static GdDict Evaluate(ShipInstance root, double playerCargo)
        {
            var report = new GdDict { { "success", false }, { "reason", "invalid_assembly" },
                { "root", root?.ShipId ?? "" }, { "members", new GdArray() }, { "engines", new GdArray() } };
            if (root==null || !Finite(playerCargo) || playerCargo<0
                || !DockingManager.TryConnectedMembers(root,out _,out string reason)) return report;
            var pending=new Stack<ShipInstance>(); pending.Push(root);
            var engines=new HashSet<string>(StringComparer.Ordinal); var systems=new HashSet<ShipSystemsManager>();
            var catalog=new ComponentCatalog(); catalog.LoadDefault();
            double mass=playerCargo, supported=0;
            while(pending.Count>0)
            {
                var ship=pending.Pop(); var spec=ship.Mobility;
                if(!ValidSpecification(spec)) { report["reason"]="invalid_mobility_specification:"+ship.ShipId; return report; }
                if(ship.SystemsManager!=null && !systems.Add(ship.SystemsManager))
                { report["reason"]="shared_systems_owner:"+ship.ShipId; return report; }
                double cargo=ship.Inventory?.GetTotalWeight() ?? 0, components=0;
                var cartIds=new HashSet<string>(StringComparer.Ordinal);
                foreach(var cart in ship.Carts)
                {
                    if(cart==null || cart.CartId.Length==0 || !cartIds.Add(cart.CartId)) { report["reason"]="invalid_cart_owner";return report; }
                    cargo+=cart.GetHold().GetTotalWeight();
                }
                var componentIds=new HashSet<string>(StringComparer.Ordinal);
                foreach(var value in ship.ComponentPlacementSummary.GetArrayOrEmpty("placed"))
                    if(value is GdDict entry && entry.GetBool("mounted",true))
                    {
                        string id=entry.GetString("component_instance_id");
                        if(id.Length>0 && !componentIds.Add(id)) { report["reason"]="duplicate_component:"+ship.ShipId; return report; }
                        components+=catalog.GetComponent(entry.GetString("component_id")).GetFloat("mass");
                    }
                if(!Finite(cargo)||cargo<0||!Finite(components)||components<0) { report["reason"]="invalid_payload"; return report; }
                double dry=spec.GetFloat("dry_mass_kg"); mass+=dry+cargo+components;
                ((GdArray)report["members"]).Add(new GdDict { {"ship_id",ship.ShipId},{"dry_kg",dry},{"cargo_kg",cargo},{"components_kg",components} });
                bool connectedControl=ReferenceEquals(ship,root) || (ship.DockingPorts.Count>0 && ship.DockingPorts[0] is GdDict edge && edge.GetString("connection_kind")=="secured");
                bool stowed=ship.ParentShip is ShipInstance parent && parent.Hangar!=null && parent.Hangar.SlotOf(ship.ShipId)>=0;
                string engine=spec.GetString("engine_id"), excluded=""; double effective=0;
                if(engine.Length==0) excluded="no_owned_installation";
                else if(!engines.Add(engine)) { report["reason"]="duplicate_engine_owner"; return report; }
                else if(stowed || !connectedControl) excluded="carried_craft_payload";
                else if(ship.SystemsManager==null || !ship.SystemsManager.IsOperational("power") || !ship.SystemsManager.IsOperational("propulsion")) excluded="local_power_or_propulsion_offline";
                else
                {
                    effective=spec.GetFloat("rated_supported_kg") * ship.SystemsManager.GetSystem("propulsion").Health()
                        * ship.SystemsManager.GetSystem("power").Health() * ship.GetHull().AverageIntegrity();
                    if(!Finite(effective)||effective<0) { report["reason"]="invalid_engine_capacity"; return report; }
                    supported+=effective;
                }
                ((GdArray)report["engines"]).Add(new GdDict { {"ship_id",ship.ShipId},{"engine_id",engine},{"effective_supported_kg",effective},{"excluded_reason",excluded} });
                foreach(var child in ship.DockedShips)
                    if(child is ShipInstance instance) pending.Push(instance);
                    else { report["reason"]="unknown_assembly_member"; return report; }
            }
            report["total_mass_kg"]=mass; report["supported_kg"]=supported; report["margin_kg"]=supported-mass;
            report["success"]=supported>=mass; report["reason"]=supported>=mass ? "ok" : "insufficient_propulsion_capacity";
            return report;
        }
    }
}
