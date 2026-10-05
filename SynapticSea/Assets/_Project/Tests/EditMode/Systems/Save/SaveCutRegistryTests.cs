using System;
using System.Threading;
using NUnit.Framework;
using SynapticSea.Core.Systems;

namespace SynapticSea.Tests.Session
{
    public sealed class SaveCutRegistryTests
    {
        static SaveCutChunk Leaf(long value) => new SaveCutChunk("value", new[] { SaveCutScalar.Int64(value) });
        static SaveCutRootUpdate Root(string id, long value) => new SaveCutRootUpdate(id, Leaf(value));
        static SaveCutRegistry Registry()
        {
            var registry = new SaveCutRegistry("session", "run", "sealed-diagnostic-lease-id");
            Assert.IsTrue(registry.TryReplaceRoots(0, new[] { Root("topology", 0), Root("inventory", 8), Root("work", 0) }, out _));
            return registry;
        }
        static long Find(SaveCutPin pin, string source)
        { for (int i = 0; i < pin.RootCount; i++) if (pin.RootAt(i).SourceId == source) return pin.RootAt(i).Chunk.ValueAt(0).Bits; throw new Exception(); }
        [Test]
        public void AtomicMultiSourceTransactionsPinCompleteBeforeOrAfterNeverMixed()
        {
            var registry = Registry(); Assert.IsTrue(registry.TryPinCut(out var before, out _));
            Assert.IsTrue(registry.TryReplaceRoots(1, new[] { Root("inventory", 7), Root("work", 1), Root("topology", 3) }, out _));
            Assert.IsTrue(registry.TryPinCut(out var after, out _));
            Assert.AreEqual(8, Find(before, "inventory")); Assert.AreEqual(0, Find(before, "work")); Assert.AreEqual(0, Find(before, "topology"));
            Assert.AreEqual(7, Find(after, "inventory")); Assert.AreEqual(1, Find(after, "work")); Assert.AreEqual(3, Find(after, "topology"));
            Assert.AreEqual(1, before.WorldRevision); Assert.AreEqual(2, after.WorldRevision);
            Assert.AreEqual(1, before.CaptureSequence); Assert.AreEqual(2, after.CaptureSequence);
            Assert.IsFalse(registry.TryReplaceRoots(1, new[] { Root("inventory", 2), Root("work", 6) }, out var reason));
            Assert.AreEqual("revision_conflict", reason); Assert.AreEqual(2, registry.WorldRevision);
            before.Release(); after.Release();
        }
        [Test]
        public void NestedOwnedValuesRemainImmutableAcrossInputMutationAndVersionReplacement()
        {
            var values = new[] { SaveCutScalar.Int64(12) }; var child = new SaveCutChunk("child", values);
            var children = new[] { child }; var tree = new SaveCutChunk("parent", new[] { SaveCutScalar.String("original") }, children);
            values[0] = SaveCutScalar.Int64(99); children[0] = Leaf(100);
            var registry = new SaveCutRegistry("s", "r", "l");
            Assert.IsTrue(registry.TryReplaceRoots(0, new[] { new SaveCutRootUpdate("topology", tree) }, out _));
            Assert.IsTrue(registry.TryPinCut(out var old, out _));
            Assert.IsTrue(registry.TryReplaceRoots(1, new[] { Root("topology", 90) }, out _));
            Assert.IsTrue(registry.TryPinCut(out var current, out _));
            Assert.AreEqual(12, old.RootAt(0).Chunk.ChildAt(0).ValueAt(0).Bits);
            Assert.AreEqual("original", old.RootAt(0).Chunk.ValueAt(0).Text);
            Assert.AreEqual(1, old.RootAt(0).Version); Assert.AreEqual(2, current.RootAt(0).Version);
            Assert.AreEqual(90, current.RootAt(0).Chunk.ValueAt(0).Bits); old.Release(); current.Release();
        }
        [Test]
        public void MutationAndPinRequireCreatingThreadAndTopologyEnrollment()
        {
            var registry = new SaveCutRegistry("s", "r", "l");
            Assert.IsFalse(registry.TryPinCut(out _, out var missing)); Assert.AreEqual("topology_not_enrolled", missing);
            bool mutated = true, pinned = true; string mutationReason = "", pinReason = "";
            var thread = new Thread(() => { mutated = registry.TryReplaceRoots(0, new[] { Root("topology", 1) }, out mutationReason); pinned = registry.TryPinCut(out _, out pinReason); });
            thread.Start(); Assert.IsTrue(thread.Join(1000));
            Assert.IsFalse(mutated); Assert.IsFalse(pinned); Assert.AreEqual("wrong_mutation_thread", mutationReason); Assert.AreEqual("wrong_mutation_thread", pinReason);
            Assert.AreEqual(0, registry.WorldRevision);
        }
        [Test]
        public void PinRootRetentionAndCancellationCapsRefuseWithoutMutationThenReleaseRestoresCapacity()
        {
            var registry = Registry(); var pins = new SaveCutPin[SaveCutRegistry.MaximumPins];
            for (int i = 0; i < pins.Length; i++) Assert.IsTrue(registry.TryPinCut(out pins[i], out _));
            Assert.IsFalse(registry.TryPinCut(out _, out var reason)); Assert.AreEqual("pin_capacity", reason);
            var cursor = new SaveCutCaptureCursor(pins[0]); Assert.IsTrue(cursor.Advance(1, out _)); cursor.Cancel();
            Assert.AreEqual(0, cursor.CopiedUnits); Assert.IsFalse(cursor.Advance(1, out reason)); Assert.AreEqual("capture_cancelled", reason);
            Assert.AreEqual(3, registry.ActivePins); Assert.IsTrue(registry.TryPinCut(out var replacement, out _)); replacement.Release(); replacement.Release();
            for (int i = 1; i < pins.Length; i++) pins[i].Release(); Assert.AreEqual(0, registry.ActivePins);
            var updates = new SaveCutRootUpdate[SaveCutRegistry.MaximumRoots + 1];
            for (int i = 0; i < updates.Length; i++) updates[i] = Root("root" + i, i);
            Assert.IsFalse(registry.TryReplaceRoots(registry.WorldRevision, updates, out reason)); Assert.AreEqual("invalid_transaction", reason);
            var capacity = new SaveCutRegistry("s", "r", "l");
            var full = new SaveCutRootUpdate[SaveCutRegistry.MaximumRoots];
            // 21-node immutable trees give 336 live nodes: the first additional pin conservatively exceeds 512.
            var small = new SaveCutChunk("branch", Array.Empty<SaveCutScalar>(), new[] { Leaf(1), Leaf(2), Leaf(3), Leaf(4) });
            var big = new SaveCutChunk("tree", Array.Empty<SaveCutScalar>(), new[] { small, small, small, small });
            for (int i = 0; i < full.Length; i++) full[i] = new SaveCutRootUpdate(i == 0 ? "topology" : "source" + i, big);
            Assert.IsTrue(capacity.TryReplaceRoots(0, full, out _));
            Assert.IsFalse(capacity.TryPinCut(out _, out reason)); Assert.AreEqual("retention_capacity", reason); Assert.AreEqual(0, capacity.ActivePins);
            Assert.IsFalse(capacity.TryReplaceRoots(1, new[] { Root("overflow", 1) }, out reason)); Assert.AreEqual("root_capacity", reason); Assert.AreEqual(1, capacity.WorldRevision);
        }
        [Test]
        public void RetainedVersionCapacityRefusesReplacementAtomicallyUntilPinReleased()
        {
            var small = new SaveCutChunk("branch", Array.Empty<SaveCutScalar>(), new[] { Leaf(1), Leaf(2), Leaf(3), Leaf(4) });
            var big = new SaveCutChunk("tree", Array.Empty<SaveCutScalar>(), new[] { small, small, small, small });
            var four = new SaveCutChunk("four", Array.Empty<SaveCutScalar>(), new[] { Leaf(1), Leaf(2), Leaf(3) });
            var maximum = new SaveCutChunk("maximum", Array.Empty<SaveCutScalar>(), new[] { big, small, Leaf(0), four });
            Assert.AreEqual(32, maximum.TreeNodes);
            var registry = new SaveCutRegistry("s", "r", "l"); var roots = new SaveCutRootUpdate[8];
            for (int i = 0; i < roots.Length; i++) roots[i] = new SaveCutRootUpdate(i == 0 ? "topology" : "source" + i, big);
            Assert.IsTrue(registry.TryReplaceRoots(0, roots, out _));
            Assert.IsTrue(registry.TryPinCut(out var first, out _)); Assert.IsTrue(registry.TryPinCut(out var second, out _));
            Assert.IsFalse(registry.TryReplaceRoots(1, new[] { new SaveCutRootUpdate("topology", maximum) }, out var reason));
            Assert.AreEqual("retention_capacity", reason); Assert.AreEqual(1, registry.WorldRevision);
            Assert.AreEqual(21, first.RootAt(0).Chunk.TreeNodes);
            first.Release();
            Assert.IsTrue(registry.TryReplaceRoots(1, new[] { new SaveCutRootUpdate("topology", maximum) }, out _));
            Assert.AreEqual(2, registry.WorldRevision); Assert.AreEqual(21, second.RootAt(0).Chunk.TreeNodes); second.Release();
        }

        [Test]
        public void BudgetedDetachedCapturePreservesExactTypedBitsPreorderAndSourceVersions()
        {
            var minusZero = BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL));
            var bits = BitConverter.Int64BitsToDouble(0x3fa81f8b6a300d00L);
            var scalars = new[] { SaveCutScalar.Float64(minusZero), SaveCutScalar.Float32(BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0)), SaveCutScalar.Float64(bits),
                SaveCutScalar.Int64(long.MaxValue), SaveCutScalar.String("tail"), SaveCutScalar.Boolean(true), SaveCutScalar.Null() };
            Assert.AreEqual(unchecked((long)0x8000000000000000UL), scalars[0].Bits);
            Assert.AreEqual(unchecked((int)0x80000000), scalars[1].Bits);
            Assert.AreEqual(0x3fa81f8b6a300d00L, scalars[2].Bits);
            var tree = new SaveCutChunk("root", scalars, new[] { new SaveCutChunk("a", new[] { SaveCutScalar.Int64(1) }), new SaveCutChunk("b", new[] { SaveCutScalar.Int64(2) }) });
            var registry = new SaveCutRegistry("s", "r", "lease"); Assert.IsTrue(registry.TryReplaceRoots(0, new[] { new SaveCutRootUpdate("topology", tree), Root("work", 4) }, out _));
            Assert.IsTrue(registry.TryPinCut(out var pin, out _)); var cursor = new SaveCutCaptureCursor(pin);
            Assert.IsFalse(cursor.Advance(0, out _)); Assert.AreEqual(0, cursor.CopiedUnits);
            Assert.IsTrue(cursor.Advance(1, out _)); Assert.AreEqual(1, cursor.CopiedUnits); Assert.IsFalse(cursor.Complete);
            Assert.IsTrue(registry.TryReplaceRoots(1, new[] { Root("work", 999) }, out _));
            for (int i = 0; i < 3; i++) Assert.IsTrue(cursor.Advance(1, out _));
            Assert.IsTrue(cursor.Complete); Assert.AreEqual(0, registry.ActivePins);
            var payload = cursor.Payload; Assert.AreEqual(1, payload.WorldRevision); Assert.AreEqual("lease", payload.ResourceLeaseId);
            Assert.AreEqual(4, payload.UnitCount); Assert.AreEqual(2, payload.UnitAt(0).ChildCount);
            for (int i = 0; i < scalars.Length; i++) { Assert.AreEqual(scalars[i].Kind, payload.UnitAt(0).Leaf.ValueAt(i).Kind); Assert.AreEqual(scalars[i].Bits, payload.UnitAt(0).Leaf.ValueAt(i).Bits); Assert.AreEqual(scalars[i].Text, payload.UnitAt(0).Leaf.ValueAt(i).Text); }
            Assert.AreEqual("a", payload.UnitAt(1).Leaf.Label); Assert.AreEqual("b", payload.UnitAt(2).Leaf.Label);
            Assert.AreEqual(0, payload.UnitAt(0).TreeOrdinal); Assert.AreEqual(1, payload.UnitAt(1).TreeOrdinal); Assert.AreEqual(2, payload.UnitAt(2).TreeOrdinal);
            Assert.AreEqual("work", payload.UnitAt(3).SourceId); Assert.AreEqual(1, payload.UnitAt(3).SourceVersion); Assert.AreEqual(4, payload.UnitAt(3).Leaf.ValueAt(0).Bits);
            Assert.IsTrue(cursor.Advance(1, out _)); cursor.Cancel(); Assert.AreSame(payload, cursor.Payload);
        }
        [Test]
        public void TwoSingleThreadedCursorsSharingPinCannotCopyAfterFirstReleasesIt()
        {
            var registry = Registry(); Assert.IsTrue(registry.TryPinCut(out var pin, out _));
            var first = new SaveCutCaptureCursor(pin); var second = new SaveCutCaptureCursor(pin);
            Assert.IsTrue(second.Advance(1, out _)); Assert.AreEqual(1, second.CopiedUnits);
            Assert.IsTrue(first.Advance(32, out _)); Assert.IsTrue(first.Complete); Assert.AreEqual(0, registry.ActivePins);
            Assert.IsFalse(second.Advance(1, out var reason)); Assert.AreEqual("capture_cancelled", reason);
            Assert.IsNull(second.Payload); second.Cancel(); Assert.AreEqual(0, second.CopiedUnits);
            Assert.Throws<InvalidOperationException>(() => pin.RootAt(0));
        }

        [Test]
        public void OtherThreadReleaseBetweenCursorAdvancesStopsCaptureWithoutPartialPayload()
        {
            var registry = Registry(); Assert.IsTrue(registry.TryPinCut(out var pin, out _));
            var cursor = new SaveCutCaptureCursor(pin); Assert.IsTrue(cursor.Advance(1, out _));
            var release = new Thread(pin.Release); release.Start(); Assert.IsTrue(release.Join(1000));
            Assert.IsFalse(cursor.Advance(1, out var reason)); Assert.AreEqual("capture_cancelled", reason);
            Assert.IsNull(cursor.Payload); Assert.IsFalse(cursor.Complete); Assert.AreEqual(0, registry.ActivePins);
            cursor.Cancel(); Assert.AreEqual(0, cursor.CopiedUnits);
        }

        [Test]
        public void OversizedUnitsRefuseBeforePinAndReleasedPinsCannotContinueCopying()
        {
            Assert.Throws<ArgumentException>(() => new SaveCutRegistry(new string(' ', 129), "run", "lease"));
            Assert.Throws<ArgumentException>(() => new SaveCutRootUpdate(new string(' ', 129), Leaf(0)));
            Assert.Throws<ArgumentException>(() => SaveCutScalar.String(new string('x', SaveCutChunk.MaximumTextLength + 1)));
            Assert.Throws<ArgumentException>(() => new SaveCutChunk("large", new SaveCutScalar[SaveCutChunk.MaximumScalars + 1]));
            var deep = Leaf(1);
            for (int i = 1; i < SaveCutChunk.MaximumDepth; i++) deep = new SaveCutChunk("parent", Array.Empty<SaveCutScalar>(), new[] { deep });
            Assert.Throws<ArgumentException>(() => new SaveCutChunk("too_deep", Array.Empty<SaveCutScalar>(), new[] { deep }));
            var registry = Registry(); Assert.IsTrue(registry.TryPinCut(out var pin, out _)); var cursor = new SaveCutCaptureCursor(pin); pin.Release();
            Assert.IsFalse(cursor.Advance(1, out var reason)); Assert.AreEqual("capture_cancelled", reason); Assert.AreEqual(0, cursor.CopiedUnits);
        }
    }
}
