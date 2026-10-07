using System;
using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>Versioned BSP composition with doors selected only from physically shared boundaries.</summary>
    public static class ConstrainedExpedition
    {
        public const string Profile = "reclamation_expedition_v4";
        public const string LegacyProfile = "constrained_expedition_v3";
        public static bool Supported(string profile) => profile == Profile || profile == LegacyProfile;
        sealed class Room { public int X,Y,W,H; public string Role,Id; }
        sealed class Edge { public int A,B; public readonly List<(Vec2i a,Vec2i b)> Cells=new List<(Vec2i,Vec2i)>(); }
        public static GdDict Layout(ShipBlueprint bp, out List<GdDict> plan) => LayoutWithAttemptBudget(bp,4,out plan);
        internal static GdDict LayoutWithAttemptBudget(ShipBlueprint bp,int attempts,out List<GdDict> plan)
        {
            if(bp.ShipSize<1||bp.ShipSize>2) {plan=new List<GdDict>(); return new GdDict();}
            var failures=new GdArray();
            for(int attempt=0;attempt<attempts;attempt++)
            {
                var grid=Compose(bp,attempt,out plan);
                string failure=ValidateComposition(grid);
                if(failure.Length==0) return grid;
                failures.Append(new GdDict{{"attempt",(long)attempt},{"reason",failure}});
            }
            var fallback=PurposefulExpedition.Layout(bp,out plan);
            fallback["composition_diagnostics"]=new GdDict {{"status","fallback"},{"reason","constraint attempt budget exhausted"},{"attempt_errors",failures},{"attempt_budget",(long)attempts},{"fallback_recipe",PurposefulExpedition.Profile}};
            return fallback;
        }

        internal static string ValidateComposition(GdDict grid)
        {
            var rooms=grid.GetDictOrEmpty("rooms"); if(rooms.IsEmpty) return "no feasible partition/route candidate";
            if(!rooms.Has("dock_01")||V.Str(rooms.Keys.First())!="dock_01")return "dock is not boarding entry";
            var owners=new Dictionary<Vec2i,string>();var graph=new Dictionary<string,HashSet<string>>();
            foreach(var key in rooms.Keys)
            {
                string id=V.Str(key);var r=(GdDict)rooms[key];graph[id]=new HashSet<string>();
                if(!(r.Get("footprint") is Vec2i fp)||fp.X<2||fp.Y<2)return "room below minimum footprint: "+id;
                foreach(var raw in r.GetArrayOrEmpty("cells"))
                {if(!(raw is Vec2i cell)||owners.ContainsKey(cell))return "invalid or overlapping floor: "+id;owners.Add(cell,id);}
            }
            foreach(GdDict edge in grid.GetArrayOrEmpty("adjacencies"))
            {
                string a=edge.GetString("from_room"),b=edge.GetString("to_room");
                if(!graph.ContainsKey(a)||!graph.ContainsKey(b)||a==b||!(edge.Get("from_cell") is Vec2i ca)||!(edge.Get("to_cell") is Vec2i cb)
                    ||Math.Abs(ca.X-cb.X)+Math.Abs(ca.Y-cb.Y)!=1||!owners.TryGetValue(ca,out string oa)||oa!=a||!owners.TryGetValue(cb,out string ob)||ob!=b)return "invalid physical portal";
                if(!graph[a].Add(b))return "duplicate room route";graph[b].Add(a);
            }
            var seen=new HashSet<string>();var pending=new Queue<string>();pending.Enqueue("dock_01");
            while(pending.Count>0){string id=pending.Dequeue();if(seen.Add(id))foreach(string next in graph[id])pending.Enqueue(next);}
            if(seen.Count!=rooms.Count)return "disconnected floor rooms";
            if(grid.GetArrayOrEmpty("adjacencies").Count-rooms.Count+1<2)return "alternate route budget not met";
            string branch=grid.GetDictOrEmpty("composition_diagnostics").GetString("branch_room");
            if(branch=="dock_01"||!graph.ContainsKey(branch)||graph[branch].Count!=1)return "protected branch not a leaf";
            foreach(var roles in new[]{new[]{"crew_quarters","medical"},new[]{"engineering","maintenance"}})
                if(!graph.Any(g=>((GdDict)rooms[g.Key]).GetString("role")==roles[0]&&g.Value.Any(id=>((GdDict)rooms[id]).GetString("role")==roles[1])))return "functional adjacency missing: "+roles[0];
            foreach(string role in new[]{"cargo","bridge"})if(!rooms.Values.Cast<GdDict>().Any(r=>r.GetString("role")==role))return "required role missing: "+role;
            return "";
        }
        static GdDict Compose(ShipBlueprint bp,int attempt,out List<GdDict> plan)
        {
            plan=new List<GdDict>();
            var rng=GodotRandom.FromSeed(bp.SeedValue ^ (attempt*0x517CC1B7L));
            int width=(int)rng.RandiRange(bp.ShipSize==1?10:12,bp.ShipSize==1?12:15);
            int height=(int)rng.RandiRange(bp.ShipSize==1?8:10,bp.ShipSize==1?10:12);
            int budget=(int)rng.RandiRange(bp.ShipSize==1?9:12,bp.ShipSize==1?12:16);
            var rooms=new List<Room>{new Room{X=0,Y=0,W=width,H=height}};
            while(rooms.Count<budget)
            {
                var candidates=rooms.Where(r=>r.W>=4||r.H>=4).OrderByDescending(r=>r.W*r.H).ToList();
                if(candidates.Count==0) break;
                var room=candidates[(int)rng.RandiRange(0,Math.Min(2,candidates.Count-1))];
                bool vertical=room.W>=4&&(room.H<4||rng.RandiRange(0,1)==0);
                int split=(int)rng.RandiRange(2,(vertical?room.W:room.H)-2);
                var other=new Room{X=vertical?room.X+split:room.X,Y=vertical?room.Y:room.Y+split,
                    W=vertical?room.W-split:room.W,H=vertical?room.H:room.H-split};
                if(vertical) room.W=split; else room.H=split;
                rooms.Add(other);
            }
            var owners=new Dictionary<Vec2i,int>();
            for(int i=0;i<rooms.Count;i++) for(int y=rooms[i].Y;y<rooms[i].Y+rooms[i].H;y++)
                for(int x=rooms[i].X;x<rooms[i].X+rooms[i].W;x++) owners.Add(new Vec2i(x,y),i);
            var edges=new List<Edge>(); var pairIndex=new Dictionary<string,Edge>();
            foreach(var cell in owners.Keys.OrderBy(c=>c.Y).ThenBy(c=>c.X)) foreach(var dir in new[]{new Vec2i(1,0),new Vec2i(0,1)})
            {
                var next=cell+dir; if(!owners.TryGetValue(next,out int b)||b==owners[cell]) continue;
                int a=owners[cell]; string key=Math.Min(a,b)+":"+Math.Max(a,b);
                if(!pairIndex.TryGetValue(key,out var edge)) {edge=new Edge{A=a,B=b};pairIndex.Add(key,edge);edges.Add(edge);}
                edge.Cells.Add((cell,next));
            }
            // Functional pairs have a real direct door, not merely labels elsewhere in the hull.
            var unassigned=new HashSet<int>(Enumerable.Range(0,rooms.Count)); var forced=new List<Edge>();
            foreach(var roles in new[]{new[]{"crew_quarters","medical"},new[]{"engineering","maintenance"}})
            {
                var eligible=edges.Where(e=>unassigned.Contains(e.A)&&unassigned.Contains(e.B)).ToList();
                if(eligible.Count==0) return new GdDict();
                var edge=eligible[(int)rng.RandiRange(0,eligible.Count-1)];
                rooms[edge.A].Role=roles[0];rooms[edge.B].Role=roles[1];unassigned.Remove(edge.A);unassigned.Remove(edge.B);forced.Add(edge);
            }
            var remaining=unassigned.OrderByDescending(i=>rooms[i].W*rooms[i].H).ToList();
            rooms[remaining[0]].Role="cargo"; rooms[remaining[1]].Role="bridge";
            foreach(int i in remaining.Skip(2)) rooms[i].Role=new[]{"cargo","crew_quarters","maintenance","corridor"}[(int)rng.RandiRange(0,3)];
            // Randomized Kruskal keeps forced adjacency while varying the actual traversable route tree.
            var shuffled=edges.ToList(); for(int i=shuffled.Count-1;i>0;i--) {int j=(int)rng.RandiRange(0,i);var tmp=shuffled[i];shuffled[i]=shuffled[j];shuffled[j]=tmp;}
            var parent=Enumerable.Range(0,rooms.Count).ToArray();
            int Root(int i) {while(parent[i]!=i)i=parent[i];return i;}
            var selected=new List<Edge>();
            foreach(var e in forced.Concat(shuffled)) if(Root(e.A)!=Root(e.B)) {parent[Root(e.A)]=Root(e.B);selected.Add(e);}
            if(selected.Count!=rooms.Count-1) return new GdDict();
            var degree=new int[rooms.Count];foreach(var e in selected){degree[e.A]++;degree[e.B]++;}
            int dockOwner=owners[new Vec2i(0,height/2)];
            var leaves=Enumerable.Range(0,rooms.Count).Where(i=>degree[i]==1 && i!=dockOwner).ToArray();
            if(leaves.Length==0) return new GdDict();
            int branch=leaves[(int)rng.RandiRange(0,leaves.Length-1)];
            var extras=shuffled.Where(e=>!selected.Contains(e)&&e.A!=branch&&e.B!=branch).ToList();
            int loops=Math.Min(extras.Count,(int)rng.RandiRange(2,4)); if(loops<2)return new GdDict();
            selected.AddRange(extras.Take(loops));
            var host=rooms[dockOwner];
            int dockY=Math.Min(height/2,host.Y+host.H-2);
            rooms.Add(new Room{X=-2,Y=dockY,W=2,H=2,Role="dock"});
            var dockEdge=new Edge{A=dockOwner,B=rooms.Count-1};dockEdge.Cells.Add((new Vec2i(0,dockY),new Vec2i(-1,dockY)));selected.Add(dockEdge);
            int quarter=(int)rng.RandiRange(0,3); Vec2i Rotate(Vec2i c){for(int q=0;q<quarter;q++)c=new Vec2i(-c.Y,c.X);return c;}
            var resultRooms=new GdDict();var counts=new Dictionary<string,int>();
            foreach(var r in rooms)
            {
                counts.TryGetValue(r.Role,out int count);counts[r.Role]=++count;r.Id=r.Role+"_"+count.ToString("D2");
                var cells=new GdArray();for(int y=r.Y;y<r.Y+r.H;y++)for(int x=r.X;x<r.X+r.W;x++)cells.Append(Rotate(new Vec2i(x,y)));
                var footprint=quarter%2==0?new Vec2i(r.W,r.H):new Vec2i(r.H,r.W);
                resultRooms[r.Id]=new GdDict{{"cells",cells},{"origin",cells[0]},{"footprint",footprint},{"deck",0L},{"role",r.Role}};
                plan.Add(new GdDict{{"id",r.Id},{"role",r.Role},{"variant","standard"},{"deck",0L},{"footprint",footprint},{"target_cells",(long)r.W*r.H}});
            }
            var links=new GdArray();foreach(var e in selected)
            {
                // Shared-edge midpoint avoids the corners of longer walls and leaves both floor centers clear.
                var boundary=e.Cells[e.Cells.Count/2];
                links.Append(new GdDict{{"from_room",rooms[e.A].Id},{"to_room",rooms[e.B].Id},{"from_cell",Rotate(boundary.a)},{"to_cell",Rotate(boundary.b)}});
            }
            // Publishing order is gameplay authority: boarding starts at the dock; the goal is a far reachable room.
            var depths=Enumerable.Repeat(-1,rooms.Count).ToArray();var pending=new Queue<int>();depths[rooms.Count-1]=0;pending.Enqueue(rooms.Count-1);
            while(pending.Count>0){int current=pending.Dequeue();foreach(var e in selected){int next=e.A==current?e.B:e.B==current?e.A:-1;if(next<0||depths[next]>=0)continue;depths[next]=depths[current]+1;pending.Enqueue(next);}}
            if(depths.Any(d=>d<0)||selected.Count(e=>e.A==branch||e.B==branch)!=1)return new GdDict();
            var published=new GdDict();var publishedPlan=new List<GdDict>();
            foreach(int i in Enumerable.Range(0,rooms.Count).OrderBy(i=>depths[i]).ThenBy(i=>i)){published[rooms[i].Id]=resultRooms[rooms[i].Id];publishedPlan.Add(plan.Single(p=>p.GetString("id")==rooms[i].Id));}
            plan=publishedPlan;
            return new GdDict{{"rooms",published},{"adjacencies",links},{"composition_diagnostics",new GdDict{{"status","composed"},{"attempt",(long)attempt},{"envelope_width",(long)width},{"envelope_height",(long)height},{"cycles",(long)loops},{"branch_room",rooms[branch].Id}}}};
        }
    }
}
