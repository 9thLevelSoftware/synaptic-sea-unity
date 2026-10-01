// Ported from scripts/systems/docking_manager.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using System.Globalization;
using SynapticSea.Core.Variant;
using SynapticSea.Core.Session;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// RUNTIME: the ship's <c>scene_root</c> Node3D, reduced to the members DockingManager touches.
    /// The Runtime layer implements it over the ship's root GameObject and converts between the Godot frame
    /// (<see cref="Xform3"/>) and Unity's Transform.
    /// </summary>
    public interface IShipSceneRoot
    {
        /// <summary><c>is_instance_valid(root) and root is Node3D</c>.</summary>
        bool IsValid { get; }

        /// <summary><c>Node3D.is_inside_tree()</c>.</summary>
        bool IsInsideTree { get; }

        /// <summary><c>Node3D.transform</c> (local to the parent), in the Godot frame.</summary>
        Xform3 Transform { get; set; }

        /// <summary><c>Node3D.global_transform</c>, in the Godot frame.</summary>
        Xform3 GlobalTransform { get; }
    }

    /// <summary>
    /// The ShipInstance fields DockingManager reads and writes (<c>scene_root</c>, <c>parent_ship</c>,
    /// <c>docked_ships</c>, <c>docking_ports</c>). ShipInstance's C# port should implement it.
    /// </summary>
    public interface IDockableShip
    {
        IShipSceneRoot SceneRoot { get; }
        IDockableShip ParentShip { get; set; }
        IList<IDockableShip> DockedShips { get; }
        GdArray DockingPorts { get; set; }
    }

    /// <summary>
    /// Pure docking math + relationship bookkeeping. Aligns a mobile ship's dock port to a host ship's dock port
    /// (coincident position, opposing facing, yaw-only) and writes the parent/child fields already declared on
    /// ShipInstance. No scene-tree ownership: callers own add_child/remove_child of ship_roots.
    ///
    /// PORT-SPACE CONTRACT: host_port is WORLD-space (the host is already placed in the world); mobile_port is
    /// MOBILE-LOCAL (relative to the mobile ship's own root); Dock() computes the mobile root's transform that
    /// brings it onto the host port. DockPorts.For*() return SHIP-LOCAL descriptors, so a caller docking a real,
    /// non-origin host must lift the host's local port to world first (<see cref="HostPortToWorld"/>).
    /// </summary>
    public static class DockingManager
    {
        /// <summary>Yaw (radians) that rotates <paramref name="from"/> onto <paramref name="to"/> in the X-Z plane.</summary>
        static double YawBetween(Vec3 from, Vec3 to)
        {
            double a = Math.Atan2(from.X, from.Z);
            double b = Math.Atan2(to.X, to.Z);
            return b - a;
        }

        static Vec3 PortVec(GdDict port, string key, Vec3 fallback) => Vec3.FromArray(port?.Get(key), fallback);

        public static Xform3 ComputeMobileTransform(GdDict hostPort, GdDict mobilePort)
        {
            Vec3 hostPos = PortVec(hostPort, "position", Vec3.Zero);
            Vec3 hostFacing = PortVec(hostPort, "facing", Vec3.Forward).Normalized();
            Vec3 localPos = PortVec(mobilePort, "position", Vec3.Zero);
            Vec3 localFacing = PortVec(mobilePort, "facing", Vec3.Forward).Normalized();
            // Rotate the mobile so its local port facing becomes the OPPOSITE of the host facing.
            Vec3 targetFacing = -hostFacing;
            double yaw = YawBetween(localFacing, targetFacing);
            // Basis(Vector3.UP, yaw): the angle parameter is real_t, so the double yaw narrows to float32.
            Basis3 basis = Basis3.FromAxisAngle(Vec3.Up, (float)yaw);
            // Translate so the (rotated) local port position lands on the host port position.
            Vec3 origin = hostPos - (basis * localPos);
            return new Xform3(basis, origin);
        }

        static bool PortValid(GdDict p)
        {
            return p != null && p.Has("position") && p.Has("facing")
                && p["position"] is Vec3 position && p["facing"] is Vec3 facing
                && Finite(position) && Finite(facing) && facing.Length() > 0.0001;
        }

        static bool Finite(Vec3 v) => !double.IsNaN(v.X) && !double.IsInfinity(v.X)
            && !double.IsNaN(v.Y) && !double.IsInfinity(v.Y)
            && !double.IsNaN(v.Z) && !double.IsInfinity(v.Z);

        /// <summary>Validate the existing reciprocal tree before a transactional graph change.</summary>
        public static bool TryConnectedMembers(IDockableShip member, out List<IDockableShip> members, out string reason)
        {
            members = new List<IDockableShip>(); reason = "ok";
            if (member == null) { reason = "missing_member"; return false; }
            var ancestry = new HashSet<IDockableShip>();
            var root = member;
            while (root.ParentShip != null)
            {
                if (!ancestry.Add(root)) { reason = "dock_cycle"; return false; }
                var parent = root.ParentShip;
                int references = 0;
                foreach (var child in parent.DockedShips) if (ReferenceEquals(child, root)) references++;
                if (references != 1) { reason = "inconsistent_dock_parent"; return false; }
                root = parent;
            }
            var seen = new HashSet<IDockableShip>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<IDockableShip>(); pending.Push(root);
            while (pending.Count > 0)
            {
                var ship = pending.Pop();
                if (ship == null || !seen.Add(ship)) { reason = "duplicate_or_cyclic_member"; return false; }
                if (ship is ShipInstance instance && (string.IsNullOrEmpty(instance.ShipId) || !ids.Add(instance.ShipId)))
                { reason = "invalid_ship_identity"; return false; }
                members.Add(ship);
                foreach (var child in ship.DockedShips)
                {
                    if (child == null || !ReferenceEquals(child.ParentShip, ship))
                    { reason = "inconsistent_dock_child"; return false; }
                    pending.Push(child);
                }
            }
            return true;
        }

        static bool SiteOccupied(IDockableShip ship, string site, IDockableShip replacingMobile)
        {
            // Unnamed legacy math-only endpoints have no site-ownership contract.
            if (site.Length == 0) return false;
            if (!ReferenceEquals(ship, replacingMobile) && ship.ParentShip != null
                && ship.DockingPorts.Count > 0 && ship.DockingPorts[0] is GdDict own
                && own.GetDictOrEmpty("mobile_local_port").GetString("site_id") == site)
                return true;
            foreach (var child in ship.DockedShips)
                if (!ReferenceEquals(child, replacingMobile) && child.DockingPorts.Count > 0
                    && child.DockingPorts[0] is GdDict edge
                    && edge.GetDictOrEmpty("host_local_port").GetString("site_id") == site)
                    return true;
            return false;
        }

        public static GdDict CanDock(IDockableShip hostInst, IDockableShip mobileInst, GdDict hostPort, GdDict mobilePort)
        {
            // Reject null insts and self-docking (a ship docking to itself would create a self-referential
            // parent_ship/docked_ships cycle).
            if (hostInst == null || mobileInst == null || ReferenceEquals(hostInst, mobileInst))
                return Result(false, "dock_failed");
            if (!PortValid(hostPort) || !PortValid(mobilePort))
                return Result(false, "dock_failed");
            // Reject before severing a previous berth or changing any transform.
            if (!TryConnectedMembers(hostInst, out _, out string hostFailure)) return Result(false, hostFailure);
            if (!TryConnectedMembers(mobileInst, out _, out string mobileFailure)) return Result(false, mobileFailure);
            for (var ancestor = hostInst; ancestor != null; ancestor = ancestor.ParentShip)
                if (ReferenceEquals(ancestor, mobileInst)) return Result(false, "dock_cycle");
            // A site can be the host OR mobile end of an existing edge. A wreck carrying
            // its shuttle must use another boundary when joining a home assembly.
            if (SiteOccupied(hostInst, hostPort.GetString("site_id"), mobileInst)
                || SiteOccupied(mobileInst, mobilePort.GetString("site_id"), mobileInst))
                return Result(false, "connection_site_occupied");
            // GDScript also checked `"scene_root" in mobile_inst`; the interface always has the member.
            IShipSceneRoot root = mobileInst.SceneRoot;
            if (root == null || !root.IsValid)
                return Result(false, "dock_failed");
            return Result(true, "ok");
        }

        public static GdDict Dock(IDockableShip hostInst, IDockableShip mobileInst, GdDict hostPort, GdDict mobilePort)
        {
            var validation = CanDock(hostInst, mobileInst, hostPort, mobilePort);
            if (!validation.GetBool("success")) return validation;
            IShipSceneRoot root = mobileInst.SceneRoot;
            // Sever any existing dock relationship first so the previous host's docked_ships list does not retain
            // a stale reference to this mobile ship.
            if (mobileInst.ParentShip != null)
                Undock(mobileInst);
            // RUNTIME: GDScript wrote `(root as Node3D).transform = ...`; the Runtime IShipSceneRoot applies it to the
            // ship root GameObject (converting from the Godot frame).
            root.Transform = ComputeMobileTransform(hostPort, mobilePort);
            mobileInst.ParentShip = hostInst;
            if (!hostInst.DockedShips.Contains(mobileInst))
                hostInst.DockedShips.Add(mobileInst);
            var connection = new GdDict { { "host_port", hostPort.DeepCopy() }, { "mobile_port", mobilePort.DeepCopy() } };
            if (hostInst.SceneRoot != null && hostInst.SceneRoot.IsValid && hostInst.SceneRoot.IsInsideTree)
            {
                Xform3 inv = SessionMath.AffineInverse(hostInst.SceneRoot.GlobalTransform);
                var local = hostPort.DeepCopy();
                local["position"] = inv * (Vec3)hostPort["position"];
                local["facing"] = (inv.Basis * (Vec3)hostPort["facing"]).Normalized();
                connection["connection_version"] = 1L;
                connection["host_local_port"] = PackPort(local);
                connection["mobile_local_port"] = PackPort(mobilePort);
                connection["connection_kind"] = "moored";
            }
            mobileInst.DockingPorts = GdArray.Of(connection);
            return Result(true, "ok");
        }

        /// <summary>
        /// Lifts a ship-LOCAL dock port to WORLD space via the host's placed transform. host_inst.scene_root must be
        /// in the scene tree for global_transform to be valid. Returns {} when the host has no valid in-tree
        /// scene_root or local_port is empty.
        /// </summary>
        public static GdDict HostPortToWorld(IDockableShip hostInst, GdDict localPort)
        {
            if (hostInst == null || localPort == null || localPort.IsEmpty)
                return new GdDict();
            IShipSceneRoot root = hostInst.SceneRoot;
            if (root == null || !root.IsValid || !root.IsInsideTree)
                return new GdDict();
            // RUNTIME: reads Node3D.global_transform through IShipSceneRoot.
            Xform3 x = root.GlobalTransform;
            var world = localPort.DeepCopy();
            world["position"] = x * PortVec(localPort, "position", Vec3.Zero);
            world["facing"] = (x.Basis * PortVec(localPort, "facing", Vec3.Forward)).Normalized();
            world["type"] = localPort.Get("type", "airlock");
            world["size_class"] = localPort.Get("size_class", 1L);
            world["condition"] = localPort.Get("condition", "intact");
            return world;
        }

        /// <summary>JSON-safe, ship-local endpoint contract. Never serialize world-space endpoints as local.</summary>
        public static GdDict PackPort(GdDict port)
        {
            if (!PortValid(port)) return new GdDict();
            var packed = port.DeepCopy();
            packed["position"] = ((Vec3)port["position"]).ToArray();
            packed["facing"] = ((Vec3)port["facing"]).ToArray();
            packed["type"] = port.Get("type", "airlock");
            packed["size_class"] = port.Get("size_class", 1L);
            if (packed.GetString("site_id").Length == 0)
            {
                Vec3 position = (Vec3)port["position"], facing = (Vec3)port["facing"];
                packed["site_id"] = string.Format(CultureInfo.InvariantCulture,
                    "endpoint-v1:{0}:{1}:{2}:{3}:{4}:{5}",
                    Math.Round(position.X * 1000), Math.Round(position.Y * 1000), Math.Round(position.Z * 1000),
                    Math.Round(facing.X * 1000), Math.Round(facing.Y * 1000), Math.Round(facing.Z * 1000));
            }
            return packed;
        }

        public static GdDict UnpackPort(GdDict packed)
        {
            if (packed == null || packed.GetArrayOrEmpty("position").Count != 3
                || packed.GetArrayOrEmpty("facing").Count != 3) return new GdDict();
            var port = packed.DeepCopy();
            port["position"] = Vec3.FromArray(packed["position"], Vec3.Inf);
            port["facing"] = Vec3.FromArray(packed["facing"], Vec3.Zero);
            return PortValid(port) ? port : new GdDict();
        }

        public static GdDict RestoreConnection(IDockableShip host, IDockableShip mobile, GdDict contract)
        {
            if (contract == null || contract.GetInt("connection_version") != 1) return Result(false, "unsupported_connection_contract");
            var localHost = UnpackPort(contract.GetDictOrEmpty("host_local_port"));
            var localMobile = UnpackPort(contract.GetDictOrEmpty("mobile_local_port"));
            if (!PortsCompatibleForRestore(localHost, localMobile)) return Result(false, "invalid_connection_endpoints");
            var result = Dock(host, mobile, HostPortToWorld(host, localHost), localMobile);
            if (result.GetBool("success") && mobile.DockingPorts.Count > 0)
            {
                ((GdDict)mobile.DockingPorts[0])["connection_kind"] = contract.GetString("connection_kind", "moored");
                if(contract.Has("connection_open")) ((GdDict)mobile.DockingPorts[0])["connection_open"] = contract.GetBool("connection_open");
            }
            return result;
        }

        static bool PortsCompatibleForRestore(GdDict a, GdDict b) => PortValid(a) && PortValid(b) && DockPorts.PortsCompatible(a, b);

        public static GdDict Undock(IDockableShip mobileInst)
        {
            if (mobileInst == null)
                return Result(false, "dock_failed");
            IDockableShip host = mobileInst.ParentShip;
            if (host == null)
                return Result(true, "not_docked");
            if (host.DockedShips.Contains(mobileInst))
                host.DockedShips.Remove(mobileInst);
            mobileInst.ParentShip = null;
            mobileInst.DockingPorts = new GdArray();
            return Result(true, "ok");
        }

        static GdDict Result(bool success, string reason) =>
            new GdDict { { "success", success }, { "reason", reason } };
    }
}
