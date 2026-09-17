using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// The ONLY place that converts between Godot's right-handed frame (used by every Core model, layout, save,
    /// and nav graph) and Unity's left-handed frame.
    ///
    /// Verified against glTFast 6.20 imports of the structural kit: a socket authored at Godot (+2, 0, 0)
    /// arrives at Unity (-2, 0, 0), i.e. glTFast mirrors the X axis. Mirroring conjugates a rotation about Y to
    /// its negative, so a Godot yaw of <c>a</c> degrees is a Unity yaw of <c>-a</c>.
    /// FrameConventionTests guards this; never convert coordinates anywhere else.
    /// </summary>
    public static class Frame
    {
        public static Vector3 ToUnity(Vec3 godot) => new Vector3(-godot.X, godot.Y, godot.Z);

        public static Vector3 ToUnity(double x, double y, double z) => new Vector3(-(float)x, (float)y, (float)z);

        public static Vec3 ToGodot(Vector3 unity) => new Vec3(-unity.x, unity.y, unity.z);

        /// <summary>Godot <c>rotation_degrees.y</c> to Unity yaw in degrees.</summary>
        public static float ToUnityYaw(double godotYawDegrees) => -(float)godotYawDegrees;

        public static double ToGodotYaw(float unityYawDegrees) => -unityYawDegrees;

        public static Quaternion YawRotation(double godotYawDegrees) => Quaternion.Euler(0f, ToUnityYaw(godotYawDegrees), 0f);

        /// <summary>
        /// A Godot node rotation (<c>rotation_degrees</c>, YXZ order) for a non-light node: mirroring X keeps pitch and
        /// negates yaw and roll (M·R·M), with the same YXZ composition Unity's <c>Quaternion.Euler</c> uses.
        /// </summary>
        public static Quaternion Rotation(Vec3 godotEulerDegrees) =>
            Quaternion.Euler(godotEulerDegrees.X, -godotEulerDegrees.Y, -godotEulerDegrees.Z);

        /// <summary>
        /// A Godot light or camera rotation: Godot lights/cameras face their local −Z, Unity's face +Z, so after the
        /// frame conversion the forward axis is flipped with a 180° turn about local Y.
        /// </summary>
        public static Quaternion LightRotation(Vec3 godotEulerDegrees) => Rotation(godotEulerDegrees) * Quaternion.Euler(0f, 180f, 0f);

        /// <summary>
        /// Box extents are unsigned, so a Godot box size maps to the same Unity size; only its center moves.
        /// </summary>
        public static Vector3 SizeToUnity(Vec3 godotSize) => new Vector3(godotSize.X, godotSize.Y, godotSize.Z);

        /// <summary>Converts a Godot <c>[x, y, z]</c> array (as stored in layouts and contracts).</summary>
        public static Vector3 ToUnity(object godotArray) => ToUnity(Vec3.FromArray(godotArray));

        /// <summary>
        /// A Godot node <c>Basis</c> (given by its columns: the node's local X, Y, Z axes in the Godot parent frame)
        /// to a Unity local rotation for a non-light node. Mirroring X conjugates the rotation (M·R·M): the Unity
        /// local Y and Z axes are the mirrored Godot Y and Z columns, and local X follows from handedness.
        /// Orthonormal (unscaled) bases only.
        /// </summary>
        public static Quaternion BasisRotation(Vec3 godotX, Vec3 godotY, Vec3 godotZ)
        {
            _ = godotX;
            return Quaternion.LookRotation(ToUnity(godotZ), ToUnity(godotY));
        }

        /// <summary>
        /// A Godot <c>Transform3D</c> (orthonormal basis given by rows) applied as a Unity LOCAL pose on
        /// <paramref name="target"/> (non-light node).
        /// </summary>
        public static void ApplyLocal(Transform target, Xform3 godot)
        {
            Basis3 b = godot.Basis;
            var x = new Vec3(b.Row0.X, b.Row1.X, b.Row2.X);
            var y = new Vec3(b.Row0.Y, b.Row1.Y, b.Row2.Y);
            var z = new Vec3(b.Row0.Z, b.Row1.Z, b.Row2.Z);
            target.localPosition = ToUnity(godot.Origin);
            target.localRotation = BasisRotation(x, y, z);
        }

        /// <summary>Inverse of <see cref="ApplyLocal"/>: a Unity local pose as a Godot <c>Transform3D</c>.</summary>
        public static Xform3 ToGodotLocal(Transform source)
        {
            ToGodotBasis(source.localRotation, out Vec3 x, out Vec3 y, out Vec3 z);
            var basis = new Basis3(new Vec3(x.X, y.X, z.X), new Vec3(x.Y, y.Y, z.Y), new Vec3(x.Z, y.Z, z.Z));
            return new Xform3(basis, ToGodot(source.localPosition));
        }

        /// <summary>Inverse of <see cref="BasisRotation"/>: the Godot basis columns of a Unity local rotation.</summary>
        public static void ToGodotBasis(Quaternion unityRotation, out Vec3 godotX, out Vec3 godotY, out Vec3 godotZ)
        {
            Vector3 ux = unityRotation * Vector3.right;
            Vector3 uy = unityRotation * Vector3.up;
            Vector3 uz = unityRotation * Vector3.forward;
            // Columns of M·R'·M: x = −M(x'), y = M(y'), z = M(z').
            godotX = new Vec3(ux.x, -ux.y, -ux.z);
            godotY = ToGodot(uy);
            godotZ = ToGodot(uz);
        }
    }
}
