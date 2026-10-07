using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    public static class HomeJoinPlanner
    {
        static readonly Vec3[] Directions = { Vec3.Right, -Vec3.Right, new Vec3(0,0,1), new Vec3(0,0,-1) };
        public static List<GdDict> Sites(ShipInstance ship)
        {
            var floors=AssemblyMobility.Floors(ship.BuiltLayout); var seen=new HashSet<Vec3>(floors);
            var result=new List<GdDict>(); var canonical=DockPorts.ForDerelict(ship.BuiltLayout);
            Vec3 reserved=canonical.Get("position") is Vec3 v ? v : Vec3.Inf;
            foreach(var cell in floors.Where(c=>Math.Abs(c.Y)<0.05).OrderBy(c=>c.X).ThenBy(c=>c.Z))
                foreach(var direction in Directions)
                {
                    if(seen.Contains(cell+direction*4)) continue;
                    Vec3 position=cell+direction*2;
                    if(position.DistanceTo(reserved)<=4.05) continue;
                    string id=string.Format(CultureInfo.InvariantCulture,"exterior-v1:{0}:{1}:{2}:{3}:{4}",cell.X,cell.Y,cell.Z,direction.X,direction.Z);
                    result.Add(new GdDict {{"site_id",id},{"position",position},{"facing",direction},{"type","airlock"},{"size_class",1L},{"condition","intact"}});
                }
            return result;
        }
        public static bool TryPlan(ShipInstance home, ShipInstance mobile, out GdDict hostPort, out GdDict mobilePort, out string reason)
        {
            hostPort=new GdDict(); mobilePort=new GdDict(); reason="no_compatible_clear_exterior_site";
            if(home==null||mobile==null||home.SceneRoot==null||mobile.SceneRoot==null) return false;
            var homeFloors=AssemblyMobility.Floors(home.BuiltLayout);
            if(homeFloors.Count==0) return false;
            double west=homeFloors.Where(c=>Math.Abs(c.Y)<0.05).Select(c=>(double)c.X).DefaultIfEmpty(double.NaN).Min();
            foreach(var h in Sites(home).Where(p=>((Vec3)p["facing"]).X==-1 && Math.Abs(((Vec3)p["position"]).X-(west-2))<0.05))
                // Retain authored ship orientation: the existing room/perception contract is
                // translation based. Do not rotate its physical hull away from those room bounds.
                foreach(var m in Sites(mobile).Where(p=>((Vec3)p["facing"]).X==1))
                    if(Validate(home,mobile,h,m,out _)) { hostPort=h;mobilePort=m;reason="ok";return true; }
            return false;
        }
        public static bool Validate(ShipInstance host, ShipInstance mobile, GdDict h, GdDict m, out string reason)
        {
            reason="invalid_exterior_site";
            if(!Sites(host).Any(p=>SameSite(p,h)) || !Sites(mobile).Any(p=>SameSite(p,m))) return false;
            var world=DockingManager.HostPortToWorld(host,h);
            var preflight=DockingManager.CanDock(host,mobile,world,m);
            if(!preflight.GetBool("success")) { reason=preflight.GetString("reason");return false; }
            if(!DockingManager.TryConnectedMembers(host,out var hostMembers,out reason)
                ||!DockingManager.TryConnectedMembers(mobile,out var mobileMembers,out reason)) return false;
            // The departing root carries descendants, never its current parent/ancestors.
            var moving=new HashSet<IDockableShip>(); var stack=new Stack<IDockableShip>();stack.Push(mobile);
            while(stack.Count>0) {var member=stack.Pop();moving.Add(member);foreach(var child in member.DockedShips)stack.Push(child);}
            Xform3 desired=DockingManager.ComputeMobileTransform(world,m);
            Xform3 inverse=SessionMath.AffineInverse(mobile.SceneRoot.GlobalTransform);
            var existing=new List<Vec3>();
            foreach(var value in hostMembers)
                if(value is ShipInstance ship && !moving.Contains(ship) && ship.SceneRoot!=null)
                    foreach(var floor in AssemblyMobility.Floors(ship.BuiltLayout)) existing.Add(ship.SceneRoot.GlobalTransform*floor);
            foreach(var value in mobileMembers.Where(moving.Contains))
            {
                if(!(value is ShipInstance ship)||ship.SceneRoot==null) { reason="missing_member_geometry";return false; }
                foreach(var floor in AssemblyMobility.Floors(ship.BuiltLayout))
                {
                    Vec3 candidate=desired*(inverse*(ship.SceneRoot.GlobalTransform*floor));
                    if(existing.Any(p=>Math.Abs(p.Y-candidate.Y)<0.25 && Math.Abs(p.X-candidate.X)<3.95 && Math.Abs(p.Z-candidate.Z)<3.95))
                    { reason="assembly_overlap";return false; }
                }
            }
            reason="ok";return true;
        }
        static bool SameSite(GdDict expected, GdDict supplied) => expected.GetString("site_id")==supplied.GetString("site_id")
            && supplied.Get("position") is Vec3 position && position.DistanceTo((Vec3)expected["position"]) < 0.001
            && supplied.Get("facing") is Vec3 facing && facing.DistanceTo((Vec3)expected["facing"]) < 0.001
            && supplied.GetString("type")=="airlock" && supplied.GetInt("size_class")==1;
    }
}
