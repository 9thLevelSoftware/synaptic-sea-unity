using System;
using System.Collections.Generic;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;

namespace SynapticSea.Core.Variant
{
    internal readonly struct ParticipantInitialQuantity
    {
        internal readonly string ItemId;
        internal readonly long Quantity;
        internal ParticipantInitialQuantity(string itemId, long quantity)
        { if (string.IsNullOrEmpty(itemId) || itemId.Length > 128 || quantity < 1) throw new ArgumentException("invalid_initial_quantity"); ItemId = itemId; Quantity = quantity; }
    }
    internal sealed class PreparedParticipantProjectionCohort : IDisposable
    {
        ParticipantProjectionCohort _cohort;
        bool _consumed;
        internal PreparedParticipantProjectionCohort(ParticipantProjectionCohort cohort) { _cohort = cohort; }
        internal bool TryPublish(out ParticipantProjectionCohort cohort, out string reason)
        {
            lock (CommonParticipantGate.SyncRoot)
            {
                cohort = null;
                if (_consumed || _cohort == null || !_cohort.PublishUnderGate()) { reason = "stale_or_consumed_participant_factory"; return false; }
                cohort = _cohort; _cohort = null; _consumed = true; reason = ""; return true;
            }
        }
        public void Dispose() { lock (CommonParticipantGate.SyncRoot) { var owned = _cohort; _cohort = null; _consumed = true; owned?.Dispose(); } }
    }
    internal static class ParticipantProjectionFactory
    {
        internal static System.Collections.Generic.IReadOnlyList<string> RequiredResourcePaths => ParticipantProjectionCohort.RequiredResourcePaths;
        internal static PreparedParticipantProjectionCohort Prepare(ResourceAuthorityLease lease, string classId, ProjectionLimits limits = null, ParticipantInitialQuantity[] initialQuantities = null)
            => ParticipantProjectionCohort.PrepareFromSource(lease, classId, limits, initialQuantities);
    }
    internal sealed partial class ParticipantProjectionCohort
    {
        static readonly string[] RequiredPaths = {
            ItemDefs.TOOL_DEFINITIONS_PATH, ItemDefs.ITEM_DEFINITIONS_PATH,
            ItemDefs.MEDICINE_DEFINITIONS_PATH, ItemDefs.STIMULANT_DEFINITIONS_PATH, ItemDefs.AMMO_DEFINITIONS_PATH,
            ItemDefs.UTILITY_DEFINITIONS_PATH, ItemDefs.TRADE_DEFINITIONS_PATH, ItemDefs.MATERIAL_DEFINITIONS_PATH,
            ItemDefs.EQUIPMENT_DEFINITIONS_PATH, ItemDefs.JUNK_ITEMS_PATH, ItemDefs.UNIQUE_ITEMS_PATH,
            ClassDefinition.DefaultClassesPath, PlayerProgressionState.DEFAULT_SKILLS_PATH,
            PlayerProgressionState.DEFAULT_BOOKS_PATH, TrainingEventBus.DEFAULT_TRAINING_ACTIONS_PATH };
        internal static IReadOnlyList<string> RequiredResourcePaths { get; } = Array.AsReadOnly(RequiredPaths);
        internal static PreparedParticipantProjectionCohort PrepareFromSource(ResourceAuthorityLease lease, string classId,
            ProjectionLimits limits = null, ParticipantInitialQuantity[] initialQuantities = null)
        {
            if (lease == null || string.IsNullOrEmpty(classId) || classId.Length > 128 || initialQuantities != null && initialQuantities.Length > 128)
                throw new ArgumentException("participant_factory_input_capacity");
            if (!lease.IsCurrent) throw new InvalidOperationException("stale_participant_factory_lease");
            limits = limits ?? new ProjectionLimits();
            var rows = initialQuantities == null ? Array.Empty<ParticipantInitialQuantity>() : (ParticipantInitialQuantity[])initialQuantities.Clone();
            foreach (var row in rows) if (string.IsNullOrEmpty(row.ItemId) || row.ItemId.Length > 128 || row.Quantity < 1) throw new ArgumentException("invalid_initial_quantity");
            var raw = new List<object>(RequiredPaths.Length);
            foreach (string path in RequiredPaths)
            {
                string text = lease.Snapshot.ReadText(path); // undeclared path refuses, declared absence is explicit
                if (text != null && text.Length > 1048576) throw new ArgumentException("participant_catalog_text_capacity");
                object parsed = lease.Snapshot.Load(path);
                if (text != null && !(parsed is GdDict)) throw new ArgumentException("malformed_participant_catalog:" + path);
                if (parsed != null) raw.Add(parsed);
            }
            CheckForestBounds(raw.ToArray(), limits);
            var definitions = ItemDefs.LoadDefinitionsFromAuthority(lease.Snapshot);
            var classesRoot = lease.Snapshot.Load(ClassDefinition.DefaultClassesPath) as GdDict;
            var skillsRoot = lease.Snapshot.Load(PlayerProgressionState.DEFAULT_SKILLS_PATH) as GdDict;
            var booksRoot = lease.Snapshot.Load(PlayerProgressionState.DEFAULT_BOOKS_PATH) as GdDict;
            var actionsRoot = lease.Snapshot.Load(TrainingEventBus.DEFAULT_TRAINING_ACTIONS_PATH) as GdDict;
            if (!(classesRoot?.Get("classes") is GdArray) || !(skillsRoot?.Get("skills") is GdArray) ||
                !(actionsRoot?.Get("training_actions") is GdArray) || booksRoot != null && !(booksRoot.Get("books") is GdArray))
                throw new ArgumentException("participant_catalog_root_shape");
            var classes = ClassDefinition.ParseAll(classesRoot);
            if (!classes.TryGetValue(classId, out var selectedClass)) throw new ArgumentException("unknown_participant_class");
            var skills = PlayerProgressionState.ParseSkills(skillsRoot);
            var books = PlayerProgressionState.ParseBooks(booksRoot);
            var inventoryReplay = new InventoryState(definitions);
            foreach (var row in rows) if (inventoryReplay.AddItem(row.ItemId, row.Quantity) != row.Quantity) throw new ArgumentException("initial_quantity_rejected");
            var progressionReplay = new PlayerProgressionState(); progressionReplay.Configure(selectedClass, skills, books);
            // Bound normalized state+policy forest BEFORE protected import.
            CheckForestBounds(new object[] { definitions, inventoryReplay.Items, progressionReplay.GetSummary(), skills, books, selectedClass.XpMultipliers, actionsRoot }, limits);
            var inventory = InventoryState.PrepareProjectionBirth(inventoryReplay, out var invOwner, out var invRoots);
            var progression = PlayerProgressionState.PrepareProjectionBirth(progressionReplay, out var progOwner, out var progRoots);
            var training = TrainingEventBus.PrepareProjectionBirth(actionsRoot, out var trainOwner, out var trainRoots);
            var allRoots = new object[12]; Array.Copy(invRoots, 0, allRoots, 0, 2); Array.Copy(progRoots, 0, allRoots, 2, 8); Array.Copy(trainRoots, 0, allRoots, 10, 2);
            CheckForestBounds(allRoots, limits);
            var owners = new[] { invOwner, progOwner, trainOwner };
            bool[] policy = { false, true, false, false, false, false, false, true, true, true, true, false };
            string[] names = { "inventory.items", "inventory.definitions", "progression.skills", "progression.xp", "progression.fractional", "progression.cross", "progression.books_read", "progression.multipliers", "progression.categories", "progression.books", "training.actions", "training.log" };
            var registry = new ParticipantProjectionRegistry(limits);
            try
            {
                var nodeList = new List<object>(); var policies = new HashSet<object>(ReferenceComparer.Instance);
                var seen = new HashSet<object>(ReferenceComparer.Instance);
                for (int i = 0; i < allRoots.Length; i++) Collect(allRoots[i], seen, nodeList);
                for (int i = 0; i < allRoots.Length; i++) if (policy[i]) CollectPolicy(allRoots[i], policies);
                var bindings = new Dictionary<object, EnrolledProjectionNode>(ReferenceComparer.Instance);
                foreach (object node in nodeList)
                {
                    var owner = node is GdDict d ? d.Owner : ((GdArray)node).Owner;
                    int ownerIndex = ReferenceEquals(owner, invOwner) ? 0 : ReferenceEquals(owner, progOwner) ? 1 : 2;
                    var kind = node is GdDict ? ProjectionNodeKind.Dictionary : ProjectionNodeKind.Array;
                    if (!registry.TryCreateNode(kind, out var handle, out string reason)) throw new InvalidOperationException(reason);
                    int count = node is GdDict map ? map.RawStorage.Keys.Count : ((GdArray)node).RawStorage.Count;
                    var state = new EnrolledProjectionNode(handle, owner, ownerIndex, policies.Contains(node), new object[count], node, node is GdDict dict ? (object)dict.RawStorage : ((GdArray)node).RawStorage);
                    bindings.Add(node, state);
                    if (node is GdDict boundMap) boundMap.ProjectionNode = state; else ((GdArray)node).ProjectionNode = state;
                }
                var versions = new List<ProjectionNodeVersion>(nodeList.Count);
                foreach (object node in nodeList)
                {
                    var state = bindings[node]; int count = state.SlotCount; var entries = new ProjectionEntry[count];
                    for (int i = 0; i < count; i++)
                    {
                        object value = node is GdDict dict ? dict.RawStorage.Values[i] : ((GdArray)node).RawStorage[i];
                        var key = node is GdDict map ? ProjectionScalar.FromNormalized(map.RawStorage.Keys[i]) : default;
                        ProjectionValue projected;
                        if (value is GdDict || value is GdArray) { state.InitializeChildSlot(i, value); projected = new ProjectionValue(bindings[value].Handle.Id); }
                        else projected = new ProjectionValue(ProjectionScalar.FromNormalized(value));
                        entries[i] = new ProjectionEntry(key, projected);
                    }
                    if (!state.Handle.TryPrepareInitialVersion(entries, out var version)) throw new InvalidOperationException("private_node_issuance_failed");
                    state.Current = version; versions.Add(version);
                }
                var roots = new ProjectionRootBinding[12]; for (int i = 0; i < roots.Length; i++) roots[i] = new ProjectionRootBinding(names[i], bindings[allRoots[i]].Handle.Id);
                var scalars = new[] { new ProjectionScalarBinding("class_id", ProjectionScalar.FromNormalized(classId)),
                    new ProjectionScalarBinding("inventory.bonus_capacity", ProjectionScalar.FromNormalized(0.0)),
                    new ProjectionScalarBinding("inventory.weight_reduction", ProjectionScalar.FromNormalized(0.0)),
                    new ProjectionScalarBinding("training.dropped", ProjectionScalar.FromNormalized(0L)),
                    new ProjectionScalarBinding("training.xp_total", ProjectionScalar.FromNormalized(0L)) };
                var cohort = new ParticipantProjectionCohort(lease, registry, inventory, progression, training, owners, roots, scalars, classId, bindings.Values);
                // Private genesis is admitted in capped32 batches; final root closure is validated by existing cursor.
                var empty = new ProjectionRootDescriptor("unpublished-private-cohort", lease.Snapshot.ContentSha256, 1, Array.Empty<ProjectionRootBinding>());
                for (int start = 0; start < versions.Count; start += 32)
                {
                    int n = Math.Min(32, versions.Count - start); var batch = new ProjectionNodeVersion[n]; versions.CopyTo(start, batch, 0, n);
                    PublishPrivate(registry, batch, empty);
                }
                PublishPrivate(registry, Array.Empty<ProjectionNodeVersion>(), cohort.Descriptor(1));
                return new PreparedParticipantProjectionCohort(cohort);
            }
            catch { registry.Dispose(); throw; }
        }
        static void PublishPrivate(ParticipantProjectionRegistry registry, ProjectionNodeVersion[] batch, ProjectionRootDescriptor descriptor)
        {
            if (!registry.TryPrepare(batch, null, descriptor, out var cursor, out var reason)) throw new InvalidOperationException(reason);
            using (cursor)
            {
                while (cursor.Status == ProjectionCursorStatus.Pending) cursor.Advance(64);
                if (cursor.Status != ProjectionCursorStatus.Complete || !registry.TryPublish(cursor, out reason)) throw new InvalidOperationException(reason);
            }
        }
        static void Collect(object value, HashSet<object> seen, List<object> nodes)
        {
            if (!(value is GdDict) && !(value is GdArray) || !seen.Add(value)) return;
            nodes.Add(value); if (value is GdDict d) foreach (object child in d.RawStorage.Values) Collect(child, seen, nodes);
            else foreach (object child in ((GdArray)value).RawStorage) Collect(child, seen, nodes);
        }
        static void CollectPolicy(object value, HashSet<object> seen)
        {
            if (!(value is GdDict) && !(value is GdArray) || !seen.Add(value)) return;
            if (value is GdDict d) foreach (object child in d.RawStorage.Values) CollectPolicy(child, seen);
            else foreach (object child in ((GdArray)value).RawStorage) CollectPolicy(child, seen);
        }
        static void CheckForestBounds(object[] roots, ProjectionLimits limits)
        {
            var heights = new Dictionary<object, int>(ReferenceComparer.Instance); var active = new HashSet<object>(ReferenceComparer.Instance);
            foreach (object root in roots) Check(root, 0, heights, active, limits);
        }
        static int Check(object value, int depth, Dictionary<object, int> heights, HashSet<object> active, ProjectionLimits limits)
        {
            if (!(value is GdDict) && !(value is GdArray)) { ProjectionScalar.FromNormalized(value); return -1; }
            if (depth > 128 || active.Contains(value)) throw new ArgumentException("participant_factory_graph_depth_or_cycle");
            if (heights.TryGetValue(value, out int cached)) { if (depth + cached > 128) throw new ArgumentException("participant_factory_graph_depth"); return cached; }
            if (heights.Count + active.Count >= limits.MaximumNodes) throw new ArgumentException("participant_factory_graph_nodes");
            active.Add(value); int height = 0;
            if (value is GdDict d)
            {
                if (d.RawStorage.Keys.Count > limits.MaximumEntriesPerNode) throw new ArgumentException("participant_factory_node_entries");
                for (int i = 0; i < d.RawStorage.Keys.Count; i++) { ProjectionScalar.FromNormalized(d.RawStorage.Keys[i]); height = Math.Max(height, Check(d.RawStorage.Values[i], depth + 1, heights, active, limits) + 1); }
            }
            else
            {
                var a = (GdArray)value; if (a.RawStorage.Count > limits.MaximumEntriesPerNode) throw new ArgumentException("participant_factory_node_entries");
                foreach (object child in a.RawStorage) height = Math.Max(height, Check(child, depth + 1, heights, active, limits) + 1);
            }
            active.Remove(value); heights.Add(value, height); if (depth + height > 128) throw new ArgumentException("participant_factory_graph_depth"); return height;
        }
    }
}
