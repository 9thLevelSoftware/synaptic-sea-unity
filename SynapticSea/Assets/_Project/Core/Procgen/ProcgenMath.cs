// Procgen float32 math helpers reproducing Godot 4.7.1-stable core/math (no GDScript source).
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Bit-faithful float32 reproductions of the Godot <c>Vector3</c>/<c>Basis</c> operations procgen uses.
    /// Godot is built with <c>real_t = float</c>; every intermediate below is narrowed to float32 in the same
    /// order as the C++ source (no FMA contraction, as with MSVC /fp:precise and x86-64 GCC defaults).
    /// </summary>
    public static class ProcgenMath
    {
        /// <summary>
        /// <c>Math::cos(float)</c> (<c>std::cos</c> on a float). Computed as the double cosine rounded to float32,
        /// which is the correctly rounded result; the platform <c>cosf</c> Godot links is accurate to &lt; 1 ulp and
        /// agrees except in rare last-bit cases.
        /// </summary>
        public static float CosF(float x) => (float)Math.Cos(x);

        /// <summary><c>Math::sin(float)</c>; see <see cref="CosF"/>.</summary>
        public static float SinF(float x) => (float)Math.Sin(x);

        /// <summary>
        /// <c>Vector3::rotated(axis, angle)</c> = <c>Basis(axis, angle).xform(v)</c>, with
        /// <c>Basis::set_axis_angle</c> (core/math/basis.cpp) and <c>Basis::xform</c> (row dot products).
        /// </summary>
        public static Vec3 Rotated(Vec3 v, Vec3 axis, float angle)
        {
            // Basis::set_axis_angle
            float axSqX = (float)(axis.X * axis.X);
            float axSqY = (float)(axis.Y * axis.Y);
            float axSqZ = (float)(axis.Z * axis.Z);
            float cosine = CosF(angle);
            float r00 = (float)(axSqX + (float)(cosine * (float)(1.0f - axSqX)));
            float r11 = (float)(axSqY + (float)(cosine * (float)(1.0f - axSqY)));
            float r22 = (float)(axSqZ + (float)(cosine * (float)(1.0f - axSqZ)));

            float sine = SinF(angle);
            float t = (float)(1.0f - cosine);

            float xyzt = (float)((float)(axis.X * axis.Y) * t);
            float zyxs = (float)(axis.Z * sine);
            float r01 = (float)(xyzt - zyxs);
            float r10 = (float)(xyzt + zyxs);

            xyzt = (float)((float)(axis.X * axis.Z) * t);
            zyxs = (float)(axis.Y * sine);
            float r02 = (float)(xyzt + zyxs);
            float r20 = (float)(xyzt - zyxs);

            xyzt = (float)((float)(axis.Y * axis.Z) * t);
            zyxs = (float)(axis.X * sine);
            float r12 = (float)(xyzt - zyxs);
            float r21 = (float)(xyzt + zyxs);

            // Basis::xform: Vector3(rows[0].dot(v), rows[1].dot(v), rows[2].dot(v))
            return new Vec3(Dot(r00, r01, r02, v), Dot(r10, r11, r12, v), Dot(r20, r21, r22, v));
        }

        /// <summary><c>Vector3::dot</c>: <c>x * p.x + y * p.y + z * p.z</c>, evaluated left to right in float32.</summary>
        static float Dot(float x, float y, float z, Vec3 p) =>
            (float)((float)((float)(x * p.X) + (float)(y * p.Y)) + (float)(z * p.Z));

        /// <summary>
        /// GDScript <c>v.rotated(Vector3.UP, deg_to_rad(yaw_degrees))</c>: the global <c>deg_to_rad</c> works in double
        /// (<c>deg * (PI / 180.0)</c>) and the result is narrowed to real_t when passed to <c>rotated</c>.
        /// </summary>
        public static Vec3 RotatedUpDegrees(Vec3 v, double yawDegrees) =>
            Rotated(v, Vec3.Up, (float)GdMath.DegToRad(yawDegrees));
    }
}
