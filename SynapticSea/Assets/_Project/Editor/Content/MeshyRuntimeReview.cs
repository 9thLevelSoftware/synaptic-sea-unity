// Ported from tools/meshy_runtime_review.py and scripts/validation/meshy_asset_review_capture.gd (Godot repository) @ 96ecb2b0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Rng;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;
using SynapticSea.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static SynapticSea.EditorTools.Content.MeshyReviewEvidence;

namespace SynapticSea.EditorTools.Content
{
    /// <summary>
    /// Governed, no-promotion locked-isometric review of one Meshy candidate, rendered by Unity instead of a
    /// disposable Godot project overlay. For each seed (42, 777) and lighting mode (normal, emergency, dark) it:
    /// <list type="number">
    /// <item>generates the Godot capture's derelict (breach_field / standard, MEDIUM, WRECKED, 5–10 rooms, derelict
    /// archetype without guaranteed roles) and builds it with <see cref="ShipSceneBuilder"/>;</item>
    /// <item>lights it with the Godot review harness (key directional, blue fill omni, flat ambient, dark background);</item>
    /// <item>mounts the candidate (imported from a disposable <c>Assets/_MeshyReview</c> copy of <c>cleaned.glb</c>) 0.9 m
    /// above the occupied cell with the largest x+z, hides structural edges within 3 m on the camera side, and fits a
    /// locked (16, 14, 16) orthographic camera to it;</item>
    /// <item>renders staged (asset alone on transparent), reference (scene without the asset) and final at 1600×900 and
    /// applies the staged, contextual and non-blank pixel gates from the Godot tool.</item>
    /// </list>
    /// Only when all six captures pass are the 18 PNGs and <c>runtime-review.json</c> (report last) published to
    /// <c>artifacts/validation-previews/meshy/&lt;asset_id&gt;/</c>. Nothing under Assets changes. Evidence binding stays in
    /// Python: <c>python3 tools/python/meshy_candidate_review.py bind --project-root . --task-dir &lt;task&gt;</c> (POSIX).
    ///   Unity.exe -batchmode -projectPath SynapticSea -executeMethod SynapticSea.EditorTools.Content.MeshyRuntimeReview.Run
    ///     -taskDir artifacts/_staging/meshy/&lt;asset_id&gt;/&lt;task_id&gt; -contract tools/asset_generation/contracts/&lt;id&gt;.json [-dryRun]
    /// Needs a GPU: run batch mode WITHOUT -nographics.
    /// Differences from Godot: ceilings are hidden everywhere (interior convention, port-status decision 17) instead of
    /// within 2 m of the mount; the mount is a plain transform for every category (the Godot threat/prop seams hid their
    /// own visuals anyway); errors logged during the run fail it, warnings do not; the Unity pixel thresholds are
    /// Godot's and have not been calibrated against a real Meshy candidate.
    /// </summary>
    public static class MeshyRuntimeReview
    {
        public const string OverlayAssetRoot = "Assets/_MeshyReview";
        static readonly Color BackgroundColor = new Color(0.004f, 0.010f, 0.035f, 1f);

        /// <summary>Godot harness lighting per mode: key colour/energy, fill colour/energy, ambient colour/energy.</summary>
        static readonly Dictionary<string, (Color key, float keyEnergy, Color fill, float fillEnergy, Color ambient, float ambientEnergy)> Lighting =
            new Dictionary<string, (Color, float, Color, float, Color, float)>
            {
                { "normal", (new Color(0.72f, 0.84f, 1.0f), 1.5f, new Color(0.16f, 0.36f, 0.90f), 4.0f, new Color(0.12f, 0.20f, 0.40f), 0.72f) },
                { "emergency", (new Color(1.0f, 0.22f, 0.10f), 0.65f, new Color(0.95f, 0.04f, 0.015f), 5.0f, new Color(0.22f, 0.035f, 0.02f), 0.38f) },
                { "dark", (new Color(0.18f, 0.28f, 0.55f), 0.18f, new Color(0.04f, 0.18f, 0.45f), 1.1f, new Color(0.025f, 0.045f, 0.10f), 0.16f) },
            };

        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        [MenuItem("Synaptic Sea/Content/Meshy Runtime Review…")]
        public static void RunMenu()
        {
            string task = EditorUtility.OpenFolderPanel("Meshy task directory (artifacts/_staging/meshy/<asset_id>/<task_id>)", Path.Combine(RepoRoot, StagingRelative), "");
            if (string.IsNullOrEmpty(task)) return;
            string contract = EditorUtility.OpenFilePanel("Asset contract", Path.Combine(RepoRoot, "tools", "asset_generation", "contracts"), "json");
            if (string.IsNullOrEmpty(contract)) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            int choice = EditorUtility.DisplayDialogComplex("Meshy Runtime Review", "Render six captures and publish the review, or only check the task inputs?", "Review", "Cancel", "Dry run");
            if (choice == 1) return;
            var setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                bool ok = Review(contract, task, choice == 2, out string reason);
                EditorUtility.DisplayDialog("Meshy Runtime Review", (ok ? "PASS: " : "FAIL: ") + reason, "OK");
            }
            finally
            {
                if (setup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
        }

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            string task = Arg("-taskDir"), contract = Arg("-contract");
            int code;
            if (task == null || contract == null)
            {
                Debug.LogError("[MeshyRuntimeReview] usage: -taskDir <artifacts/_staging/meshy/<asset>/<task>> -contract <contract.json> [-dryRun]");
                code = 2;
            }
            else
            {
                code = Review(Rooted(contract), Rooted(task), args.Contains("-dryRun"), out _) ? 0 : 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static string Rooted(string path) => Path.IsPathRooted(path) ? path : Path.Combine(RepoRoot, path);

        public static bool Review(string contractPath, string taskDir, bool dryRun, out string reason)
        {
            var errors = new List<string>();
            void OnLog(string message, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors.Add(message);
            }
            try
            {
                ReviewInputs inputs = LoadTaskInputs(RepoRoot, contractPath, taskDir);
                string previewDir = FixedPreviewPath(RepoRoot, inputs.AssetId);
                if (dryRun)
                {
                    reason = "dry-run validation passed; no outputs written";
                    Debug.Log($"[MeshyRuntimeReview] MESHY RUNTIME REVIEW DRY-RUN PASS asset={inputs.AssetId} task_id={inputs.TaskId}");
                    return true;
                }

                Application.logMessageReceived += OnLog;
                var captures = new List<CaptureRecord>();
                var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                GameObject model = ImportOverlay(inputs);
                try
                {
                    foreach (int seed in Seeds)
                        foreach (string lighting in LightingModes)
                        {
                            var (record, finalPng, stagedPng, referencePng) = Capture(model, inputs, seed, lighting);
                            if (errors.Count > 0) throw new ReviewException($"capture seed={seed} lighting={lighting} logged an error: {errors[0]}");
                            captures.Add(record);
                            payloads[CaptureName(seed, lighting)] = finalPng;
                            payloads[AuxiliaryCaptureName(seed, lighting, "staged")] = stagedPng;
                            payloads[AuxiliaryCaptureName(seed, lighting, "reference")] = referencePng;
                            Debug.Log($"[MeshyRuntimeReview] MESHY RUNTIME CAPTURE PASS seed={seed} lighting={lighting} camera_size={record.Camera.Size:0.000000000} " +
                                      $"staged_opaque_pixels={record.Staged.OpaquePixels} staged_luma_range={record.Staged.LumaRange:0.000000000} " +
                                      $"contextual_reference_pixels={record.Contextual.ReferencePixels} contextual_changed_pixels={record.Contextual.ChangedPixels} " +
                                      $"contextual_max_delta={record.Contextual.MaxDelta:0.000000000}");
                        }
                }
                finally
                {
                    DeleteOverlay();
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }

                var document = BuildDocument(inputs, captures);
                Publish(previewDir, payloads, CanonicalJsonBytes(document));
                reason = "runtime review passed; evidence published to " + previewDir;
                Debug.Log($"[MeshyRuntimeReview] MESHY RUNTIME REVIEW PASS asset={inputs.AssetId} task_id={inputs.TaskId} seeds=42,777 " +
                          $"lighting=normal,emergency,dark captures=6 out={previewDir}");
                return true;
            }
            catch (Exception e) when (e is ReviewException || e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
            {
                reason = e.Message;
                Debug.LogError("[MeshyRuntimeReview] meshy_runtime_review: " + e.Message);
                return false;
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }
        }

        // ------------------------------------------------------------------ overlay

        static string ProjectPath(string assetPath) => Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), assetPath.Replace('/', Path.DirectorySeparatorChar));

        static GameObject ImportOverlay(ReviewInputs inputs)
        {
            string assetPath = $"{OverlayAssetRoot}/{inputs.AssetId}/cleaned.glb";
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectPath(assetPath)));
            File.Copy(inputs.CleanedGlbPath, ProjectPath(assetPath), true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (model == null) throw new ReviewException("could not load staged GLB: " + assetPath);
            if (model.GetComponentsInChildren<Renderer>(true).Length == 0) throw new ReviewException("staged visual has no mesh geometry");
            return model;
        }

        static void DeleteOverlay()
        {
            AssetDatabase.DeleteAsset(OverlayAssetRoot);
            string full = ProjectPath(OverlayAssetRoot);
            if (Directory.Exists(full)) Directory.Delete(full, true);
            if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
        }

        // ------------------------------------------------------------------ capture

        static (CaptureRecord record, byte[] finalPng, byte[] stagedPng, byte[] referencePng) Capture(GameObject model, ReviewInputs inputs, int seed, string lighting)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CoreServices.Resources = new FileSystemResourceReader(Application.streamingAssetsPath);
            CatalogRegistry.Clear();
            GodotGlobalRandom.Seed(seed);

            ShipView ship = LoadDerelict(seed);
            var mode = Lighting[lighting];
            var (key, fill) = CreateHarnessLights(mode.key, mode.keyEnergy, mode.fill, mode.fillEnergy);

            Vec3 mountGodot = MountPosition(ship.Layout.LayoutDoc);
            var mount = new GameObject(MountName(inputs)).transform;
            mount.position = Frame.ToUnity(mountGodot);
            var visual = (GameObject)PrefabUtility.InstantiatePrefab(model, mount);
            visual.transform.localPosition = Vector3.zero;
            ApplyLocalCutaway(ship, mountGodot);

            Bounds bounds = VisualBounds(visual);
            var cameraGo = new GameObject("ReviewCamera");
            var camera = cameraGo.AddComponent<Camera>();
            CameraTransform godotCamera = FitCamera(camera, bounds);

            // Staged: the asset alone, no environment, transparent background.
            ship.gameObject.SetActive(false);
            AtmosphereApplier.SetFlatAmbient(Color.black, 0f);
            byte[] staged = Render(camera, new Color(0f, 0f, 0f, 0f), out byte[] stagedPng);
            // Reference and final: the harness environment, without and with the asset.
            ship.gameObject.SetActive(true);
            AtmosphereApplier.SetFlatAmbient(mode.ambient, mode.ambientEnergy);
            RenderSettings.fog = false;
            mount.gameObject.SetActive(false);
            byte[] reference = Render(camera, BackgroundColor, out byte[] referencePng);
            mount.gameObject.SetActive(true);
            byte[] final = Render(camera, BackgroundColor, out byte[] finalPng);

            var stagedSamples = GovernedSamples(staged);
            StagedVisibility stagedEvidence = Staged(stagedSamples);
            if (!stagedEvidence.Pass) throw new ReviewException($"staged visual visibility evidence failed seed={seed} lighting={lighting}");
            ContextualVisibility contextual = Contextual(stagedSamples, GovernedSamples(reference), GovernedSamples(final));
            if (!contextual.Pass) throw new ReviewException($"contextual visual visibility evidence failed seed={seed} lighting={lighting}");
            if (!IsVisible(final)) throw new ReviewException($"viewport capture was blank or near-uniform seed={seed} lighting={lighting}");
            ValidateStaged(stagedEvidence);
            ValidateContextual(contextual);
            ValidateCamera(godotCamera, inputs.BoundsDimensions);
            UnityEngine.Object.DestroyImmediate(key.gameObject);
            UnityEngine.Object.DestroyImmediate(fill.gameObject);

            var record = new CaptureRecord
            {
                Seed = seed,
                Lighting = lighting,
                Camera = godotCamera,
                Staged = stagedEvidence,
                Contextual = contextual,
                OutputSha256 = PromoteStructuralSource.Sha256Hex(finalPng),
                StagedSha256 = PromoteStructuralSource.Sha256Hex(stagedPng),
                ReferenceSha256 = PromoteStructuralSource.Sha256Hex(referencePng),
            };
            return (record, finalPng, stagedPng, referencePng);
        }

        static string MountName(ReviewInputs inputs)
        {
            string category = inputs.Category ?? "";
            if (category.StartsWith("threat", StringComparison.Ordinal)) return "StagedThreat_" + inputs.AssetId;
            if (category == "gameplay_prop" || category.StartsWith("prop", StringComparison.Ordinal)) return "StagedVisualOnlyProp_" + inputs.AssetId;
            return "StagedVisualOnlyAsset_" + inputs.AssetId;
        }

        /// <summary>The Godot capture's <c>_load_derelict</c>.</summary>
        static ShipView LoadDerelict(int seed)
        {
            GdDict archetype = CatalogRegistry.LoadDict("res://data/procgen/archetypes/derelict.json") ?? new GdDict
            {
                { "name", "Derelict" },
                { "type", "derelict" },
                { "role_weights", new GdDict { { "cargo", 4L }, { "corridor", 3L }, { "bridge", 3L }, { "dock", 1L } } },
                { "guaranteed_roles", new GdArray() },
                { "max_duplicates", 3L },
            };
            archetype = archetype.DeepCopy();
            archetype["guaranteed_roles"] = new GdArray();
            var blueprint = new ShipBlueprint(ShipBlueprint.Size.Medium, ShipBlueprint.Condition.Wrecked, seed) { RoomCountRange = new Vec2i(5, 10) };
            var generator = new ShipGenerator();
            generator.ConfigureRunContext("breach_field", "standard");
            ShipDocuments documents = generator.Generate(blueprint, archetype);
            if (documents == null) throw new ReviewException("ShipGenerator returned null for breach_field");
            var builder = ShipSceneBuilder.Create(null, "GeneratedDerelict");
            if (!builder.LoadFromDocuments(documents.Layout, documents.Kit, documents.GameplaySlice ?? new GdDict(), false))
                throw new ReviewException("derelict scene build failed");
            // The review harness replaces the biome atmosphere lights.
            foreach (var l in builder.View.GetComponentsInChildren<Light>(true))
                if (l.type == LightType.Directional || l.name == AtmosphereApplier.AccentLightName) UnityEngine.Object.DestroyImmediate(l.gameObject);
            return builder.View;
        }

        /// <summary>The harness key light (Godot basis, faces −Z) and fill omni, energies through the calibrated scales.</summary>
        static (Light key, Light fill) CreateHarnessLights(Color keyColor, float keyEnergy, Color fillColor, float fillEnergy)
        {
            // Godot basis columns: y = (-0.25, 0.866025, -0.433013), z = (0.433013, 0.5, 0.75); the light shines along −z.
            Vector3 forward = Frame.ToUnity(new Vec3(-0.433013f, -0.5f, -0.75f));
            Vector3 up = Frame.ToUnity(new Vec3(-0.25f, 0.866025f, -0.433013f));
            var key = new GameObject("ReviewKeyLight").AddComponent<Light>();
            key.type = LightType.Directional;
            key.transform.rotation = Quaternion.LookRotation(forward, up);
            key.color = keyColor;
            key.intensity = keyEnergy * AtmosphereApplier.DirectionalEnergyScale;
            key.shadows = LightShadows.Soft;
            var fill = new GameObject("ReviewFillLight").AddComponent<Light>();
            fill.type = LightType.Point;
            fill.transform.position = Frame.ToUnity(new Vec3(-5f, 9f, 5f));
            fill.color = fillColor;
            fill.intensity = fillEnergy * AtmosphereApplier.OmniEnergyScale;
            fill.range = 34f;
            fill.shadows = LightShadows.None;
            return (key, fill);
        }

        /// <summary><c>_mount_position</c>: the occupied cell with the largest x+z (ties: larger x, z, y), plus 0.9 m.</summary>
        public static Vec3 MountPosition(GdDict layout)
        {
            GdDict occupancy = layout?.GetDictOrEmpty("structural_plan").GetDictOrEmpty("occupancy") ?? new GdDict();
            bool found = false;
            Vec3 best = default;
            double bestScore = double.NegativeInfinity;
            foreach (object record in occupancy.Values)
            {
                if (!(record is GdDict r) || !TryReadPosition(r.Get("position"), out Vec3 p)) continue;
                double score = (double)p.X + p.Z;
                if (!found || score > bestScore || (score == bestScore && Precedes(p, best)))
                {
                    best = p;
                    bestScore = score;
                    found = true;
                }
            }
            if (!found) throw new ReviewException("no valid occupied cell exists");
            return new Vec3(best.X, best.Y + 0.9f, best.Z);
        }

        static bool Precedes(Vec3 candidate, Vec3 current)
        {
            if (!Mathf.Approximately(candidate.X, current.X)) return candidate.X > current.X;
            if (!Mathf.Approximately(candidate.Z, current.Z)) return candidate.Z > current.Z;
            return candidate.Y > current.Y;
        }

        static bool TryReadPosition(object raw, out Vec3 position)
        {
            position = default;
            double[] values = null;
            if (raw is GdArray a && a.Count == 3 && a.All(V.IsNumber)) values = a.Select(v => V.F64(v)).ToArray();
            else if (raw is string s)
            {
                string text = s.Trim();
                if (text.StartsWith("Vector3(", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal)) text = text.Substring(8, text.Length - 9);
                else if (text.StartsWith("(", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal)) text = text.Substring(1, text.Length - 2);
                else return false;
                string[] parts = text.Split(',');
                if (parts.Length != 3) return false;
                values = new double[3];
                for (int i = 0; i < 3; i++)
                    if (!double.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[i])) return false;
            }
            if (values == null || values.Any(v => double.IsNaN(v) || double.IsInfinity(v))) return false;
            position = new Vec3((float)values[0], (float)values[1], (float)values[2]);
            return true;
        }

        /// <summary>
        /// <c>_apply_local_cutaway</c>: hides structural edges within 3 m of the mount on the camera's (+x, +z) Godot side.
        /// Ceilings are culled by the camera (interior convention) instead of only near the mount.
        /// </summary>
        static void ApplyLocalCutaway(ShipView ship, Vec3 mountGodot)
        {
            var diagonal = new Vector2(1f, 1f).normalized;
            foreach (Transform t in ship.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("StructuralEdge_", StringComparison.Ordinal)) continue;
                Vec3 p = Frame.ToGodot(t.position);
                var horizontal = new Vector2(p.X - mountGodot.X, p.Z - mountGodot.Z);
                if (horizontal.magnitude <= 3f && Vector2.Dot(horizontal, diagonal) > 0f) t.gameObject.SetActive(false);
            }
        }

        static Bounds VisualBounds(GameObject visual)
        {
            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new ReviewException("staged visual has no mesh geometry");
            Bounds b = renderers[0].bounds;
            foreach (var r in renderers.Skip(1)) b.Encapsulate(r.bounds);
            if (b.size.magnitude <= 0.0001f) throw new ReviewException("staged visual bounds are empty");
            return b;
        }

        /// <summary><c>_fit_locked_isometric_camera</c>; returns the camera in the Godot frame (size = full height).</summary>
        static CameraTransform FitCamera(Camera camera, Bounds bounds)
        {
            Vector3 target = bounds.center;
            Vector3 direction = Frame.ToUnity(new Vec3(16f, 14f, 16f)).normalized;
            float distance = Mathf.Max(bounds.size.magnitude * 2.5f, 4f);
            if (distance <= 4f) distance = 4.00001f;
            camera.transform.position = target + direction * distance;
            camera.transform.LookAt(target, Vector3.up);
            camera.orthographic = true;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 4000f;
            camera.cullingMask = ~(1 << PhysicsLayers.Ceiling);
            camera.allowHDR = false;
            camera.allowMSAA = false;
            float aspect = (float)CaptureWidth / CaptureHeight;
            float maxX = 0f, maxY = 0f;
            Vector3 min = bounds.min, max = bounds.max;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) != 0 ? max.x : min.x, (i & 2) != 0 ? max.y : min.y, (i & 4) != 0 ? max.z : min.z);
                Vector3 local = camera.transform.InverseTransformPoint(corner);
                maxX = Mathf.Max(maxX, Mathf.Abs(local.x));
                maxY = Mathf.Max(maxY, Mathf.Abs(local.y));
            }
            float godotSize = Mathf.Max(Mathf.Max(maxY * 2f, maxX * 2f / aspect) * 1.15f, 1.5f);
            camera.orthographicSize = godotSize * 0.5f;
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
            Vec3 position = Frame.ToGodot(camera.transform.position), godotTarget = Frame.ToGodot(target);
            return new CameraTransform
            {
                Position = new double[] { position.X, position.Y, position.Z },
                Target = new double[] { godotTarget.X, godotTarget.Y, godotTarget.Z },
                Size = godotSize,
            };
        }

        /// <summary>Renders at the capture size; returns RGBA32 pixels (row 0 = bottom) and the PNG of the same bytes.</summary>
        static byte[] Render(Camera camera, Color clear, out byte[] png)
        {
            var rt = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = rt;
                camera.aspect = (float)CaptureWidth / CaptureHeight;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = clear;
                // Two renders: the GPU Resident Drawer uploads new instances during the first (see ScreenshotRunner).
                camera.Render();
                camera.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGBA32, false, false);
                tex.ReadPixels(new Rect(0, 0, CaptureWidth, CaptureHeight), 0, 0);
                tex.Apply();
                byte[] pixels = tex.GetRawTextureData<byte>().ToArray();
                png = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);
                return pixels;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }
    }
}
