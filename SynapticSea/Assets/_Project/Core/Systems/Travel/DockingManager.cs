// Ported from scripts/systems/docking_manager.gd @ 96ecb2b0

using System;
using System.Collections.Generic;
using SynapticSea.Core.Variant;

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
                && p["position"] is Vec3 && p["facing"] is Vec3 facing
                && facing.Length() > 0.0001;
        }

        public static GdDict Dock(IDockableShip hostInst, IDockableShip mobileInst, GdDict hostPort, GdDict mobilePort)
        {
            // Reject null insts and self-docking (a ship docking to itself would create a self-referential
            // parent_ship/docked_ships cycle).
            if (hostInst == null || mobileInst == null || ReferenceEquals(hostInst, mobileInst))
                return Result(false, "dock_failed");
            if (!PortValid(hostPort) || !PortValid(mobilePort))
                return Result(false, "dock_failed");
            // GDScript also checked `"scene_root" in mobile_inst`; the interface always has the member.
            IShipSceneRoot root = mobileInst.SceneRoot;
            if (root == null || !root.IsValid)
                return Result(false, "dock_failed");
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
            mobileInst.DockingPorts = GdArray.Of(new GdDict { { "host_port", hostPort }, { "mobile_port", mobilePort } });
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
            return new GdDict
            {
                { "position", x * PortVec(localPort, "position", Vec3.Zero) },
                { "facing", (x.Basis * PortVec(localPort, "facing", Vec3.Forward)).Normalized() },
                { "type", V.Str(localPort.Get("type", "airlock")) },
                { "size_class", V.I64(localPort.Get("size_class", 1L)) },
                { "condition", V.Str(localPort.Get("condition", "intact")) },
            };
        }

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
