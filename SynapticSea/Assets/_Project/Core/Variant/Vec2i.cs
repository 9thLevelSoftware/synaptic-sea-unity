using System;

namespace SynapticSea.Core.Variant
{
    /// <summary>Godot <c>Vector2i</c> (int32 components). Used for procgen grid cells.</summary>
    public readonly struct Vec2i : IEquatable<Vec2i>
    {
        public readonly int X, Y;

        public Vec2i(int x, int y)
        {
            X = x;
            Y = y;
        }

        public static readonly Vec2i Zero = new Vec2i(0, 0);
        public static readonly Vec2i Up = new Vec2i(0, -1);
        public static readonly Vec2i Down = new Vec2i(0, 1);
        public static readonly Vec2i Left = new Vec2i(-1, 0);
        public static readonly Vec2i Right = new Vec2i(1, 0);

        public static Vec2i operator +(Vec2i a, Vec2i b) => new Vec2i(unchecked(a.X + b.X), unchecked(a.Y + b.Y));
        public static Vec2i operator -(Vec2i a, Vec2i b) => new Vec2i(unchecked(a.X - b.X), unchecked(a.Y - b.Y));
        public static Vec2i operator -(Vec2i a) => new Vec2i(-a.X, -a.Y);
        public static Vec2i operator *(Vec2i a, int s) => new Vec2i(unchecked(a.X * s), unchecked(a.Y * s));
        public static bool operator ==(Vec2i a, Vec2i b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(Vec2i a, Vec2i b) => !(a == b);

        /// <summary>Godot orders Vector2i by x, then y.</summary>
        public static bool operator <(Vec2i a, Vec2i b) => a.X == b.X ? a.Y < b.Y : a.X < b.X;
        public static bool operator >(Vec2i a, Vec2i b) => b < a;

        public int ManhattanTo(Vec2i o) => Math.Abs(X - o.X) + Math.Abs(Y - o.Y);

        public GdArray ToArray() => GdArray.Of((long)X, (long)Y);

        public static Vec2i FromArray(object v, Vec2i fallback = default)
        {
            if (v is Vec2i already) return already;
            if (v is GdArray a && a.Count >= 2) return new Vec2i((int)V.I64(a[0]), (int)V.I64(a[1]));
            return fallback;
        }

        public bool Equals(Vec2i o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is Vec2i o && Equals(o);
        public override int GetHashCode() => unchecked(X * 73856093 ^ Y * 19349663);
        public override string ToString() => "(" + X + ", " + Y + ")";
    }
}
