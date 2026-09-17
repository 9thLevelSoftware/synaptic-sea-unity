using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Procgen
{
    /// <summary>
    /// Kit + structural placement contracts are the only socket/footprint authority.
    /// Missing sockets or footprint on either row leave a module ungated (not shippable).
    /// </summary>
    public class KitAuthorityAuditTests
    {
        CollectingLog _log;

        [SetUp]
        public void SetUp()
        {
            _log = new CollectingLog();
            CoreServices.Log = _log;
            CoreServices.Resources = new FileSystemResourceReader(Fixtures.StreamingDataRoot);
        }

        [TearDown]
        public void TearDown()
        {
            CatalogRegistry.Clear();
            CoreServices.Log = NullLog.Instance;
        }

        [Test]
        public void MissingKitSocketsOrFootprint_AreUngated()
        {
            var kitRow = new GdDict
            {
                { "module_id", "ghost_module" },
                { "module_family", "junction" },
            };
            KitAuthorityModuleReport report = KitAuthorityAudit.EvaluateModule(kitRow, null, null, hasV0Twin: true);
            Assert.IsTrue(report.Ungated, "missing kit sockets/footprint must be ungated");
            StringAssert.Contains("kit missing footprint_cells", string.Join("\n", report.Issues));
            StringAssert.Contains("kit missing socket_names", string.Join("\n", report.Issues));
            StringAssert.Contains("contract missing", string.Join("\n", report.Issues));
        }

        [Test]
        public void EmptyContractSocketsOrFootprint_AreUngated()
        {
            var kitRow = new GdDict
            {
                { "module_id", "ghost_module" },
                { "module_family", "junction" },
                { "footprint_cells", GdArray.Of(1L, 1L) },
                { "socket_names", GdArray.Of("SOCK_wall_face_north_01") },
                { "pivot_policy", "edge-center" },
                { "nav_blocker", true },
            };
            var contract = new GdDict
            {
                { "module_id", "ghost_module" },
                { "footprint_cells", new GdArray() },
                { "sockets", new GdArray() },
            };
            KitAuthorityModuleReport report = KitAuthorityAudit.EvaluateModule(kitRow, contract, null, hasV0Twin: true);
            Assert.IsTrue(report.Ungated);
            StringAssert.Contains("contract missing footprint_cells", string.Join("\n", report.Issues));
            StringAssert.Contains("contract missing sockets", string.Join("\n", report.Issues));
        }

        [Test]
        public void KitSocketNameAbsentFromContract_IsUngated()
        {
            var kitRow = GatedKitRow("ghost_module", GdArray.Of("SOCK_invented_face_north_01"));
            var contract = GatedContract("ghost_module", "wall_face_north_01", "wall_face");
            KitAuthorityModuleReport report = KitAuthorityAudit.EvaluateModule(kitRow, contract, null, hasV0Twin: true);
            Assert.IsTrue(report.Ungated);
            StringAssert.Contains("SOCK_invented_face_north_01", string.Join("\n", report.Issues));
        }

        [Test]
        public void NewModuleWithoutCompanionAssetJson_IsUngated()
        {
            var kitRow = GatedKitRow("wall_x_junction", GdArray.Of(
                "SOCK_wall_face_north_01", "SOCK_wall_face_east_01",
                "SOCK_wall_face_south_01", "SOCK_wall_face_west_01"));
            var contract = new GdDict
            {
                { "module_id", "wall_x_junction" },
                { "module_family", "junction" },
                { "footprint_cells", GdArray.Of(1L, 1L) },
                { "sockets", GdArray.Of(
                    Socket("wall_face_north_01", "wall_face"),
                    Socket("wall_face_east_01", "wall_face"),
                    Socket("wall_face_south_01", "wall_face"),
                    Socket("wall_face_west_01", "wall_face")) },
            };
            KitAuthorityModuleReport missing = KitAuthorityAudit.EvaluateModule(kitRow, contract, null, hasV0Twin: false);
            Assert.IsTrue(missing.Ungated, "mesh alone is not shippable for new modules");
            StringAssert.Contains(KitAuthorityAudit.CompanionFileName("wall_x_junction"), string.Join("\n", missing.Issues));

            var companion = KitAuthorityAudit.CompanionFromKitRow(kitRow, "four 4x3x0.2 wing boxes");
            KitAuthorityModuleReport gated = KitAuthorityAudit.EvaluateModule(kitRow, contract, companion, hasV0Twin: false);
            Assert.IsFalse(gated.Ungated, string.Join("\n", gated.Issues));
        }

        [Test]
        public void IthappyKit_NoModuleIsMissingSocketsOrFootprint()
        {
            KitAuthorityReport report = KitAuthorityAudit.AuditIthappy(Fixtures.RepoRoot);
            Assert.AreEqual(KitCatalog.ITHAPPY_KIT_ID, report.KitId);
            Assert.AreEqual(16, report.Modules.Count);
            CollectionAssert.Contains(report.Modules.Select(m => m.ModuleId).ToList(), "wall_x_junction");

            var missing = new List<string>();
            foreach (var module in report.Modules)
            {
                foreach (string issue in module.Issues)
                {
                    if (issue.Contains("missing footprint") || issue.Contains("missing socket"))
                        missing.Add(module.ModuleId + ": " + issue);
                }
            }
            Assert.IsEmpty(missing, "ungated (missing sockets/footprint vs kit/contract):\n" + string.Join("\n", missing));
        }

        [Test]
        public void IthappyKit_KitSocketNamesComeFromContracts_NotInvented()
        {
            KitAuthorityReport report = KitAuthorityAudit.AuditIthappy(Fixtures.RepoRoot);
            var invented = report.Modules
                .SelectMany(m => m.Issues.Where(i => i.StartsWith("kit socket not in contract")).Select(i => m.ModuleId + ": " + i))
                .ToList();
            Assert.IsEmpty(invented, string.Join("\n", invented));
            foreach (var module in report.Modules)
            {
                Assert.That(module.KitSocketNames, Is.Not.Empty, module.ModuleId);
                foreach (string name in module.KitSocketNames)
                {
                    Assert.That(name, Does.StartWith("SOCK_"), module.ModuleId + " " + name);
                    StringAssert.AreEqualIgnoringCase(
                        KitAuthorityAudit.SocketIdFromKitName(name),
                        module.ContractSocketIds.First(id => id == KitAuthorityAudit.SocketIdFromKitName(name)));
                }
            }
        }

        [Test]
        public void IthappyKit_WallXJunctionCompanionAssetJsonGatesTheNewModule()
        {
            KitAuthorityReport report = KitAuthorityAudit.AuditIthappy(Fixtures.RepoRoot);
            KitAuthorityModuleReport wallX = report.Modules.Single(m => m.ModuleId == "wall_x_junction");
            Assert.IsFalse(wallX.HasV0Twin, "wall_x_junction is ithappy-only");
            Assert.IsTrue(wallX.HasCompanionAssetJson, "Forge companion wall_x_junction.asset.json must sit beside the GLB");
            Assert.IsFalse(wallX.Ungated, string.Join("\n", wallX.Issues));
            CollectionAssert.AreEqual(new[]
            {
                "SOCK_wall_face_north_01",
                "SOCK_wall_face_east_01",
                "SOCK_wall_face_south_01",
                "SOCK_wall_face_west_01",
            }, wallX.KitSocketNames);
        }

        [Test]
        public void SocketCatalog_LoadsIthappyContracts_WithoutInventedSocketIds()
        {
            var catalog = new ModularSocketCatalog();
            Assert.IsTrue(catalog.LoadKit(KitCatalog.ITHAPPY_KIT_ID));
            Assert.AreEqual(KitCatalog.ITHAPPY_KIT_ID, catalog.KitId);

            KitAuthorityReport report = KitAuthorityAudit.AuditIthappy(Fixtures.RepoRoot);
            foreach (var module in report.Modules)
            {
                var catalogIds = new List<string>();
                foreach (var socketVariant in catalog.SocketsOf(module.ModuleId))
                {
                    if (socketVariant is GdDict socket) catalogIds.Add(socket.GetString("id"));
                }
                CollectionAssert.AreEquivalent(module.ContractSocketIds, catalogIds, module.ModuleId);
            }
        }

        [Test]
        public void SocketCatalog_WallFaceKindIsAuthoredButNotAnEnclosureJoin()
        {
            // Gap: Godot ENCLOSURE_KINDS omits wall_face. wall_x_junction / wall_t_junction sockets
            // therefore never bind via SocketsCompatible. The compiler still chooses T-junctions by
            // HasModule, not by inventing SOCK names. Do not add ad-hoc synonyms here.
            CollectionAssert.DoesNotContain(ModularSocketCatalog.ENCLOSURE_KINDS, "wall_face");
            CollectionAssert.Contains(KitAuthorityAudit.ArtLockKinds, "wall_face");

            var catalog = new ModularSocketCatalog();
            Assert.IsTrue(catalog.LoadKit(KitCatalog.ITHAPPY_KIT_ID));
            GdArray sockets = catalog.SocketsOf("wall_x_junction");
            Assert.AreEqual(4, sockets.Count);
            Assert.AreEqual("wall_face", ((GdDict)sockets[0]).GetString("kind"));
            Assert.IsFalse(catalog.SocketsCompatible((GdDict)sockets[0], (GdDict)sockets[1]));
        }

        [Test]
        public void Compiler_ChooseModuleReadsContractKinds_NotInventedNames()
        {
            var catalog = new ModularSocketCatalog();
            Assert.IsTrue(catalog.LoadKit(KitCatalog.ITHAPPY_KIT_ID));
            Assert.AreEqual("floor_1x1", catalog.ChooseModule(new[] { "floor_edge", "floor_top" }, "floor_1x1"));
            Assert.AreEqual("wall_straight_1x1", catalog.ChooseModule(new[] { "wall_base", "wall_end" }, "wall_straight_1x1"));
            Assert.AreEqual("wall_t_junction", catalog.ChooseModule(new[] { "wall_face" }, "wall_t_junction"));
            Assert.IsTrue(catalog.HasModule("wall_x_junction"));
            Assert.IsTrue(catalog.HasKind("wall_x_junction", "wall_face"));
        }

        [Test]
        public void CompanionPath_SitsBesideTheGlb()
        {
            string root = Path.Combine(Fixtures.RepoRoot, "SynapticSea", "Assets", "Content", "Structural", "ithappy");
            string expected = Path.Combine(root, "wall_x_junction", "wall_x_junction.asset.json");
            Assert.AreEqual(expected, KitAuthorityAudit.CompanionAssetJsonPath(root, "wall_x_junction"));
            Assert.IsTrue(File.Exists(expected), expected);
            Assert.IsTrue(File.Exists(Path.Combine(root, "wall_x_junction", "wall_x_junction.glb")));
        }

        static GdDict GatedKitRow(string moduleId, GdArray socketNames) => new GdDict
        {
            { "module_id", moduleId },
            { "module_family", "junction" },
            { "footprint_cells", GdArray.Of(1L, 1L) },
            { "socket_names", socketNames },
            { "pivot_policy", "edge-center" },
            { "nav_blocker", true },
        };

        static GdDict GatedContract(string moduleId, string socketId, string kind) => new GdDict
        {
            { "module_id", moduleId },
            { "module_family", "junction" },
            { "footprint_cells", GdArray.Of(1L, 1L) },
            { "sockets", GdArray.Of(Socket(socketId, kind)) },
        };

        static GdDict Socket(string id, string kind) => new GdDict
        {
            { "id", id },
            { "kind", kind },
            { "position_m", GdArray.Of(0.0, 0.0, 0.0) },
            { "compatible_kinds", GdArray.Of(kind) },
        };
    }
}
