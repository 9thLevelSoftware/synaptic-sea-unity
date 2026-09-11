namespace SynapticSea.Runtime
{
    /// <summary>
    /// Project physics layers (ProjectSettings/TagManager; mirrored from the Editor ProjectSettingsBootstrap).
    /// Godot put every generated collider on layer 1; the port splits them by role. Raycasts ignore triggers.
    /// </summary>
    public static class PhysicsLayers
    {
        public const int Default = 0;
        public const int Player = 6;
        public const int Structure = 7;
        public const int ZoneBlocker = 8;
        /// <summary>Trigger volumes (Godot Area3D): objectives, portal detection, hazard / atmosphere zones.</summary>
        public const int Sensor = 9;
        public const int Threat = 10;
        /// <summary>Visual-only props (no collider).</summary>
        public const int Prop = 11;
        /// <summary>Portal blockers (Godot StaticBody3D "PortalBlocker").</summary>
        public const int Portal = 12;
        public const int Ceiling = 13;
        public const int Hallucination = 14;
        public const int Walkable = 15;
    }
}
