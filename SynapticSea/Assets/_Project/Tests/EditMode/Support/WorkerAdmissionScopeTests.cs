using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Tests.Systems;

namespace SynapticSea.Tests.Session
{
    public sealed class WorkerAdmissionScopeTests : InfraDataTestBase
    {
        static ImmutableResourceAuthority Authority(string value) => new ImmutableResourceAuthority(new Dictionary<string,string> {
            { "res://diagnostic/value.json", "{\"value\":\"" + value + "\"}" }, { "res://diagnostic/missing.json", null } });
        static ResourceAuthorityLease Publish(ImmutableResourceAuthority authority)
        { ResourceAuthorityPublication.Publish(authority); Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var lease, out _)); return lease; }
        sealed class SpyLog : ILog
        {
            internal int Calls;
            public void Info(string message) { Calls++; }
            public void Warning(string message) { Calls++; }
            public void Error(string message) { Calls++; }
        }
        [Test]
        public void EveryAmbientPortAndGlobalMutationRefusesBeforeTouchAndLegacyDefaultsReturnAfterScope()
        {
            var oldClock = CoreServices.Clock; var oldStorage = CoreServices.UserStorage; var oldEngine = CoreServices.Engine;
            var oldVersion = CoreServices.ProjectVersion; var oldLog = CoreServices.Log; var log = new SpyLog(); CoreServices.Log = log;
            var lease = Publish(Authority("one"));
            try
            {
                using (new PinnedAdmissionResourceScope(lease))
                {
                    Assert.Throws<UnsupportedWorkerPortException>(() => { var ignored = CoreServices.Clock; });
                    Assert.Throws<UnsupportedWorkerPortException>(() => CoreServices.Clock = null);
                    Assert.Throws<UnsupportedWorkerPortException>(() => { var ignored = CoreServices.Log; });
                    Assert.Throws<UnsupportedWorkerPortException>(() => CoreServices.Log = null);
                    Assert.Throws<UnsupportedWorkerPortException>(() => { var ignored = CoreServices.UserStorage; });
                    Assert.Throws<UnsupportedWorkerPortException>(() => CoreServices.UserStorage = null);
                    Assert.Throws<UnsupportedWorkerPortException>(() => { var ignored = CoreServices.Engine; });
                    Assert.Throws<UnsupportedWorkerPortException>(() => CoreServices.Engine = null);
                    Assert.Throws<UnsupportedWorkerPortException>(() => { var ignored = CoreServices.ProjectVersion; });
                    Assert.Throws<UnsupportedWorkerPortException>(() => CoreServices.ProjectVersion = "changed");
                    Assert.Throws<UnsupportedWorkerPortException>(() => CoreServices.Resources = null);
                    Assert.Throws<UnsupportedWorkerPortException>(() => ResourceAuthorityPublication.Publish(Authority("two")));
                    Assert.Throws<UnsupportedWorkerPortException>(() => ResourceAuthorityPublication.ReplaceReader(null));
                    Assert.Throws<UnsupportedWorkerPortException>(() => ResourceAuthorityPublication.Invalidate());
                    Assert.Throws<UnsupportedWorkerPortException>(() => CatalogRegistry.Clear());
                    Assert.Throws<UnsupportedWorkerPortException>(() => { var ignored = ResourceAuthorityPublication.Reader; });
                    Assert.Throws<UnsupportedWorkerPortException>(() => ResourceAuthorityPublication.TryAcquire(out _, out _));
                    Assert.IsTrue(lease.IsCurrent, "refused setters/cache clear cannot invalidate the lease");
                }
                Assert.AreSame(oldClock, CoreServices.Clock); Assert.AreSame(oldStorage, CoreServices.UserStorage);
                Assert.AreSame(oldEngine, CoreServices.Engine); Assert.AreEqual(oldVersion, CoreServices.ProjectVersion);
                Assert.AreSame(log, CoreServices.Log); Assert.AreEqual(0, log.Calls);
            }
            finally { CoreServices.Log = oldLog; }
        }
        [Test]
        public void PinnedReadsIgnoreReplacementButGlobalFreshnessAndUndeclaredRefusalRemainExact()
        {
            var first = Authority("first"); var lease = Publish(first); var replacement = Authority("second");
            var log = new SpyLog(); var oldLog = CoreServices.Log; CoreServices.Log = log;
            try
            {
                using (new PinnedAdmissionResourceScope(lease))
                {
                    var change = new Thread(() => ResourceAuthorityPublication.Publish(replacement)); change.Start(); Assert.IsTrue(change.Join(1000));
                    Assert.AreSame(first, CoreServices.Resources); Assert.IsFalse(lease.IsCurrent);
                    Assert.AreEqual("first", CatalogRegistry.LoadDict("res://diagnostic/value.json", copy:false).Get("value"));
                    Assert.IsFalse(CatalogRegistry.Exists("res://diagnostic/missing.json")); Assert.IsNull(CatalogRegistry.Load("res://diagnostic/missing.json"));
                    Assert.Throws<InvalidOperationException>(() => CatalogRegistry.Exists("res://undeclared.json"));
                    Assert.Throws<InvalidOperationException>(() => CatalogRegistry.Load("res://undeclared.json"));
                }
                Assert.AreSame(replacement, CoreServices.Resources); Assert.AreEqual(0, log.Calls);
            }
            finally { CoreServices.Log = oldLog; }
        }
        [Test]
        public void NestedScopeExceptionOrderAndWrongThreadDisposalCannotLeakOrReplaceAnotherThreadsReader()
        {
            var first = Publish(Authority("first")); var second = Publish(Authority("second"));
            var outer = new PinnedAdmissionResourceScope(first); var inner = new PinnedAdmissionResourceScope(second);
            Assert.AreSame(second.Snapshot, CoreServices.Resources);
            Assert.Throws<InvalidOperationException>(() => outer.Dispose());
            Exception wrong = null; IResourceReader other = null; bool inheritedScope = true;
            var thread = new Thread(() => { inheritedScope = PinnedAdmissionResourceScope.ReaderOrNull != null; other = CoreServices.Resources; try { inner.Dispose(); } catch (Exception error) { wrong = error; } });
            thread.Start(); Assert.IsTrue(thread.Join(1000)); Assert.IsInstanceOf<InvalidOperationException>(wrong);
            Assert.IsFalse(inheritedScope);
            Assert.AreSame(second.Snapshot, other, "other thread reads global publication, never the ThreadStatic outer scope");
            inner.Dispose(); Assert.AreSame(first.Snapshot, CoreServices.Resources);
            try { using (new PinnedAdmissionResourceScope(second)) throw new ArgumentException("diagnostic"); }
            catch (ArgumentException) { }
            Assert.AreSame(first.Snapshot, CoreServices.Resources); outer.Dispose(); outer.Dispose();
            Assert.IsNull(PinnedAdmissionResourceScope.ReaderOrNull); Assert.AreSame(second.Snapshot, CoreServices.Resources);
        }
    }
}
