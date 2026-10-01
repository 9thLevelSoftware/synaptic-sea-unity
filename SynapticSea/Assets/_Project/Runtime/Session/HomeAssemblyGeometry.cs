using System.Collections.Generic;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime.Session
{
    /// <summary>Only qualified secured endpoints cut their complete instance wall assemblies. Physics and door carving remain authoritative.</summary>
    public sealed class HomeAssemblyGeometry
    {
        string _identity="";
        readonly List<Collider> _colliders=new List<Collider>();
        readonly List<Renderer> _renderers=new List<Renderer>();
        readonly List<StructuralModule> _modules=new List<StructuralModule>();
        readonly List<GameObject> _doors=new List<GameObject>();
        readonly List<GameObject> _roots=new List<GameObject>();
        readonly Dictionary<GameObject, ShipInstance> _owners=new Dictionary<GameObject, ShipInstance>();
        GameObject _home;
        public void Reconcile(ShipInstance home)
        {
            if(home==null||!(home.SceneRoot is SceneShipRoot root)||!root.IsValid) {Restore();return;}
            if(!DockingManager.TryConnectedMembers(home,out var members,out _)) {Restore();return;}
            var secured=members.OfType<ShipInstance>().Where(s=>s.ParentShip==home && s.DockingPorts.Count>0
                && s.DockingPorts[0] is GdDict e && e.GetString("connection_kind")=="secured").ToList();
            if(secured.Count==0) {Restore();return;}
            string identity=string.Join("|",members.OfType<ShipInstance>().OrderBy(s=>s.ShipId).Select(s=>s.ShipId+":"+
                (s.SceneRoot is SceneShipRoot view && view.IsValid ? view.GameObject.GetEntityId()+":"+view.GameObject.transform.localToWorldMatrix.ToString() : "missing")
                +":"+(s.DockingPorts.Count>0 && s.DockingPorts[0] is GdDict edge ? edge.GetBool("connection_open").ToString():"")));
            if(identity==_identity)return;
            Restore();_identity=identity;_home=root.GameObject;
            foreach(var ship in members.OfType<ShipInstance>()) if(ship.SceneRoot is SceneShipRoot view && view.IsValid) {_roots.Add(view.GameObject);_owners[view.GameObject]=ship;}
            foreach(var ship in secured)
            {
                if(!(ship.SceneRoot is SceneShipRoot mobile)||!mobile.IsValid)continue;
                var edge=(GdDict)ship.DockingPorts[0];
                var h=DockingManager.UnpackPort(edge.GetDictOrEmpty("host_local_port"));
                var m=DockingManager.UnpackPort(edge.GetDictOrEmpty("mobile_local_port"));
                Vector3 endpoint=root.GameObject.transform.TransformPoint(Frame.ToUnity((Vec3)h["position"]));
                HideBoundary(root.GameObject,endpoint);HideBoundary(mobile.GameObject,endpoint);
                var door=new GameObject("HomeConnection_"+ship.ShipId);door.transform.SetParent(root.GameObject.transform,false);
                door.transform.localPosition=Frame.ToUnity((Vec3)h["position"])+Vector3.up*0.12f;
                door.transform.localRotation=Quaternion.LookRotation(Frame.ToUnity((Vec3)h["facing"]));
                Box(door.transform,"LeftFrame",new Vector3(-1.45f,1.5f,0),new Vector3(1.1f,3,0.25f),new Color(.16f,.25f,.30f));
                Box(door.transform,"RightFrame",new Vector3(1.45f,1.5f,0),new Vector3(1.1f,3,0.25f),new Color(.16f,.25f,.30f));
                var panel=Box(door.transform,"Door",new Vector3(0,1.5f,0),new Vector3(1.8f,3,.16f),new Color(.28f,.44f,.48f));
                var blocker=panel.GetComponent<BoxCollider>();NavMeshBlocker.Attach(blocker);
                blocker.enabled=!edge.GetBool("connection_open");panel.GetComponent<Renderer>().enabled=blocker.enabled;
                _doors.Add(door);
            }
            Physics.SyncTransforms();ShipNavMesh.BuildAssembly(_home,_roots);
        }
        static GameObject Box(Transform parent,string name,Vector3 position,Vector3 size,Color color)
        {
            var go=RuntimeVisualCatalog.AddMesh(parent,name,RuntimeVisualCatalog.Cube,RuntimeVisualCatalog.Material(color),position,Quaternion.identity,size,PhysicsLayers.Structure);
            go.AddComponent<BoxCollider>();return go;
        }
        void HideBoundary(GameObject root,Vector3 point)
        {
            foreach(var module in root.GetComponentsInChildren<StructuralModule>())
                if(module.layer=="edge" && module.moduleId.StartsWith("wall_") && Vector3.Distance(module.transform.position,point)<0.5f)
                {
                    module.ConnectionOpening=true;_modules.Add(module);
                    foreach(var c in module.GetComponentsInChildren<Collider>(true)) if(c.enabled&&!c.isTrigger) {c.enabled=false;_colliders.Add(c);}
                    foreach(var r in module.GetComponentsInChildren<Renderer>(true)) if(r.enabled) {r.enabled=false;_renderers.Add(r);}
                }
        }
        public void Restore()
        {
            foreach(var module in _modules)if(module!=null){module.ConnectionOpening=false;module.SetIntegrity(module.integrityState);}
            foreach(var c in _colliders) if(c!=null && c.GetComponentInParent<StructuralModule>()?.integrityState!=StructuralModule.IntegrityDestroyed)c.enabled=true;
            foreach(var r in _renderers)if(r!=null)r.enabled=true;
            foreach(var door in _doors)if(door!=null) {door.SetActive(false);if(Application.isPlaying)Object.Destroy(door);else Object.DestroyImmediate(door);}
            if(_home!=null)ShipNavMesh.ResetComposite(_home);
            // A departing craft may already belong to a different active dock composite. Do not
            // reactivate its standalone surface over that new host's navigation data.
            foreach(var root in _roots)if(root!=null && _owners.TryGetValue(root,out var owner) && owner.ParentShip==null)ShipNavMesh.Build(root);
            _colliders.Clear();_renderers.Clear();_modules.Clear();_doors.Clear();_roots.Clear();_owners.Clear();_home=null;_identity="";
        }
    }
}
