// Float32 subset of Godot's AABB (core/math/aabb.h @ 4.7) used by ship_occupancy.gd.

using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>Godot <c>AABB(position, size)</c> in the Godot frame, float32.</summary>
    public readonly struct Aabb3 : IEquatable<Aabb3>
    {
        public readonly Vec3 Position;
        public readonly Vec3 Size;

        public Aabb3(Vec3 position, Vec3 size)
        {
            Position = position;
            Size = size;
        }

        /// <summary>Godot <c>AABB.grow(by)</c>: position -= by, size += 2 * by on every axis.</summary>
        public Aabb3 Grow(float by)
        {
            float twice = (float)(2.0f * by);
            return new Aabb3(
                new Vec3((float)(Position.X - by), (float)(Position.Y - by), (float)(Position.Z - by)),
                new Vec3((float)(Size.X + twice), (float)(Size.Y + twice), (float)(Size.Z + twice)));
        }

        /// <summary>Godot <c>AABB.has_point(p)</c>: inclusive on both faces (compares against position + size).</summary>
        public bool HasPoint(Vec3 p)
        {
            if (p.X < Position.X) return false;
            if (p.Y < Position.Y) return false;
            if (p.Z < Position.Z) return false;
            if (p.X > (float)(Position.X + Size.X)) return false;
            if (p.Y > (float)(Position.Y + Size.Y)) return false;
            if (p.Z > (float)(Position.Z + Size.Z)) return false;
            return true;
        }

        public bool Equals(Aabb3 o) => Position == o.Position && Size == o.Size;
        public override bool Equals(object obj) => obj is Aabb3 o && Equals(o);
        public override int GetHashCode() => unchecked(Position.GetHashCode() * 397 ^ Size.GetHashCode());
        public override string ToString() => "[P: " + Position + ", S: " + Size + "]";
    }
}
