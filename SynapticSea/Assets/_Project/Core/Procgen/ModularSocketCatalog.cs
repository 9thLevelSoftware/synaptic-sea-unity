// Ported from scripts/procgen/modular_socket_catalog.gd @ 96ecb2b0
using System.Collections;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Procgen
{
    /// <summary>
    /// Loads ModularAssetSpec JSON for a structural kit and answers socket queries.
    /// The compiler owns topology; this catalog only reports authored sockets.
    /// </summary>
    public sealed class ModularSocketCatalog
    {
        public const string CONTRACTS_ROOT = "res://data/placement/contracts/structural/";
        public const string DEFAULT_KIT_ID = "ship_structural_v0";
        public const double GRID_SNAP_M = 4.0;
        public const double POSITION_EPSILON_M = 0.05;

        public static readonly IReadOnlyList<string> ENCLOSURE_KINDS = new[]
        {
            "floor_edge",
            "corridor_edge",
            "wall_base",
            "wall_end",
            "wall_edge",
            "portal_edge",
            "portal_center",
            "inner_corner_vertex",
            "outer_corner_vertex",
            "ceiling_edge",
            "floor_top",
            "ceiling_bottom",
        };

        /// <summary>
        /// v0 wall_straight_1x1 exposes wall_end (and wall_base on some kits) while authored compatible_kinds name
        /// the abstract join wall_edge.
        /// </summary>
        public static readonly IReadOnlyList<string> WALL_JOIN_KINDS = new[] { "wall_edge", "wall_end", "wall_base" };

        public string KitId = "";

        /// <summary>module_id -&gt; <c>{module_id, module_family, kit_id, sockets, path}</c>.</summary>
        public GdDict Modules = new GdDict();

        public bool LoadKit(string pKitId)
        {
            Modules.Clear();
            KitId = !string.IsNullOrEmpty(pKitId) ? pKitId : DEFAULT_KIT_ID;
            if (!LoadKitDirectory(KitId) || Modules.IsEmpty)
            {
                if (KitId != DEFAULT_KIT_ID) LoadKitDirectory(DEFAULT_KIT_ID);
            }
            return !Modules.IsEmpty;
        }

        public bool HasModule(string moduleId) => Modules.Has(moduleId);

        /// <summary>The module's socket array (shared reference, like GDScript), or a new empty array.</summary>
        public GdArray SocketsOf(string moduleId)
        {
            if (!Modules.Has(moduleId)) return new GdArray();
            if (!(Modules[moduleId] is GdDict record)) return new GdArray();
            if (!(record.Get("sockets", new GdArray()) is GdArray sockets)) return new GdArray();
            return sockets;
        }

        public List<string> KindsOf(string moduleId)
        {
            var kinds = new List<string>();
            foreach (var socketVariant in SocketsOf(moduleId))
            {
                if (!(socketVariant is GdDict socket)) continue;
                string kind = V.Str(socket.Get("kind", ""));
                if (kind.Length != 0 && !kinds.Contains(kind)) kinds.Add(kind);
            }
            return kinds;
        }

        public bool HasKind(string moduleId, string kind) => KindsOf(moduleId).Contains(kind);

        /// <param name="requiredKinds">GDScript <c>Array</c> (a <see cref="GdArray"/> or any string sequence).</param>
        public bool HasAllKinds(string moduleId, IEnumerable requiredKinds)
        {
            List<string> kinds = KindsOf(moduleId);
            if (requiredKinds == null) return true;
            foreach (var kindVariant in requiredKinds)
                if (!kinds.Contains(V.Str(kindVariant))) return false;
            return true;
        }

        public string ModuleFamily(string moduleId)
        {
            if (!Modules.Has(moduleId)) return "";
            if (!(Modules[moduleId] is GdDict record)) return "";
            return V.Str(record.Get("module_family", ""));
        }

        public string ChooseModule(IEnumerable requiredKinds, string preferredId = "")
        {
            preferredId = preferredId ?? "";
            if (preferredId.Length != 0 && HasModule(preferredId) && HasAllKinds(preferredId, requiredKinds))
                return preferredId;
            var ids = new List<object>(Modules.Keys);
            GdSort.Sort(ids);
            foreach (var idVariant in ids)
            {
                string moduleId = V.Str(idVariant);
                if (HasAllKinds(moduleId, requiredKinds)) return moduleId;
            }
            if (preferredId.Length != 0 && HasModule(preferredId)) return preferredId;
            return "";
        }

        public bool SocketsCompatible(GdDict socketA, GdDict socketB)
        {
            string kindA = V.Str(socketA.Get("kind", ""));
            string kindB = V.Str(socketB.Get("kind", ""));
            if (kindA.Length == 0 || kindB.Length == 0) return false;
            if (!Contains(ENCLOSURE_KINDS, kindA) || !Contains(ENCLOSURE_KINDS, kindB)) return false;
            // One authored list naming the other kind (or its wall_edge alias) is enough. Mutual membership
            // rejects v0 wall_end<->portal_edge and wall_end<->inner_corner because portals/corners list
            // wall_edge, not wall_end. Same-kind still fails when the list omits that kind (inner_corner_vertex).
            bool acceptsB = ListAccepts(EffectiveCompatibleKinds(socketA, kindA), kindB);
            bool acceptsA = ListAccepts(EffectiveCompatibleKinds(socketB, kindB), kindA);
            return acceptsB || acceptsA;
        }

        /// <summary>
        /// <c>placement_position + local_position.rotated(Vector3.UP, deg_to_rad(yaw_degrees))</c>, with Godot's
        /// float32 Basis(axis, angle) * Vector3 reproduced by <see cref="ProcgenMath.RotatedUpDegrees"/>.
        /// </summary>
        public Vec3 WorldSocketPosition(Vec3 placementPosition, double yawDegrees, Vec3 localPosition)
        {
            Vec3 rotated = ProcgenMath.RotatedUpDegrees(localPosition, yawDegrees);
            return placementPosition + rotated;
        }

        public bool PositionsAgree(Vec3 worldA, Vec3 worldB)
        {
            if ((double)worldA.DistanceTo(worldB) <= POSITION_EPSILON_M) return true;
            Vec3 snapA = SnapToGrid(worldA);
            Vec3 snapB = SnapToGrid(worldB);
            return IsEqualApproxF(snapA, snapB);
        }

        public Vec3 SocketLocalPosition(GdDict socket) =>
            ToVec3(socket.Get("position_m", socket.Get("position", new GdArray())));

        // --- Internal helpers ---

        bool LoadKitDirectory(string loadKitId)
        {
            string dirPath = ProcgenCompat.PathJoin(CONTRACTS_ROOT, loadKitId);
            if (!ProcgenCompat.ResDirExists(dirPath)) return false;
            var names = new List<string>();
            foreach (string entry in ProcgenCompat.ListResFiles(dirPath))
                if (entry.EndsWith("_contract.json", System.StringComparison.Ordinal)) names.Add(entry);
            ProcgenCompat.SortStrings(names);
            foreach (string fileName in names) LoadContract(ProcgenCompat.PathJoin(dirPath, fileName));
            if (names.Count > 0) KitId = loadKitId;
            return !Modules.IsEmpty;
        }

        void LoadContract(string path)
        {
            if (!CatalogRegistry.Exists(path)) return;
            GdDict contract = CatalogRegistry.LoadDict(path);
            if (contract == null) return;
            string moduleId = V.Str(contract.Get("module_id", contract.Get("asset_id", "")));
            if (moduleId.Length == 0)
            {
                if (contract.Get("asset") is GdDict asset)
                    moduleId = V.Str(asset.Get("module_id", asset.Get("id", "")));
            }
            if (moduleId.Length == 0) return;
            GdArray sockets = SocketsOfContract(contract);
            Modules[moduleId] = new GdDict
            {
                { "module_id", moduleId },
                { "module_family", V.Str(contract.Get("module_family", "")) },
                { "kit_id", V.Str(contract.Get("kit_id", KitId)) },
                { "sockets", sockets },
                { "path", path },
            };
        }

        static GdArray SocketsOfContract(GdDict contract)
        {
            if (contract.Get("sockets") is GdArray sockets && !sockets.IsEmpty) return sockets.DeepCopy();
            if (contract.Get("asset") is GdDict asset)
            {
                if (asset.Get("sockets", new GdArray()) is GdArray nested) return nested.DeepCopy();
            }
            return new GdArray();
        }

        static GdArray CompatibleKinds(GdDict socket)
        {
            if (!(socket.Get("compatible_kinds", new GdArray()) is GdArray kinds)) return new GdArray();
            return kinds;
        }

        static GdArray EffectiveCompatibleKinds(GdDict socket, string kind)
        {
            GdArray compatible = CompatibleKinds(socket);
            if (compatible.IsEmpty) return GdArray.Of(kind);
            return compatible;
        }

        static bool ListAccepts(GdArray compatible, string otherKind)
        {
            if (compatible.Contains(otherKind)) return true;
            if (!Contains(WALL_JOIN_KINDS, otherKind)) return false;
            foreach (string joinKind in WALL_JOIN_KINDS)
                if (compatible.Contains(joinKind)) return true;
            return false;
        }

        static bool Contains(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return true;
            return false;
        }

        /// <summary><c>Vector3(snappedf(x, 4), snappedf(y, 4), snappedf(z, 4))</c>: double snap, float32 store.</summary>
        static Vec3 SnapToGrid(Vec3 value) => new Vec3(
            GdMath.Snapped(value.X, GRID_SNAP_M),
            GdMath.Snapped(value.Y, GRID_SNAP_M),
            GdMath.Snapped(value.Z, GRID_SNAP_M));

        /// <summary><c>Vector3.is_equal_approx</c>: per-component <c>Math::is_equal_approx(float, float)</c>.</summary>
        static bool IsEqualApproxF(Vec3 a, Vec3 b) =>
            IsEqualApproxF(a.X, b.X) && IsEqualApproxF(a.Y, b.Y) && IsEqualApproxF(a.Z, b.Z);

        static bool IsEqualApproxF(float a, float b)
        {
            if (a == b) return true;
            float tolerance = (float)((float)GdMath.CmpEpsilon * System.Math.Abs(a));
            if (tolerance < (float)GdMath.CmpEpsilon) tolerance = (float)GdMath.CmpEpsilon;
            return System.Math.Abs((float)(a - b)) < tolerance;
        }

        static Vec3 ToVec3(object raw)
        {
            if (raw is Vec3 v) return v;
            if (!(raw is GdArray arr)) return Vec3.Zero;
            if (arr.Count < 3) return Vec3.Zero;
            return new Vec3(V.F64(arr[0]), V.F64(arr[1]), V.F64(arr[2]));
        }
    }
}
