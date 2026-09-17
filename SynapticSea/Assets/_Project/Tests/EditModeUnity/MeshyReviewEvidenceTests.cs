using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using SynapticSea.EditorTools.Content;
using UnityEngine;
using E = SynapticSea.EditorTools.Content.MeshyReviewEvidence;

namespace SynapticSea.Tests
{
    /// <summary>
    /// Pixel gates, camera validation, canonical JSON and task governance of the Unity Meshy runtime review, on synthetic
    /// inputs (no real Meshy task exists in the repository).
    /// </summary>
    public class MeshyReviewEvidenceTests
    {
        const int W = E.CaptureWidth, H = E.CaptureHeight;
        string _tmp;

        [SetUp]
        public void SetUp()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "ss-meshy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmp);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
        }

        // ------------------------------------------------------------------ canonical JSON

        [TestCase(0.0, "0.0")]
        [TestCase(-0.0, "-0.0")]
        [TestCase(1.0, "1.0")]
        [TestCase(100.0, "100.0")]
        [TestCase(0.1, "0.1")]
        [TestCase(1.0 / 3.0, "0.3333333333333333")]
        [TestCase(0.0001, "0.0001")]
        [TestCase(0.00001, "1e-05")]
        [TestCase(2.5e-7, "2.5e-07")]
        [TestCase(1e15, "1000000000000000.0")]
        [TestCase(1e16, "1e+16")]
        [TestCase(123456789.125, "123456789.125")]
        [TestCase(-4.00001, "-4.00001")]
        [TestCase(5e-324, "5e-324")]
        [TestCase(double.MaxValue, "1.7976931348623157e+308")]
        [TestCase(0.004000000000000004, "0.004000000000000004")]
        public void FloatsFollowPythonRepr(double value, string expected)
        {
            Assert.AreEqual(expected, E.PythonFloatRepr(value));
        }

        [Test]
        public void CanonicalJsonSortsKeysUsesCompactSeparatorsAndEndsWithNewline()
        {
            var doc = new Dictionary<string, object>
            {
                { "b", 1L },
                { "a", new List<object> { true, null, "x\"\n\\é\u0001", 0.5, (long)-3 } },
                { "A", new Dictionary<string, object> { { "z", 2.0 }, { "y", "" } } },
            };
            string json = Encoding.UTF8.GetString(E.CanonicalJsonBytes(doc));
            Assert.AreEqual("{\"A\":{\"y\":\"\",\"z\":2.0},\"a\":[true,null,\"x\\\"\\n\\\\é\\u0001\",0.5,-3],\"b\":1}\n", json);
            Assert.Throws<E.ReviewException>(() => E.CanonicalJsonBytes(new List<object> { double.NaN }));
        }

        // ------------------------------------------------------------------ pixels

        static byte[] Image(Func<int, int, Color32> topDown)
        {
            var bytes = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    Color32 c = topDown(x, y);
                    int i = ((H - 1 - y) * W + x) * 4;
                    bytes[i] = c.r; bytes[i + 1] = c.g; bytes[i + 2] = c.b; bytes[i + 3] = c.a;
                }
            return bytes;
        }

        static bool InBlock(int x, int y) => x >= 600 && x < 1000 && y >= 300 && y < 600;

        [Test]
        public void SamplesAreTopDownOnTheGovernedGrid()
        {
            var samples = E.GovernedSamples(Image((x, y) => y == 0 && x == 0 ? new Color32(255, 0, 0, 255) : new Color32(0, 0, 0, 0)));
            Assert.AreEqual(E.StagedSampleMax, samples.Count);
            Assert.AreEqual(2304, samples.Count);
            Assert.AreEqual((0, 0), (samples[0].X, samples[0].Y));
            Assert.AreEqual(1.0, samples[0].R);
            Assert.AreEqual(0.2126, samples[0].Luma, 1e-12);
            Assert.AreEqual((25, 0), (samples[1].X, samples[1].Y));
            Assert.AreEqual((0, 25), (samples[64].X, samples[64].Y));
        }

        [Test]
        public void StagedGateCountsOpaqueSamplesAndLumaRange()
        {
            var staged = E.Staged(E.GovernedSamples(Image((x, y) => InBlock(x, y) ? new Color32((byte)(x % 256), 128, 40, 255) : new Color32(0, 0, 0, 0))));
            Assert.IsTrue(staged.Pass);
            Assert.AreEqual(16 * 12, staged.OpaquePixels);
            Assert.Greater(staged.LumaRange, E.StagedLumaRangeMin);
            E.ValidateStaged(staged);

            var blank = E.Staged(E.GovernedSamples(Image((x, y) => new Color32(0, 0, 0, 0))));
            Assert.IsFalse(blank.Pass);
            Assert.AreEqual(0, blank.OpaquePixels);
            Assert.Throws<E.ReviewException>(() => E.ValidateStaged(blank));
        }

        [Test]
        public void ContextualGateMeasuresTheChangeUnderTheSilhouette()
        {
            var staged = E.GovernedSamples(Image((x, y) => InBlock(x, y) ? new Color32(200, 200, 200, 255) : new Color32(0, 0, 0, 0)));
            var reference = E.GovernedSamples(Image((x, y) => new Color32(10, 20, 30, 255)));
            var final = E.GovernedSamples(Image((x, y) => InBlock(x, y) ? new Color32(90, 20, 30, 255) : new Color32(10, 20, 30, 255)));
            var contextual = E.Contextual(staged, reference, final);
            Assert.IsTrue(contextual.Pass);
            Assert.AreEqual(192, contextual.ReferencePixels);
            Assert.AreEqual(192, contextual.ChangedPixels);
            Assert.AreEqual(80 / 255.0, contextual.MaxDelta, 1e-15);
            E.ValidateContextual(contextual);

            var unchanged = E.Contextual(staged, reference, reference);
            Assert.IsFalse(unchanged.Pass);
            Assert.AreEqual(0, unchanged.ChangedPixels);
            Assert.Throws<E.ReviewException>(() => E.Contextual(staged, reference.Take(10).ToList(), final));
        }

        [Test]
        public void NonBlankGateRejectsUniformCaptures()
        {
            Assert.IsFalse(E.IsVisible(Image((x, y) => new Color32(40, 40, 40, 255))));
            Assert.IsTrue(E.IsVisible(Image((x, y) => new Color32((byte)(x / 8), 40, 40, 255))));
            Assert.IsFalse(E.IsVisible(Image((x, y) => new Color32((byte)(x / 8), 40, 40, 0))));
        }

        // ------------------------------------------------------------------ camera

        static E.CameraTransform LockedCamera(double distance, double size)
        {
            double length = Math.Sqrt(16 * 16 + 14 * 14 + 16 * 16);
            var target = new[] { 12.0, 0.9, -4.0 };
            return new E.CameraTransform
            {
                Target = target,
                Position = new[] { target[0] + 16 / length * distance, target[1] + 14 / length * distance, target[2] + 16 / length * distance },
                Size = size,
            };
        }

        [Test]
        public void CameraMustBeLockedIsometricOrthographicWithinTheSizeBound()
        {
            E.ValidateCamera(LockedCamera(6.0, 3.0), new[] { 1.0, 2.0, 1.0 });
            Assert.Throws<E.ReviewException>(() => E.ValidateCamera(LockedCamera(3.5, 3.0), null), "distance below 4");
            Assert.Throws<E.ReviewException>(() => E.ValidateCamera(LockedCamera(6.0, 1.0), null), "size below 1.5");
            Assert.Throws<E.ReviewException>(() => E.ValidateCamera(LockedCamera(6.0, 40.0), new[] { 1.0, 1.0, 1.0 }), "size above diagonal x 16");
            var mirrored = LockedCamera(6.0, 3.0);
            mirrored.Position[0] = mirrored.Target[0] - (mirrored.Position[0] - mirrored.Target[0]);
            Assert.Throws<E.ReviewException>(() => E.ValidateCamera(mirrored, null), "Unity-frame direction must be converted to Godot's");
            Assert.AreEqual(4096.0, E.CameraSizeLimit(null));
            Assert.AreEqual(1.5, E.CameraSizeLimit(new[] { 0.01, 0.01, 0.01 }));
        }

        // ------------------------------------------------------------------ document

        static readonly string Hash = new string('a', 64);

        static E.ReviewInputs Inputs() => new E.ReviewInputs
        {
            AssetId = "probe_v1", TaskId = "task-1", ContractSha256 = Hash, CleanedGlbSha256 = new string('b', 64),
            BlenderValidationSha256 = new string('c', 64), BoundsDimensions = new[] { 1.0, 2.0, 1.0 },
        };

        static List<E.CaptureRecord> Captures() =>
            E.Seeds.SelectMany(s => E.LightingModes.Select(l => new E.CaptureRecord
            {
                Seed = s, Lighting = l, Camera = LockedCamera(6.0, 3.0),
                Staged = new E.StagedVisibility { Pass = true, OpaquePixels = 40, LumaRange = 0.25 },
                Contextual = new E.ContextualVisibility { Pass = true, ReferencePixels = 40, ChangedPixels = 30, MaxDelta = 0.5 },
                OutputSha256 = Hash, StagedSha256 = Hash, ReferenceSha256 = Hash,
            })).ToList();

        [Test]
        public void DocumentHoldsSixOrderedCapturesAndEighteenHashes()
        {
            var captures = Captures();
            captures.Reverse();
            var doc = E.BuildDocument(Inputs(), captures);
            var ordered = (List<object>)doc["captures"];
            Assert.AreEqual(6, ordered.Count);
            Assert.AreEqual(42L, ((IDictionary<string, object>)ordered[0])["seed"]);
            Assert.AreEqual("normal", ((IDictionary<string, object>)ordered[0])["lighting"]);
            Assert.AreEqual(777L, ((IDictionary<string, object>)ordered[5])["seed"]);
            Assert.AreEqual(18, ((IDictionary<string, object>)doc["output_hashes"]).Count);
            CollectionAssert.AreEquivalent(E.FixedOutputNames().Take(18), ((IDictionary<string, object>)doc["output_hashes"]).Keys);
            string json = Encoding.UTF8.GetString(E.CanonicalJsonBytes(doc));
            StringAssert.StartsWith("{\"asset_id\":\"probe_v1\",\"blender_validation_sha256\":", json);
            StringAssert.Contains("\"staged_visibility\":{\"luma_range\":0.25,\"opaque_pixels\":40,\"pass\":true}", json);

            Assert.Throws<E.ReviewException>(() => E.BuildDocument(Inputs(), Captures().Take(5).ToList()));
            var failing = Captures();
            failing[2].Staged.Pass = false;
            Assert.Throws<E.ReviewException>(() => E.BuildDocument(Inputs(), failing));
        }

        // ------------------------------------------------------------------ governed task

        static Dictionary<string, object> R4(string sha, long size, string contractSha) => new Dictionary<string, object>
        {
            { "schema_version", "1.0.0" }, { "document_kind", "meshy_blender_validation" }, { "status", "PASS" }, { "task_id", "task-1" },
            { "asset_id", "probe_v1" }, { "contract_sha256", contractSha }, { "sha256", sha }, { "byte_size", size }, { "mesh_count", 1L },
            { "triangle_count", 12L }, { "material_names", new List<object> { "Body" } },
            { "bounds", new Dictionary<string, object> { { "min", new List<object> { -0.5, 0.0, -0.5 } }, { "max", new List<object> { 0.5, 2.0, 0.5 } }, { "dimensions", new List<object> { 1.0, 2.0, 1.0 } } } },
            { "uvs_present", true },
            { "uv_evidence", new List<object> { new Dictionary<string, object> { { "mesh_index", 0L }, { "primitive_index", 0L }, { "accessor", 3L }, { "vertex_count", 24L }, { "uv_count", 24L }, { "finite", true }, { "range_valid", true } } } },
            { "blender_reimport_passed", true }, { "master_provenance", null },
        };

        (string task, string contract) WriteTask(Action<Dictionary<string, Dictionary<string, object>>> mutate = null)
        {
            string task = Path.Combine(_tmp, "artifacts", "_staging", "meshy", "probe_v1", "task-1");
            Directory.CreateDirectory(task);
            var contract = new Dictionary<string, object>
            {
                { "schema_version", "1.0.0" }, { "document_kind", "ai_asset_contract" }, { "asset_id", "probe_v1" }, { "category", "gameplay_prop" },
                { "dimensions_m", new List<object> { 1.0, 2.0, 1.0 } },
            };
            byte[] contractBytes = E.CanonicalJsonBytes(contract);
            string contractPath = Path.Combine(_tmp, "probe_v1.json");
            File.WriteAllBytes(contractPath, contractBytes);
            File.WriteAllBytes(Path.Combine(task, "contract.json"), contractBytes);
            string sha = PromoteStructuralSource.Sha256Hex(contractBytes);

            string glb = Path.GetFullPath(Path.Combine(Application.dataPath, "..", PromoteStructuralSource.RuntimeGlbAssetPath("floor_1x1", "intact")));
            File.Copy(glb, Path.Combine(task, "cleaned.glb"), true);
            long size = new FileInfo(glb).Length;
            var docs = new Dictionary<string, Dictionary<string, object>>
            {
                { "review.json", new Dictionary<string, object> { { "asset_id", "probe_v1" }, { "task_id", "task-1" }, { "state", "selected" } } },
                { "generation.json", new Dictionary<string, object> { { "asset_id", "probe_v1" }, { "task_id", "task-1" }, { "status", "SUCCEEDED" }, { "contract_sha256", sha }, { "contract_artifact_sha256", sha } } },
                { "blender-validation.json", R4(PromoteStructuralSource.Sha256Hex(glb), size, sha) },
            };
            mutate?.Invoke(docs);
            foreach (var kv in docs) File.WriteAllBytes(Path.Combine(task, kv.Key), E.CanonicalJsonBytes(kv.Value));
            return (task, contractPath);
        }

        [Test]
        public void GovernedTaskInputsLoadAndBindTheCleanedGlb()
        {
            var (task, contract) = WriteTask();
            var inputs = E.LoadTaskInputs(_tmp, contract, task);
            Assert.AreEqual("probe_v1", inputs.AssetId);
            Assert.AreEqual("task-1", inputs.TaskId);
            Assert.AreEqual("gameplay_prop", inputs.Category);
            CollectionAssert.AreEqual(new[] { 1.0, 2.0, 1.0 }, inputs.BoundsDimensions);
            StringAssert.IsMatch("^[0-9a-f]{64}$", inputs.BlenderValidationSha256);
        }

        [TestCase("generation.json", "status", "FAILED", "SUCCEEDED generation")]
        [TestCase("review.json", "state", "rejected", "selected or promotion_ready")]
        [TestCase("blender-validation.json", "sha256", "0000000000000000000000000000000000000000000000000000000000000000", "does not match the current task")]
        [TestCase("blender-validation.json", "status", "FAIL", "document identity")]
        public void TaskEvidenceMustBeBound(string file, string key, string value, string expected)
        {
            var (task, contract) = WriteTask(docs => docs[file][key] = value);
            var e = Assert.Throws<E.ReviewException>(() => E.LoadTaskInputs(_tmp, contract, task));
            StringAssert.Contains(expected, e.Message);
        }

        [Test]
        public void TaskLayoutAndCanonicalFormAreEnforced()
        {
            var (task, contract) = WriteTask();
            File.WriteAllText(Path.Combine(task, "review.json"), "{\"asset_id\": \"probe_v1\", \"state\": \"selected\", \"task_id\": \"task-1\"}\n");
            var e = Assert.Throws<E.ReviewException>(() => E.LoadTaskInputs(_tmp, contract, task));
            StringAssert.Contains("not canonical", e.Message);

            string outside = Path.Combine(_tmp, "elsewhere", "probe_v1", "task-1");
            Directory.CreateDirectory(outside);
            e = Assert.Throws<E.ReviewException>(() => E.LoadTaskInputs(_tmp, contract, outside));
            StringAssert.Contains("under Meshy staging", e.Message);

            var (task2, contract2) = WriteTask(docs => ((Dictionary<string, object>)docs["blender-validation.json"]["bounds"])["dimensions"] = new List<object> { 1.0, 2.5, 1.0 });
            e = Assert.Throws<E.ReviewException>(() => E.LoadTaskInputs(_tmp, contract2, task2));
            StringAssert.Contains("bounds dimensions", e.Message);
        }

        // ------------------------------------------------------------------ publication

        [Test]
        public void PublicationWritesFixedLeavesReportLastAndNeverOverwritesDifferentEvidence()
        {
            string preview = E.FixedPreviewPath(_tmp, "probe_v1");
            var payloads = E.FixedOutputNames().Take(18).ToDictionary(n => n, n => Encoding.ASCII.GetBytes(n), StringComparer.Ordinal);
            byte[] report = Encoding.ASCII.GetBytes("{}\n");
            E.Publish(preview, payloads, report);
            CollectionAssert.AreEquivalent(E.FixedOutputNames(), Directory.GetFiles(preview).Select(Path.GetFileName));
            E.Publish(preview, payloads, report);

            var changed = new Dictionary<string, byte[]>(payloads, StringComparer.Ordinal) { [E.CaptureName(42, "dark")] = new byte[] { 1 } };
            Assert.Throws<E.ReviewException>(() => E.Publish(preview, changed, report));
            File.Delete(Path.Combine(preview, E.ReportName));
            Assert.Throws<E.ReviewException>(() => E.Publish(preview, changed, report));

            File.WriteAllText(Path.Combine(preview, "notes.txt"), "x");
            Assert.Throws<E.ReviewException>(() => E.FixedPreviewPath(_tmp, "probe_v1"));
            Assert.Throws<E.ReviewException>(() => E.FixedPreviewPath(_tmp, "Bad/Id"));
        }
    }
}
