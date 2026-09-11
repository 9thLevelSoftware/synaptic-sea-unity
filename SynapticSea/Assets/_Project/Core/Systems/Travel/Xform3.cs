// Float32 subset of Godot's Basis / Transform3D used by the docking ports (docking_manager.gd, dock_ports.gd).
// Formulas follow core/math/basis.cpp and transform_3d.h at Godot 4.7; every product/sum is narrowed to float32
// in the same order as the C++ (real_t = float), matching Godot's single-precision build.

using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Godot <c>Basis</c> (row-major: <c>rows[i]</c> is the i-th row, float32).</summary>
    public readonly struct Basis3 : IEquatable<Basis3>
    {
        public readonly Vec3 Row0, Row1, Row2;

        public Basis3(Vec3 row0, Vec3 row1, Vec3 row2)
        {
            Row0 = row0;
            Row1 = row1;
            Row2 = row2;
        }

        public static readonly Basis3 Identity = new Basis3(new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f), new Vec3(0f, 0f, 1f));

        /// <summary>
        /// Godot <c>Basis(axis, angle)</c> (Basis::set_axis_angle). The axis must be normalized; the angle is real_t,
        /// so a GDScript float argument is narrowed to float32 first, and cos/sin are evaluated at float precision.
        /// </summary>
        public static Basis3 FromAxisAngle(Vec3 axis, float angle)
        {
            float sqX = (float)(axis.X * axis.X);
            float sqY = (float)(axis.Y * axis.Y);
            float sqZ = (float)(axis.Z * axis.Z);
            float cosine = (float)Math.Cos(angle);
            float r00 = (float)(sqX + (float)(cosine * (float)(1.0f - sqX)));
            float r11 = (float)(sqY + (float)(cosine * (float)(1.0f - sqY)));
            float r22 = (float)(sqZ + (float)(cosine * (float)(1.0f - sqZ)));

            float sine = (float)Math.Sin(angle);
            float t = (float)(1f - cosine);

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

            return new Basis3(new Vec3(r00, r01, r02), new Vec3(r10, r11, r12), new Vec3(r20, r21, r22));
        }

        /// <summary>Godot <c>basis * v</c> (Basis::xform): <c>(rows[0].dot(v), rows[1].dot(v), rows[2].dot(v))</c>.</summary>
        public Vec3 Xform(Vec3 v) => new Vec3(Row0.Dot(v), Row1.Dot(v), Row2.Dot(v));

        public static Vec3 operator *(Basis3 b, Vec3 v) => b.Xform(v);

        public bool Equals(Basis3 o) => Row0 == o.Row0 && Row1 == o.Row1 && Row2 == o.Row2;
        public override bool Equals(object obj) => obj is Basis3 o && Equals(o);
        public override int GetHashCode() => unchecked((Row0.GetHashCode() * 397 ^ Row1.GetHashCode()) * 397 ^ Row2.GetHashCode());

        /// <summary>Godot's <c>str(Basis)</c> form: <c>[X: (..), Y: (..), Z: (..)]</c> (columns).</summary>
        public override string ToString() =>
            "[X: " + new Vec3(Row0.X, Row1.X, Row2.X) + ", Y: " + new Vec3(Row0.Y, Row1.Y, Row2.Y) + ", Z: " + new Vec3(Row0.Z, Row1.Z, Row2.Z) + "]";
    }

    /// <summary>Godot <c>Transform3D</c> (float32 basis + origin).</summary>
    public readonly struct Xform3 : IEquatable<Xform3>
    {
        public readonly Basis3 Basis;
        public readonly Vec3 Origin;

        public Xform3(Basis3 basis, Vec3 origin)
        {
            Basis = basis;
            Origin = origin;
        }

        public static readonly Xform3 Identity = new Xform3(Basis3.Identity, Vec3.Zero);

        /// <summary>Godot <c>transform * v</c> (Transform3D::xform): <c>basis.rows[i].dot(v) + origin[i]</c>.</summary>
        public Vec3 Xform(Vec3 v) => new Vec3(
            (float)(Basis.Row0.Dot(v) + Origin.X),
            (float)(Basis.Row1.Dot(v) + Origin.Y),
            (float)(Basis.Row2.Dot(v) + Origin.Z));

        public static Vec3 operator *(Xform3 x, Vec3 v) => x.Xform(v);

        public bool Equals(Xform3 o) => Basis.Equals(o.Basis) && Origin == o.Origin;
        public override bool Equals(object obj) => obj is Xform3 o && Equals(o);
        public override int GetHashCode() => unchecked(Basis.GetHashCode() * 397 ^ Origin.GetHashCode());
        public override string ToString() => Basis + " - " + Origin;
    }
}
