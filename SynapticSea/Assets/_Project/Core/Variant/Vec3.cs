using System;

namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// Godot <c>Vector3</c> (real_t = float32). Lives in Godot's right-handed frame; the Runtime layer
    /// converts to <c>UnityEngine.Vector3</c> in exactly one place (Frame/SpaceConv).
    /// All arithmetic is explicitly narrowed to float32 to match Godot's single-precision build.
    /// </summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly float X, Y, Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public Vec3(double x, double y, double z) : this((float)x, (float)y, (float)z) { }

        public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);
        public static readonly Vec3 One = new Vec3(1f, 1f, 1f);
        public static readonly Vec3 Up = new Vec3(0f, 1f, 0f);
        public static readonly Vec3 Down = new Vec3(0f, -1f, 0f);
        public static readonly Vec3 Forward = new Vec3(0f, 0f, -1f);
        public static readonly Vec3 Back = new Vec3(0f, 0f, 1f);
        public static readonly Vec3 Left = new Vec3(-1f, 0f, 0f);
        public static readonly Vec3 Right = new Vec3(1f, 0f, 0f);
        public static readonly Vec3 Inf = new Vec3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3((float)(a.X + b.X), (float)(a.Y + b.Y), (float)(a.Z + b.Z));
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3((float)(a.X - b.X), (float)(a.Y - b.Y), (float)(a.Z - b.Z));
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3((float)(a.X * s), (float)(a.Y * s), (float)(a.Z * s));
        public static Vec3 operator *(float s, Vec3 a) => a * s;
        public static Vec3 operator *(Vec3 a, Vec3 b) => new Vec3((float)(a.X * b.X), (float)(a.Y * b.Y), (float)(a.Z * b.Z));
        public static Vec3 operator /(Vec3 a, float s) => new Vec3((float)(a.X / s), (float)(a.Y / s), (float)(a.Z / s));
        public static bool operator ==(Vec3 a, Vec3 b) => a.Equals(b);
        public static bool operator !=(Vec3 a, Vec3 b) => !a.Equals(b);

        public float LengthSquared() => (float)((float)((float)(X * X) + (float)(Y * Y)) + (float)(Z * Z));
        public float Length() => (float)Math.Sqrt(LengthSquared());
        public float DistanceTo(Vec3 b) => (b - this).Length();
        public float DistanceSquaredTo(Vec3 b) => (b - this).LengthSquared();
        public float Dot(Vec3 b) => (float)((float)((float)(X * b.X) + (float)(Y * b.Y)) + (float)(Z * b.Z));

        public Vec3 Cross(Vec3 b) => new Vec3(
            (float)((float)(Y * b.Z) - (float)(Z * b.Y)),
            (float)((float)(Z * b.X) - (float)(X * b.Z)),
            (float)((float)(X * b.Y) - (float)(Y * b.X)));

        /// <summary>Godot <c>normalized()</c>: returns zero for a zero vector.</summary>
        public Vec3 Normalized()
        {
            float l = LengthSquared();
            if (l == 0f) return Zero;
            l = (float)Math.Sqrt(l);
            return new Vec3((float)(X / l), (float)(Y / l), (float)(Z / l));
        }

        public Vec3 Lerp(Vec3 to, float weight) => new Vec3(
            (float)(X + (float)(weight * (float)(to.X - X))),
            (float)(Y + (float)(weight * (float)(to.Y - Y))),
            (float)(Z + (float)(weight * (float)(to.Z - Z))));

        /// <summary>Godot <c>move_toward()</c>.</summary>
        public Vec3 MoveToward(Vec3 to, float delta)
        {
            Vec3 vd = to - this;
            float len = vd.Length();
            return len <= delta || len < (float)GdMath.CmpEpsilon ? to : this + (vd / len) * delta;
        }

        /// <summary>Rotation about the Y axis, matching <c>v.rotated(Vector3.UP, angle)</c> for a unit Y axis.</summary>
        public Vec3 RotatedY(float angleRadians)
        {
            double c = Math.Cos(angleRadians), s = Math.Sin(angleRadians);
            return new Vec3((float)(X * c + Z * s), Y, (float)(-X * s + Z * c));
        }

        public bool IsEqualApprox(Vec3 b) =>
            GdMath.IsEqualApprox(X, b.X) && GdMath.IsEqualApprox(Y, b.Y) && GdMath.IsEqualApprox(Z, b.Z);

        public bool IsZeroApprox() => GdMath.IsZeroApprox(X) && GdMath.IsZeroApprox(Y) && GdMath.IsZeroApprox(Z);

        public Vec3 Abs() => new Vec3(Math.Abs(X), Math.Abs(Y), Math.Abs(Z));
        public Vec3 Floor() => new Vec3((float)Math.Floor(X), (float)Math.Floor(Y), (float)Math.Floor(Z));
        public Vec3 Round() => new Vec3((float)GdMath.Round(X), (float)GdMath.Round(Y), (float)GdMath.Round(Z));

        /// <summary>Flattened form used in summaries and saves: <c>[x, y, z]</c> as doubles.</summary>
        public GdArray ToArray() => GdArray.Of((double)X, (double)Y, (double)Z);

        public static Vec3 FromArray(object v, Vec3 fallback = default)
        {
            if (v is Vec3 already) return already;
            if (v is GdArray a && a.Count >= 3)
                return new Vec3(V.F64(a[0]), V.F64(a[1]), V.F64(a[2]));
            return fallback;
        }

        public bool Equals(Vec3 o) => X.Equals(o.X) && Y.Equals(o.Y) && Z.Equals(o.Z);
        public override bool Equals(object obj) => obj is Vec3 o && Equals(o);
        public override int GetHashCode() => unchecked((X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode());

        /// <summary>Godot's <c>str(Vector3)</c> form, e.g. <c>(1.0, 2.5, 0.0)</c>.</summary>
        public override string ToString() =>
            "(" + GdFloatFormat.NumReal(X, true) + ", " + GdFloatFormat.NumReal(Y, true) + ", " + GdFloatFormat.NumReal(Z, true) + ")";
    }
}
