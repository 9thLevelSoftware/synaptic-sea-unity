using System;
using System.Collections.Generic;
using CritterCrafter;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using SynapticSea.Runtime.Session;
using UnityEngine;
using UnityEditor.Animations;
using Object = UnityEngine.Object;

namespace SynapticSea.Tests
{
    public class ThreatCreatureFactoryTests
    {
        readonly List<Object> _objects = new List<Object>();
        T Keep<T>(T value) where T : Object { _objects.Add(value); return value; }
        [TearDown] public void Cleanup()
        { foreach (var o in _objects) if (o != null) Object.DestroyImmediate(o); _objects.Clear(); CreatureAssembler.ClearCache(); }
        ThreatAIState Threat(string archetype = "stalker") => new ThreatAIState { InstanceId = "ship17:marker9", ArchetypeId = archetype };
        GameObject Build(ThreatAIState t, CritterLibrary lib = null) => Keep(new ThreatCreatureFactory(lib).Build(t, null, PhysicsLayers.Threat));
        void Placeholder(GameObject node)
        {
            Assert.That(node.transform.Find("Mesh"), Is.Not.Null);
            Assert.That(node.GetComponent<ThreatCreatureVisualStatus>().IsCreature, Is.False);
            Assert.That(node.GetComponentInChildren<Collider>().isTrigger, Is.True);
        }

        [Test] public void MissingLibraryAndMechanicalDroneRemainPlayable()
        {
            var organic = Threat(); Placeholder(Build(organic));
            Assert.That(organic.CreatureVisual.Count, Is.GreaterThan(0));
            var drone = Threat("drone_swarm"); Placeholder(Build(drone, Library()));
            Assert.That(drone.CreatureVisual.Count, Is.Zero);
            Assert.That(ThreatCreatureFactory.PoolFor("hull_tendril"), Is.Empty);
        }
        [Test] public void EmptyProductionPoolDoesNotApproveOrRerollOnRestore()
        {
            var library = Library(approved: false); var threat = Threat();
            var first = Build(threat, library); Placeholder(first);
            StringAssert.Contains("CC_GEN_NO_SKELETON", first.GetComponent<ThreatCreatureVisualStatus>().Reason);
            Assert.That(library.Catalog.skeletons[0].status, Is.EqualTo("draft"));
            var restored = Restore(threat); Placeholder(Build(restored, library));
            Assert.That(restored.CreatureVisual["seed"], Is.EqualTo(threat.CreatureVisual["seed"]));
        }
        [TestCase("")][TestCase("{oops")][TestCase("{}")] public void MissingOrMalformedSavedRecipeNeverRegenerates(string json)
        {
            var t = Threat(); t.CreatureVisual = new GdDict { { "pool_id", "stalker" },
                { "seed", ThreatCreatureFactory.SeedFor(t.InstanceId) }, { "recipe_json", json } };
            Placeholder(Build(t, Library()));
            Assert.That(t.CreatureVisual["recipe_json"], Is.EqualTo(json));
        }
        [Test] public void MissingPartFallsBackButRetainsGeneratedRecipe()
        {
            var t = Threat(); var node = Build(t, Library(missingPart: true)); Placeholder(node);
            StringAssert.Contains("Missing part model", node.GetComponent<ThreatCreatureVisualStatus>().Reason);
            Assert.That(V.Str(t.CreatureVisual["recipe_json"]), Is.Not.Empty);
        }
        [Test] public void SavedRecipeCannotBypassOwnerApprovalOrChangePoolIdentity()
        {
            var t = Threat(); Build(t, Library());
            var restored = Restore(t); var denied = Build(restored, Library(approved: false)); Placeholder(denied);
            StringAssert.Contains("CC_SKELETON_NOT_APPROVED", denied.GetComponent<ThreatCreatureVisualStatus>().Reason);
            string recipe = V.Str(restored.CreatureVisual["recipe_json"]);
            restored.CreatureVisual["seed"] = "9";
            var mismatched = Build(restored, Library()); Placeholder(mismatched);
            StringAssert.Contains("identity mismatch", mismatched.GetComponent<ThreatCreatureVisualStatus>().Reason);
            Assert.That(restored.CreatureVisual["recipe_json"], Is.EqualTo(recipe));
        }
        [Test] public void ProductionBuildUsesPublishedCreatureSpeedButRetainsGameMultipliers()
        {
            var lib = Library(); lib.Catalog.skeletons[0].locomotion.v_run_mps = 1.8;
            var t = Threat(); t.HuntSpeedMult = 1.2;
            var node = Build(t, lib);
            Assert.That(node.GetComponent<ThreatCreatureVisualStatus>().IsCreature, Is.True);
            Assert.That(t.MoveSpeed, Is.EqualTo(1.8)); Assert.That(t.HuntSpeedMult, Is.EqualTo(1.2));
            Assert.That(Restore(t).MoveSpeed, Is.EqualTo(1.8));
        }
        [Test] public void SyntheticApprovedFixtureAssemblesAndRestoresExactRecipeWithoutChangingSpeed()
        {
            var lib = Library(); var t = Threat(); t.MoveSpeed = 2.75; t.HuntSpeedMult = 1.2;
            var node = Build(t, lib);
            Assert.That(node.GetComponent<ThreatCreatureVisualStatus>().Reason, Is.EqualTo("Validated production recipe"),
                node.GetComponent<ThreatCreatureVisualStatus>().Details);
            Assert.That(node.GetComponent<ThreatCreatureVisualStatus>().IsCreature, Is.True);
            Assert.That(node.name, Is.EqualTo("Threat_stalker"));
            Assert.That(node.transform.Find("Mesh").GetComponentInChildren<SkinnedMeshRenderer>(), Is.Not.Null);
            Assert.That(node.GetComponentInChildren<AssembledCreature>().Triangles, Is.EqualTo(1));
            Assert.That(t.MoveSpeed, Is.EqualTo(2.75)); Assert.That(t.HuntSpeedMult, Is.EqualTo(1.2));
            var restored = Restore(t); var loaded = Build(restored, lib);
            Assert.That(loaded.GetComponent<ThreatCreatureVisualStatus>().IsCreature, Is.True);
            Assert.That(restored.CreatureVisual["recipe_json"], Is.EqualTo(t.CreatureVisual["recipe_json"]));
            Assert.That(restored.CreatureVisual["seed"], Is.EqualTo(t.CreatureVisual["seed"]));
            foreach (var c in loaded.transform.Find("Mesh").GetComponentsInChildren<Collider>()) Assert.That(c.enabled, Is.False);
            Assert.That(loaded.GetComponentInChildren<Animator>().applyRootMotion, Is.False);
        }
        static ThreatAIState Restore(ThreatAIState t)
        {
            var runtime = new ThreatRuntime(); runtime.Threats.Add(t);
            var loaded = new ThreatRuntime();
            Assert.That(loaded.ApplySummary((GdDict)GdJson.ParseString(GdJson.Stringify(runtime.GetSummary()))), Is.True);
            return loaded.Threats[0];
        }

        CritterLibrary Library(bool approved = true, bool missingPart = false)
        {
            // Synthetic test geometry only; no owner draft asset or approval record is modified.
            var bone = new BoneData { name = "root", parent = "", head_m = new[] { 0.0, 0.0, 0.0 },
                tail_m = new[] { 0.0, 1.0, 0.0 }, up_m = new[] { 0.0, 0.0, 1.0 } };
            var branch = new BranchData { branch_id = "body", template = "body", binding_profile_id = "test",
                binding_profile_version = "1", binding_profile_hash = "test", parent_branch = "", attach_bone = "root",
                bone_names = new[] { "root" }, length_mm = 1000, girth_mm = 200, length_m = 1, girth_m = .2,
                side = "symmetric", required = true, optional_fill_pct = 100, connector_size_class = "",
                accepts = new Accepts { categories = new[] { "body" }, templates = Array.Empty<string>(), tags_any = Array.Empty<string>() },
                snap = new SnapData { position_m = new[] { 0.0, 0.0, 0.0 }, rotation_xyzw = new[] { 0.0, 0.0, 0.0, 1.0 } } };
            var skeleton = new SkeletonData { skeleton_id = "synthetic", family = "crawler", status = approved ? "approved" : "draft",
                symmetry_pct = 100, bones = new[] { bone }, branches = new[] { branch },
                asset = new SkeletonAssetInfo { clips = Array.Empty<ClipInfo>() }, neutral_pose = new NeutralPoseData
                { root_offset_m = new[] { 0.0, 0.0, 0.0 }, rotations = new[] { new NeutralRotationData
                    { bone_name = "root", rotation_xyzw = new[] { 0.0, 0.0, 0.0, 1.0 } } } } };
            var part = new PartData { part_id = "synthetic_body", category = "body", template = "body",
                binding_profile_id = "test", binding_profile_version = "1", binding_profile_hash = "test", side = "symmetric",
                inventory_kind = "production", status = "approved", species_tags = Array.Empty<string>(),
                dimensions_m = new[] { 1.0, .2, .2 },
                length_mm = 1000, girth_mm = 200, length_m = 1, girth_m = .2, max_triangles = 1, max_material_slots = 1 };
            var catalog = new CatalogData { schema_version = "3.0.0", document_kind = "critter_library", library_id = "synthetic_test",
                version = "0.2.0", generator = new GeneratorInfo { algorithm = "cc-gen-3", rng = "splitmix64" },
                limits = new CatalogLimits { max_triangles = 30000, max_bones = 120, max_parts = 16, max_influences = 4 },
                gait_profiles = Array.Empty<GaitProfile>(), binding_profiles = Array.Empty<BindingProfile>(), branch_templates = Array.Empty<BranchTemplate>(),
                skeletons = new[] { skeleton }, parts = new[] { part }, pools = new[] { new PoolData
                { pool_id = "stalker", families = new[] { "crawler" }, skeleton_ids = Array.Empty<string>() } } };
            var model = Keep(new GameObject("TestSkeleton")); new GameObject("root").transform.SetParent(model.transform);
            var controller = Keep(new AnimatorController());
            controller.AddLayer("Base Layer");
            controller.AddParameter("PlaybackRate", AnimatorControllerParameterType.Float);
            var partModel = Keep(new GameObject("TestPart")); var b0 = new GameObject("b0"); b0.transform.SetParent(partModel.transform);
            var renderer = partModel.AddComponent<SkinnedMeshRenderer>();
            var mesh = Keep(new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 },
                boneWeights = new[] { Weight(), Weight(), Weight() }, bindposes = new[] { Matrix4x4.identity } }); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            renderer.sharedMesh = mesh; renderer.bones = new[] { b0.transform }; renderer.rootBone = b0.transform;
            var proxy = new GameObject("BindProxy"); proxy.transform.SetParent(model.transform, false);
            var bindProxy = proxy.AddComponent<SkinnedMeshRenderer>(); bindProxy.sharedMesh = mesh;
            bindProxy.bones = new[] { model.transform.Find("root") }; bindProxy.rootBone = bindProxy.bones[0];
            partModel.AddComponent<BoxCollider>();
            var mat = Keep(new Material(Shader.Find("Universal Render Pipeline/Lit")));
            var lib = Keep(ScriptableObject.CreateInstance<CritterLibrary>());
            lib.EditorSetContents(Keep(new TextAsset(JsonUtility.ToJson(catalog))), new[] { new CritterLibrary.SkeletonEntry
                { skeletonId = "synthetic", model = model, controller = controller } }, new[] { new CritterLibrary.PartEntry
                { partId = "synthetic_body", model = missingPart ? null : partModel, material = mat } });
            return lib;
        }
        static BoneWeight Weight() => new BoneWeight { boneIndex0 = 0, weight0 = 1 };
    }
}
