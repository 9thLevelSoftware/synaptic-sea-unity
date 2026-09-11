// Ported from scripts/procgen/readability_prop_factory.gd @ 96ecb2b0

using SynapticSea.Core.Variant;
using UnityEngine;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Semantic readability props: roots with stable names and a <see cref="ReadabilityProp"/> component, each a
    /// multi-mesh primitive composition (unshaded colors) plus optional point lights and empty marker children.
    /// Godot's playable ship scene (not the loader) places them; positions are set by the caller except for route cues.
    /// </summary>
    public static class ReadabilityPropFactory
    {
        public const string OBJECTIVE_PREFIX = "ObjectiveAffordance_";
        public const string OBJECTIVE_KIND_SUPPLY = "ObjectiveSupplyCache";
        public const string OBJECTIVE_KIND_BREAKER = "ObjectiveBreakerPanel";
        public const string OBJECTIVE_KIND_MED = "ObjectiveMedTerminal";
        public const string OBJECTIVE_KIND_REACTOR_CONSOLE = "ObjectiveReactorConsole";
        public const string OBJECTIVE_KIND_GENERIC = "ObjectiveGeneric";

        public const string BLOCKED_NAME = "BlockedAffordance_01_BlockedBiomatter";
        public const string BLOCKED_KIND = "BlockedBiomatter";
        public const string RAMP_NAME = "VerticalAffordance_01_RampCue";
        public const string RAMP_KIND = "RampCue";
        public const string ENTRY_NAME = "EntryBeacon";
        public const string ENTRY_KIND = "EntryBeacon";
        public const string DESTINATION_NAME = "DestinationReactorCore";
        public const string DESTINATION_KIND = "DestinationReactorCore";
        public const string ROUTE_PREFIX = "RouteCue_";
        public const string ROUTE_KIND = "RouteCue";

        const int Layer = PhysicsLayers.Prop;

        public static GameObject CreateObjectiveProp(long sequence, string objectiveType)
        {
            string kind = ObjectiveKind(objectiveType);
            var root = BaseProp($"{OBJECTIVE_PREFIX}{GdString.FormatIntPadded(sequence, 2)}_{kind}", kind);
            var meta = root.GetComponent<ReadabilityProp>();
            meta.objectiveType = objectiveType;
            meta.sequence = sequence;
            AddObjectiveComposition(root.transform, kind);
            return root;
        }

        public static GameObject CreateBlockedBiomatter()
        {
            var root = BaseProp(BLOCKED_NAME, BLOCKED_KIND);
            var membrane = new Color(0.45f, 0.10f, 0.10f);
            var blob = new Color(0.85f, 0.20f, 0.15f);
            AddSphere(root.transform, "BlockedBiomatterMembrane", 1.20f, new Vec3(0f, 0.80f, 0f), membrane);
            AddSphere(root.transform, "BlockedBiomatterBlobA", 0.45f, new Vec3(0.85f, 0.30f, 0.55f), blob);
            AddSphere(root.transform, "BlockedBiomatterBlobB", 0.35f, new Vec3(-0.75f, 0.45f, 0.35f), blob);
            AddSphere(root.transform, "BlockedBiomatterBlobC", 0.40f, new Vec3(0.20f, 1.55f, -0.65f), blob);
            AddSphere(root.transform, "BlockedBiomatterBlobD", 0.30f, new Vec3(-0.40f, 0.20f, -0.85f), blob);
            AddLight(root.transform, "BlockedBiomatterGlow", Vec3.Zero, new Color(1.0f, 0.35f, 0.25f), 0.9f, 4.0f);
            return root;
        }

        public static GameObject CreateRampCue()
        {
            var root = BaseProp(RAMP_NAME, RAMP_KIND);
            AddBox(root.transform, "RampCueStem", new Vec3(2.4f, 0.15f, 0.7f), new Vec3(0f, 0.075f, 0f), new Color(0.95f, 0.78f, 0.30f));
            AddBox(root.transform, "RampCueHead", new Vec3(0.9f, 0.20f, 1.3f), new Vec3(1.55f, 0.10f, 0f), new Color(0.98f, 0.55f, 0.12f));
            AddMarker(root.transform, "RampCueAim", new Vec3(2.0f, 0.18f, 0f));
            return root;
        }

        public static GameObject CreateEntryBeacon()
        {
            var root = BaseProp(ENTRY_NAME, ENTRY_KIND);
            var post = new Color(0.30f, 0.65f, 1.00f);
            AddCylinder(root.transform, "EntryBeaconPost", 0.16f, 0.20f, 2.4f, new Vec3(0f, 1.2f, 0f), post);
            AddCylinder(root.transform, "EntryBeaconBase", 0.55f, 0.55f, 0.10f, new Vec3(0f, 0.05f, 0f), post);
            AddSphere(root.transform, "EntryBeaconGlowSphere", 0.38f, new Vec3(0f, 2.7f, 0f), new Color(0.65f, 0.92f, 1.00f));
            AddLight(root.transform, "EntryBeaconHalo", new Vec3(0f, 2.7f, 0f), new Color(0.40f, 0.85f, 1.00f), 1.4f, 6.5f);
            return root;
        }

        public static GameObject CreateDestinationReactorCore()
        {
            var root = BaseProp(DESTINATION_NAME, DESTINATION_KIND);
            var column = new Color(0.10f, 0.55f, 0.30f);
            AddCylinder(root.transform, "DestinationReactorColumn", 0.55f, 0.85f, 2.4f, new Vec3(0f, 1.2f, 0f), column);
            AddCylinder(root.transform, "DestinationReactorBase", 1.10f, 1.20f, 0.25f, new Vec3(0f, 0.125f, 0f), column);
            AddSphere(root.transform, "DestinationReactorGlowSphere", 0.75f, new Vec3(0f, 3.0f, 0f), new Color(0.20f, 1.00f, 0.55f));
            AddLight(root.transform, "DestinationReactorCoreGlow", new Vec3(0f, 3.0f, 0f), new Color(0.20f, 1.00f, 0.55f), 2.0f, 8.0f);
            AddMarker(root.transform, "DestinationReactorCoreFocus", new Vec3(0f, 3.0f, 0f));
            return root;
        }

        /// <summary>A route arrow from <paramref name="fromPos"/> to <paramref name="toPos"/> (Godot frame, parent-local).</summary>
        public static GameObject CreateRouteCue(long index, Vec3 fromPos, Vec3 toPos)
        {
            var root = BaseProp($"{ROUTE_PREFIX}{GdString.FormatIntPadded(index, 2)}", ROUTE_KIND);
            var meta = root.GetComponent<ReadabilityProp>();
            meta.GodotRouteFrom = fromPos;
            meta.GodotRouteTo = toPos;
            meta.routeIndex = index;

            Vec3 midpoint = (fromPos + toPos) * 0.5f;
            Vec3 span = toPos - fromPos;
            float length = span.Length();
            root.transform.localPosition = Frame.ToUnity(midpoint);

            // Local +X points along the (horizontal) direction; child meshes live in local space.
            if (length > 0.001f)
            {
                Vec3 dir = span / length;
                Vec3 basisZ = Vec3.Up.Cross(dir);
                if (basisZ.Length() > 0.001f)
                {
                    basisZ = basisZ.Normalized();
                    Vec3 basisY = dir.Cross(basisZ).Normalized();
                    root.transform.localRotation = Frame.BasisRotation(dir, basisY, basisZ);
                }
            }

            float stemLength = Mathf.Max(length, 0.5f);
            AddBox(root.transform, "RouteCueStem", new Vec3(stemLength, 0.20f, 0.30f), new Vec3(0f, 0.10f, 0f), new Color(0.85f, 0.85f, 0.20f));
            var headSize = new Vec3(0.55f, 0.55f, 0.55f);
            float headX = stemLength * 0.5f + headSize.X * 0.5f;
            AddBox(root.transform, "RouteCueHead", headSize, new Vec3(headX, 0.10f, 0f), new Color(0.95f, 0.55f, 0.10f));
            AddMarker(root.transform, "RouteCueFrom", fromPos - midpoint);
            AddMarker(root.transform, "RouteCueTo", toPos - midpoint);
            return root;
        }

        // ------------------------------------------------------------------ internals

        static GameObject BaseProp(string name, string kind)
        {
            var root = new GameObject(name) { layer = Layer };
            var meta = root.AddComponent<ReadabilityProp>();
            meta.readabilityKind = kind;
            meta.normalModeVisual = true;
            return root;
        }

        static Material Unshaded(Color c) => RuntimeVisualCatalog.Material(c, unshaded: true);

        static GameObject AddBox(Transform root, string name, Vec3 size, Vec3 pos, Color color) =>
            RuntimeVisualCatalog.AddMesh(root, name, RuntimeVisualCatalog.Cube, Unshaded(color), Frame.ToUnity(pos), Quaternion.identity,
                Frame.SizeToUnity(size), Layer);

        static GameObject AddSphere(Transform root, string name, float radius, Vec3 pos, Color color) =>
            RuntimeVisualCatalog.AddMesh(root, name, RuntimeVisualCatalog.Sphere, Unshaded(color), Frame.ToUnity(pos), Quaternion.identity,
                Vector3.one * (radius * 2f), Layer);

        static GameObject AddCylinder(Transform root, string name, float topRadius, float bottomRadius, float height, Vec3 pos, Color color) =>
            RuntimeVisualCatalog.AddMesh(root, name, RuntimeVisualCatalog.Cylinder(topRadius, bottomRadius, height), Unshaded(color),
                Frame.ToUnity(pos), Quaternion.identity, Vector3.one, Layer);

        static void AddLight(Transform root, string name, Vec3 pos, Color color, float energy, float range) =>
            RuntimeVisualCatalog.AddOmniLight(root, name, Frame.ToUnity(pos), color, energy, range, Layer);

        /// <summary>Godot <c>Marker3D</c>: an empty transform.</summary>
        static void AddMarker(Transform root, string name, Vec3 pos)
        {
            var go = new GameObject(name) { layer = Layer };
            go.transform.SetParent(root, false);
            go.transform.localPosition = Frame.ToUnity(pos);
        }

        static string ObjectiveKind(string objectiveType)
        {
            switch (objectiveType)
            {
                case "recover_supplies": return OBJECTIVE_KIND_SUPPLY;
                case "restore_systems": return OBJECTIVE_KIND_BREAKER;
                case "download_logs": return OBJECTIVE_KIND_MED;
                case "stabilize_reactor": return OBJECTIVE_KIND_REACTOR_CONSOLE;
                default: return OBJECTIVE_KIND_GENERIC;
            }
        }

        static void AddObjectiveComposition(Transform root, string kind)
        {
            Color baseColor = ObjectiveBaseColor(kind), accent = ObjectiveAccentColor(kind), glow = ObjectiveGlowColor(kind);
            switch (kind)
            {
                case OBJECTIVE_KIND_SUPPLY:
                    AddBox(root, kind + "Crate", new Vec3(1.20f, 0.90f, 1.20f), new Vec3(0f, 0.45f, 0f), baseColor);
                    AddBox(root, kind + "Lid", new Vec3(1.30f, 0.18f, 1.30f), new Vec3(0f, 0.99f, 0f), accent);
                    break;
                case OBJECTIVE_KIND_BREAKER:
                    AddBox(root, kind + "Panel", new Vec3(1.40f, 1.10f, 0.25f), new Vec3(0f, 0.75f, 0f), baseColor);
                    AddBox(root, kind + "Switch", new Vec3(0.35f, 0.35f, 0.20f), new Vec3(0f, 1.00f, 0.18f), accent);
                    AddBox(root, kind + "SwitchLever", new Vec3(0.10f, 0.45f, 0.10f), new Vec3(0f, 1.00f, 0.32f), glow);
                    break;
                case OBJECTIVE_KIND_MED:
                    AddCylinder(root, kind + "Base", 0.60f, 0.70f, 1.00f, new Vec3(0f, 0.50f, 0f), baseColor);
                    AddBox(root, kind + "Screen", new Vec3(0.95f, 0.55f, 0.10f), new Vec3(0f, 1.15f, 0.30f), accent);
                    break;
                case OBJECTIVE_KIND_REACTOR_CONSOLE:
                    AddBox(root, kind + "Base", new Vec3(1.50f, 0.60f, 1.00f), new Vec3(0f, 0.30f, 0f), baseColor);
                    AddSphere(root, kind + "Core", 0.42f, new Vec3(0f, 0.85f, 0f), glow);
                    break;
                default:
                    AddCylinder(root, kind + "Pedestal", 0.45f, 0.55f, 0.90f, new Vec3(0f, 0.45f, 0f), baseColor);
                    AddSphere(root, kind + "Orb", 0.55f, new Vec3(0f, 1.30f, 0f), accent);
                    break;
            }
            AddLight(root, kind + "Glow", Vec3.Zero, glow, 1.1f, 4.0f);
        }

        static Color ObjectiveBaseColor(string kind)
        {
            switch (kind)
            {
                case OBJECTIVE_KIND_SUPPLY: return new Color(0.85f, 0.65f, 0.22f);
                case OBJECTIVE_KIND_BREAKER: return new Color(0.22f, 0.45f, 0.85f);
                case OBJECTIVE_KIND_MED: return new Color(0.75f, 0.25f, 0.45f);
                case OBJECTIVE_KIND_REACTOR_CONSOLE: return new Color(0.20f, 0.75f, 0.45f);
                default: return new Color(0.55f, 0.55f, 0.55f);
            }
        }

        static Color ObjectiveAccentColor(string kind)
        {
            switch (kind)
            {
                case OBJECTIVE_KIND_SUPPLY: return new Color(0.95f, 0.78f, 0.30f);
                case OBJECTIVE_KIND_BREAKER: return new Color(0.30f, 0.55f, 0.95f);
                case OBJECTIVE_KIND_MED: return new Color(0.95f, 0.30f, 0.55f);
                case OBJECTIVE_KIND_REACTOR_CONSOLE: return new Color(0.25f, 0.95f, 0.55f);
                default: return new Color(0.80f, 0.80f, 0.80f);
            }
        }

        static Color ObjectiveGlowColor(string kind)
        {
            switch (kind)
            {
                case OBJECTIVE_KIND_SUPPLY: return new Color(1.00f, 0.85f, 0.30f);
                case OBJECTIVE_KIND_BREAKER: return new Color(1.00f, 0.30f, 0.20f);
                case OBJECTIVE_KIND_MED: return new Color(0.30f, 0.95f, 0.95f);
                case OBJECTIVE_KIND_REACTOR_CONSOLE: return new Color(0.35f, 1.00f, 0.65f);
                default: return new Color(0.85f, 0.85f, 0.85f);
            }
        }
    }
}
