using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using SynapticSea.EditorTools.Content;
using UnityEngine;

namespace SynapticSea.Tests
{
    /// <summary>The engine-free checks of <see cref="PromoteStructuralSource"/> (ported structural_source_contract rules).</summary>
    public class PromoteStructuralSourceTests
    {
        string _tmp;

        [SetUp]
        public void SetUp()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "ss-promote-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmp);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
        }

        static string ContractPath(string id) =>
            Path.Combine(Application.streamingAssetsPath, "data", "placement", "contracts", "structural", "ship_structural_v0", id + "_contract.json");

        static string RuntimeGlb(string id) =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", PromoteStructuralSource.RuntimeGlbAssetPath(id, "intact")));

        [Test]
        public void EveryAllowlistedRuntimeContractAndGlbPassesStrictValidation()
        {
            Assert.AreEqual(15, PromoteStructuralSource.ModuleIds.Length);
            foreach (string id in PromoteStructuralSource.ModuleIds)
            {
                var spec = PromoteStructuralSource.LoadSourceSpec(id, ContractPath(id), RuntimeGlb(id));
                Assert.AreEqual("ship_structural_v0", spec.KitId, id);
                Assert.Greater(spec.Sockets.Length, 0, id);
                Assert.AreEqual(2, spec.FootprintCells.Length, id);
                StringAssert.IsMatch("^[0-9a-f]{64}$", spec.ContractSha256);
                StringAssert.IsMatch("^[0-9a-f]{64}$", spec.SourceGlbSha256);
            }
        }

        [Test]
        public void ModulesOutsideTheAllowlistAreRejected()
        {
            Assert.IsFalse(PromoteStructuralSource.IsAllowlisted("pressure_door_1x1"));
            var e = Assert.Throws<PromoteStructuralSource.PromotionException>(() => PromoteStructuralSource.LoadSourceSpec("pressure_door_1x1", "x", "y"));
            StringAssert.Contains("unsupported structural source module", e.Message);
        }

        static string Mutate(string text, string from, string to)
        {
            Assert.IsTrue(text.Contains(from), "fixture text missing: " + from);
            return text.Replace(from, to);
        }

        [TestCase("\"modular_asset_spec\"", "\"asset_spec\"", "invalid structural contract kind")]
        [TestCase("\"module_id\": \"floor_1x1\"", "\"module_id\": \"floor_2x1\"", "contract module_id mismatch")]
        [TestCase("\"schema_version\": \"1.0.0\"", "\"schema_version\": \"2.0.0\"", "invalid structural contract schema_version")]
        [TestCase("\"grid_step_m\": 4", "\"grid_step_m\": 0", "invalid structural contract grid_step_m")]
        [TestCase("\"nav_blocker\": false", "\"nav_blocker\": \"no\"", "collision.nav_blocker")]
        public void ContractViolationsReportTheGodotMessages(string from, string to, string expected)
        {
            string text = File.ReadAllText(ContractPath("floor_1x1"));
            // Normalise the fixture's number formatting so the replacements find their targets.
            text = text.Replace("\"grid_step_m\": 4.0", "\"grid_step_m\": 4");
            byte[] bytes = Encoding.UTF8.GetBytes(Mutate(text, from, to));
            var e = Assert.Throws<PromoteStructuralSource.PromotionException>(() => PromoteStructuralSource.ParseSourceSpec("floor_1x1", bytes, RuntimeGlb("floor_1x1")));
            StringAssert.Contains(expected, e.Message);
        }

        [Test]
        public void FootprintMustBeIntegersAndSocketIdsUnique()
        {
            string text = File.ReadAllText(ContractPath("floor_1x1"));
            var footprint = System.Text.RegularExpressions.Regex.Replace(text, "\"footprint_cells\":\\s*\\[\\s*1\\s*,\\s*1\\s*\\]", "\"footprint_cells\": [1.0, 1]");
            Assert.AreNotEqual(text, footprint, "fixture footprint not found");
            var e = Assert.Throws<PromoteStructuralSource.PromotionException>(() => PromoteStructuralSource.ParseSourceSpec("floor_1x1", Encoding.UTF8.GetBytes(footprint), RuntimeGlb("floor_1x1")));
            StringAssert.Contains("footprint_cells", e.Message);

            var spec = PromoteStructuralSource.ParseSourceSpec("floor_1x1", Encoding.UTF8.GetBytes(text), RuntimeGlb("floor_1x1"));
            string first = spec.Sockets[0].SocketId, second = spec.Sockets[1].SocketId;
            string duplicated = text.Replace("\"" + second + "\"", "\"" + first + "\"");
            e = Assert.Throws<PromoteStructuralSource.PromotionException>(() => PromoteStructuralSource.ParseSourceSpec("floor_1x1", Encoding.UTF8.GetBytes(duplicated), RuntimeGlb("floor_1x1")));
            StringAssert.Contains("duplicate structural contract socket id", e.Message);

            e = Assert.Throws<PromoteStructuralSource.PromotionException>(() => PromoteStructuralSource.ParseSourceSpec("floor_1x1", Encoding.UTF8.GetBytes("{ not json"), RuntimeGlb("floor_1x1")));
            StringAssert.Contains("invalid structural contract JSON", e.Message);
        }

        static byte[] Glb(uint version, uint length, string magic = "glTF")
        {
            var bytes = new byte[length < 12 ? 12 : length];
            Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
            BitConverter.GetBytes(version).CopyTo(bytes, 4);
            BitConverter.GetBytes(length).CopyTo(bytes, 8);
            return bytes;
        }

        [Test]
        public void GlbHeaderNeedsMagicVersionTwoAndTheFileLength()
        {
            Assert.IsTrue(PromoteStructuralSource.IsValidGlbHeader(Glb(2, 64), 64));
            Assert.IsFalse(PromoteStructuralSource.IsValidGlbHeader(Glb(2, 64, "glTX"), 64));
            Assert.IsFalse(PromoteStructuralSource.IsValidGlbHeader(Glb(1, 64), 64));
            Assert.IsFalse(PromoteStructuralSource.IsValidGlbHeader(Glb(2, 64), 65));
            Assert.IsFalse(PromoteStructuralSource.IsValidGlbHeader(new byte[4], 4));

            string path = Path.Combine(_tmp, "bad.glb");
            File.WriteAllBytes(path, Glb(2, 100));
            File.AppendAllText(path, "x");
            var e = Assert.Throws<PromoteStructuralSource.PromotionException>(() => PromoteStructuralSource.ValidateGlbFile(path, "floor_1x1"));
            StringAssert.Contains("invalid source GLB", e.Message);
            string wrongExtension = Path.Combine(_tmp, "good.gltf");
            File.WriteAllBytes(wrongExtension, Glb(2, 64));
            e = Assert.Throws<PromoteStructuralSource.PromotionException>(() => PromoteStructuralSource.ValidateGlbFile(wrongExtension, "floor_1x1"));
            StringAssert.Contains("invalid source GLB extension", e.Message);
        }

        [Test]
        public void IdenticalStagedAndRuntimeGlbsAreSkippedUnlessForced()
        {
            string a = Path.Combine(_tmp, "a.glb"), b = Path.Combine(_tmp, "b.glb"), c = Path.Combine(_tmp, "c.glb");
            File.WriteAllBytes(a, Glb(2, 32));
            File.WriteAllBytes(b, Glb(2, 32));
            File.WriteAllBytes(c, Glb(2, 40));
            Assert.IsTrue(PromoteStructuralSource.ShouldSkipPromotion(a, b, force: false));
            Assert.IsFalse(PromoteStructuralSource.ShouldSkipPromotion(a, b, force: true));
            Assert.IsFalse(PromoteStructuralSource.ShouldSkipPromotion(a, c, force: false));
            Assert.IsFalse(PromoteStructuralSource.ShouldSkipPromotion(a, Path.Combine(_tmp, "missing.glb"), force: false));
        }

        [Test]
        public void AtomicCopyReplacesTheDestinationAndLeavesNoTemporary()
        {
            string source = Path.Combine(_tmp, "staged.glb"), destination = Path.Combine(_tmp, "runtime", "floor_1x1.glb");
            File.WriteAllBytes(source, Glb(2, 20));
            PromoteStructuralSource.CopyAtomic(source, destination);
            CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(destination));
            File.WriteAllBytes(source, Glb(2, 28));
            PromoteStructuralSource.CopyAtomic(source, destination);
            CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(destination));
            CollectionAssert.AreEquivalent(new[] { "floor_1x1.glb" }, Array.ConvertAll(Directory.GetFiles(Path.GetDirectoryName(destination)), Path.GetFileName));
        }

        [Test]
        public void VariantFileNamesFollowTheGodotSuffixes()
        {
            Assert.AreEqual("wall_end_cap.glb", PromoteStructuralSource.VariantFileName("wall_end_cap", "intact"));
            Assert.AreEqual("wall_end_cap_damaged.glb", PromoteStructuralSource.VariantFileName("wall_end_cap", "damaged"));
            Assert.AreEqual("wall_end_cap_breached.glb", PromoteStructuralSource.VariantFileName("wall_end_cap", "breached"));
            Assert.Throws<PromoteStructuralSource.PromotionException>(() => PromoteStructuralSource.VariantFileName("wall_end_cap", "destroyed"));
        }
    }
}
