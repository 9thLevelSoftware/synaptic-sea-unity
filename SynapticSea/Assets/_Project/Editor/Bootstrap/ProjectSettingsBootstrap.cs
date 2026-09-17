using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SynapticSea.EditorTools.Bootstrap
{
    /// <summary>
    /// One-shot, idempotent project configuration: identity, player defaults, physics layers
    /// and the layer collision matrix defined in docs/unity-port-plan.md (Phase 0, step 6).
    ///   unity run SynapticSea -- -executeMethod SynapticSea.EditorTools.Bootstrap.ProjectSettingsBootstrap.Apply
    /// </summary>
    public static class ProjectSettingsBootstrap
    {
        public const int Player = 6;
        public const int Structure = 7;
        public const int ZoneBlocker = 8;
        public const int Sensor = 9;
        public const int Threat = 10;
        public const int Prop = 11;
        public const int Portal = 12;
        public const int Ceiling = 13;
        public const int Hallucination = 14;
        public const int Walkable = 15;

        static readonly Dictionary<int, string> Layers = new()
        {
            { Player, "Player" },
            { Structure, "Structure" },
            { ZoneBlocker, "ZoneBlocker" },
            { Sensor, "Sensor" },
            { Threat, "Threat" },
            { Prop, "Prop" },
            { Portal, "Portal" },
            { Ceiling, "Ceiling" },
            { Hallucination, "Hallucination" },
            { Walkable, "Walkable" },
        };

        // Unordered pairs of project layers that collide. Every other pair touching a project layer is ignored.
        static readonly (int a, int b)[] CollidingPairs =
        {
            (Player, Structure), (Player, ZoneBlocker), (Player, Portal), (Player, Threat),
            (Sensor, Player),
            (Threat, Structure), (Threat, ZoneBlocker), (Threat, Portal),
            (Player, Walkable), (Threat, Walkable),
            (0, Player), (0, Structure), (0, ZoneBlocker), (0, Threat), (0, Portal),
        };

        /// <summary>
        /// The NavMesh agent the threats path as. The player's own CharacterController is radius 0.35 / height 1.6,
        /// and a ship's doorways leave a 1.2 m gap between their jambs, which Unity's built-in Humanoid agent
        /// (radius 0.5) erodes shut, sealing every room off. Climb 0.3 matches the controller's step offset.
        /// </summary>
        /// <summary>
        /// The NavMesh area a burning room's floor is marked with, at the cost the nav graph charges for fire
        /// (<c>ShipNavGraph.FIRE_COST_MULT</c>), so a threat routes around a fire exactly as it did in Godot.
        /// </summary>
        public const string FireAreaName = "ThreatFire";
        public const int FireAreaIndex = 8;
        public const float FireAreaCost = 6f;

        public const string ThreatAgentType = "Threat";
        public const int ThreatAgentTypeId = 1734437751;
        public const float ThreatAgentRadius = 0.35f;
        public const float ThreatAgentHeight = 1.8f;
        public const float ThreatAgentClimb = 0.3f;
        public const float ThreatAgentSlope = 45f;

        [MenuItem("Synaptic Sea/Bootstrap/Apply Project Settings")]
        public static void Apply()
        {
            ApplyIdentity();
            ApplyLayers();
            ApplyCollisionMatrix();
            ApplyNavAgentTypes();
            AssetDatabase.SaveAssets();
            Debug.Log("[ProjectSettingsBootstrap] Applied identity, layers, collision matrix and NavMesh agent types.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static void ApplyIdentity()
        {
            PlayerSettings.companyName = "9th Level Software";
            PlayerSettings.productName = "The Synaptic Sea";
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Standalone, "com.9thlevelsoftware.the-synaptic-sea");
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetApiCompatibilityLevel(UnityEditor.Build.NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
            EditorSettings.serializationMode = SerializationMode.ForceText;
        }

        static void ApplyLayers()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset"));
            var layers = tagManager.FindProperty("layers");
            foreach (var kv in Layers)
            {
                var slot = layers.GetArrayElementAtIndex(kv.Key);
                if (!string.IsNullOrEmpty(slot.stringValue) && slot.stringValue != kv.Value)
                    Debug.LogWarning($"[ProjectSettingsBootstrap] Layer {kv.Key} was '{slot.stringValue}', overwriting with '{kv.Value}'.");
                slot.stringValue = kv.Value;
            }
            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }

        static void ApplyCollisionMatrix()
        {
            var collides = new HashSet<(int, int)>();
            foreach (var (a, b) in CollidingPairs)
            {
                collides.Add((a, b));
                collides.Add((b, a));
            }

            for (int i = 0; i < 32; i++)
            {
                for (int j = i; j < 32; j++)
                {
                    bool iProject = Layers.ContainsKey(i);
                    bool jProject = Layers.ContainsKey(j);
                    bool enabled = !iProject && !jProject || collides.Contains((i, j));
                    Physics.IgnoreLayerCollision(i, j, !enabled);
                }
            }

            // Line-of-sight and interaction raycasts must never hit trigger sensors.
            Physics.queriesHitTriggers = false;

            // Physics.* setters write to the DynamicsManager asset; mark it dirty so SaveAssets persists it.
            var dynamics = AssetDatabase.LoadMainAssetAtPath("ProjectSettings/DynamicsManager.asset");
            if (dynamics != null) EditorUtility.SetDirty(dynamics);
        }

        /// <summary>
        /// Registers the <see cref="ThreatAgentType"/> agent in ProjectSettings/NavMeshAreas.asset (the Navigation
        /// window's Agents tab). The id is fixed so a re-run, and every scene or surface that stored it, keeps
        /// pointing at the same agent.
        /// </summary>
        static void ApplyNavAgentTypes()
        {
            var asset = AssetDatabase.LoadMainAssetAtPath("ProjectSettings/NavMeshAreas.asset");
            if (asset == null)
            {
                Debug.LogWarning("[ProjectSettingsBootstrap] ProjectSettings/NavMeshAreas.asset is missing; no agent type registered.");
                return;
            }
            var so = new SerializedObject(asset);
            SerializedProperty areas = so.FindProperty("areas");
            if (areas != null && areas.arraySize > FireAreaIndex)
            {
                SerializedProperty area = areas.GetArrayElementAtIndex(FireAreaIndex);
                area.FindPropertyRelative("name").stringValue = FireAreaName;
                area.FindPropertyRelative("cost").floatValue = FireAreaCost;
            }
            SerializedProperty settings = so.FindProperty("m_Settings");
            SerializedProperty names = so.FindProperty("m_SettingNames");
            int index = -1;
            for (int i = 0; i < names.arraySize; i++)
                if (names.GetArrayElementAtIndex(i).stringValue == ThreatAgentType) index = i;
            if (index < 0)
            {
                index = settings.arraySize;
                settings.InsertArrayElementAtIndex(index);
                names.InsertArrayElementAtIndex(index);
            }
            names.GetArrayElementAtIndex(index).stringValue = ThreatAgentType;
            SerializedProperty s = settings.GetArrayElementAtIndex(index);
            s.FindPropertyRelative("agentTypeID").intValue = ThreatAgentTypeId;
            s.FindPropertyRelative("agentRadius").floatValue = ThreatAgentRadius;
            s.FindPropertyRelative("agentHeight").floatValue = ThreatAgentHeight;
            s.FindPropertyRelative("agentClimb").floatValue = ThreatAgentClimb;
            s.FindPropertyRelative("agentSlope").floatValue = ThreatAgentSlope;
            s.FindPropertyRelative("minRegionArea").floatValue = 2f;
            s.FindPropertyRelative("manualCellSize").intValue = 0;
            s.FindPropertyRelative("cellSize").floatValue = ThreatAgentRadius / 3f;
            s.FindPropertyRelative("manualTileSize").intValue = 0;
            s.FindPropertyRelative("tileSize").intValue = 256;
            s.FindPropertyRelative("ledgeDropHeight").floatValue = 0f;
            s.FindPropertyRelative("maxJumpAcrossDistance").floatValue = 0f;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }
    }
}
