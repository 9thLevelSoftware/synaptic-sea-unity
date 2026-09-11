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
    }
}
