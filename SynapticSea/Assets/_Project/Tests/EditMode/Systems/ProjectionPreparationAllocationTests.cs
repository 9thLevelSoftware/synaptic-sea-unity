#if SYNAPTIC_DOTNET_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Systems
{
    [NonParallelizable]
    public sealed class ProjectionPreparationAllocationTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(31)]
        public void HeightMemoPreallocatesCandidateCountAndDoesNotGrowDuringAdvance(int count)
        {
            using var registry = new ParticipantProjectionRegistry();
            var handles = new ProjectionNodeHandle[count];
            var versions = new ProjectionNodeVersion[count];
            for (int i = 0; i < count; i++)
                Assert.True(registry.TryCreateNode(ProjectionNodeKind.Dictionary, out handles[i], out _));
            for (int i = 0; i < count; i++)
            {
                var entries = i + 1 < count ? new[] { new ProjectionEntry(ProjectionScalar.FromNormalized("child"), new ProjectionValue(handles[i + 1].Id)) } : Array.Empty<ProjectionEntry>();
                Assert.True(handles[i].TryCreateVersion(entries, out versions[i], out _));
            }
            var roots = count == 0 ? Array.Empty<ProjectionRootBinding>() : new[] { new ProjectionRootBinding("root", handles[0].Id) };
            var descriptor = new ProjectionRootDescriptor("allocation-diagnostic", "synthetic-policy", 1, roots);
            Assert.True(registry.TryPrepare(versions, null, descriptor, out var cursor, out var reason), reason);
            using (cursor)
            {
                var memo = (Dictionary<ProjectionNodeId, int>)typeof(ProjectionPreparationCursor)
                    .GetField("_heights", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cursor);
                int initialCapacity = memo.EnsureCapacity(0);
                Assert.GreaterOrEqual(initialCapacity, count);
                Assert.Less(initialCapacity, 64); // one-node preparation cannot reserve the 4096-node maximum
                while (cursor.Status == ProjectionCursorStatus.Pending)
                {
                    cursor.Advance(1);
                    Assert.LessOrEqual(cursor.LastWorkUnits, 1);
                    Assert.AreEqual(initialCapacity, memo.EnsureCapacity(0));
                }
                Assert.AreEqual(ProjectionCursorStatus.Complete, cursor.Status, cursor.Reason);
                Assert.AreEqual(count, memo.Count);
                Assert.True(registry.TryPublish(cursor, out reason), reason);
            }
        }
    }
}
#endif
