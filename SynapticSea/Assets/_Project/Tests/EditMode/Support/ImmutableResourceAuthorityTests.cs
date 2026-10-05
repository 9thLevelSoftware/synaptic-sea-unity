using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.EditMode.Services
{
    [NonParallelizable]
    public sealed class ImmutableResourceAuthorityTests
    {
        IResourceReader _previous;
        [SetUp] public void Before() { _previous = CoreServices.Resources; CatalogRegistry.Clear(); }
        [TearDown] public void After() { CoreServices.Resources = _previous; CatalogRegistry.Clear(); }
        static ImmutableResourceAuthority Authority(string text = "{\"nested\":{\"value\":1}}") =>
            new ImmutableResourceAuthority(new Dictionary<string,string> { { "res://data/a.json", text }, { "res://data/missing.json", null } },
                new Dictionary<string,IReadOnlyList<string>> { { "res://data", new[] { "a.json" } }, { "res://absent", null } });

        [Test] public void ConstructorAndEveryParsedReadOwnTheirBytesAndNestedValues()
        {
            var texts = new Dictionary<string,string> { { "res://data/a.json", "{\"nested\":{\"value\":1}}" } };
            var names = new[] { "a.json" };
            var directories = new Dictionary<string,IReadOnlyList<string>> { { "res://data", names } };
            var authority = new ImmutableResourceAuthority(texts, directories);
            string hash = authority.ContentSha256;
            texts["res://data/a.json"] = "{}"; names[0] = "forged.json"; directories.Clear();
            ResourceAuthorityPublication.Publish(authority);
            var first = CatalogRegistry.LoadDict("res://data/a.json", copy:false);
            first.GetDictOrEmpty("nested")["value"] = 99L;
            Assert.AreEqual(1, CatalogRegistry.LoadDict("res://data/a.json", copy:false).GetDictOrEmpty("nested").GetInt("value"));
            ((string[])authority.ListFiles("res://data"))[0] = "mutated.json";
            Assert.AreEqual("a.json", authority.ListFiles("res://data")[0]);
            Assert.AreEqual(hash, authority.ContentSha256);
            Assert.AreEqual("{\"nested\":{\"value\":1}}", authority.ReadText("res://data/a.json"));
        }

        [Test] public void ReplacementsInvalidateWhileIsolatedSnapshotRemainsExact()
        {
            var a = Authority(); ResourceAuthorityPublication.Publish(a);
            Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var lease, out _)); Assert.IsTrue(lease.IsCurrent);
            var b = Authority("{\"nested\":{\"value\":2}}"); ResourceAuthorityPublication.Publish(b);
            Assert.IsFalse(lease.IsCurrent); Assert.AreEqual(1, ((GdDict)lease.Snapshot.Load("res://data/a.json")).GetDictOrEmpty("nested").GetInt("value"));
            Assert.AreEqual(2, CatalogRegistry.LoadDict("res://data/a.json").GetDictOrEmpty("nested").GetInt("value"));
            Assert.AreNotEqual(a.ContentSha256, b.ContentSha256);
            Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var next, out _)); Assert.Greater(next.Version, lease.Version);
        }

        [Test] public void CacheClearAndEvenSameReaderSetterInvalidateTokens()
        {
            var authority = Authority(); CoreServices.Resources = authority;
            Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var before, out _));
            CatalogRegistry.Clear(); Assert.IsFalse(before.IsCurrent);
            Assert.IsTrue(ResourceAuthorityPublication.TryAcquire(out var afterClear, out _));
            CoreServices.Resources = authority; Assert.IsFalse(afterClear.IsCurrent);
            Assert.AreEqual(1, CatalogRegistry.LoadDict("res://data/a.json").GetDictOrEmpty("nested").GetInt("value"));
        }

        sealed class MutableReader : IResourceReader
        {
            public string Text = "{\"value\":1}";
            public bool Exists(string path) => true;
            public string ReadText(string path) => Text;
        }
        [Test] public void UnversionedReaderCannotObtainLeaseBeforeOrAfterInPlaceMutation()
        {
            var reader = new MutableReader(); CoreServices.Resources = reader;
            Assert.IsFalse(ResourceAuthorityPublication.TryAcquire(out var lease, out string reason)); Assert.IsNull(lease);
            Assert.AreEqual("unversioned_mutable_resource_reader", reason);
            reader.Text = "{\"value\":2}";
            Assert.AreEqual(reader.Text, CoreServices.Resources.ReadText("res://anything"));
            Assert.IsFalse(ResourceAuthorityPublication.TryAcquire(out lease, out _));
        }

        [Test] public void LegacyMutableRegistrySemanticsRemainUnchanged()
        {
            CoreServices.Resources = new MutableReader(); CatalogRegistry.Clear();
            CatalogRegistry.LoadDict("res://a", copy:false)["value"] = 7L;
            Assert.AreEqual(7, CatalogRegistry.LoadDict("res://a", copy:false).GetInt("value"));
            var detached = CatalogRegistry.LoadDict("res://a"); detached["value"] = 8L;
            Assert.AreEqual(7, CatalogRegistry.LoadDict("res://a").GetInt("value"));
        }

        [Test] public void ClosedSnapshotRefusesUndeclaredDependenciesAndRetainsDeclaredAbsence()
        {
            var authority = Authority();
            Assert.Throws<InvalidOperationException>(() => authority.ReadText("res://other.json"));
            Assert.Throws<InvalidOperationException>(() => authority.Exists("res://other.json"));
            Assert.Throws<InvalidOperationException>(() => authority.Load("res://other.json"));
            Assert.Throws<InvalidOperationException>(() => authority.ListFiles("res://other"));
            Assert.Throws<InvalidOperationException>(() => authority.DirExists("res://other"));
            Assert.IsFalse(authority.Exists("res://data/missing.json")); Assert.IsNull(authority.ReadText("res://data/missing.json"));
            Assert.IsFalse(authority.DirExists("res://absent")); Assert.IsEmpty(authority.ListFiles("res://absent"));
        }

        [Test] public void DirectoryMembershipAndDeclaredAbsenceParticipateInIdentity()
        {
            var text = new Dictionary<string,string> { { "res://data/a.json", "{}" } };
            var a = new ImmutableResourceAuthority(text);
            var b = new ImmutableResourceAuthority(text, new Dictionary<string,IReadOnlyList<string>> { { "res://data", new[] { "a.json" } } });
            Assert.AreNotEqual(a.ContentSha256, b.ContentSha256);
            text["res://missing.json"] = null;
            Assert.AreNotEqual(a.ContentSha256, new ImmutableResourceAuthority(text).ContentSha256);
            Assert.Throws<ArgumentException>(() => new ImmutableResourceAuthority(text,
                new Dictionary<string,IReadOnlyList<string>> { { "res://data", new[] { "forged.json" } } }));
            Assert.Throws<ArgumentException>(() => new ImmutableResourceAuthority(text,
                new Dictionary<string,IReadOnlyList<string>> { { "res://data", new string[0] } }));
        }

        [Test] public void PureWorkerSnapshotValidationHasNoGlobalLoggerOrReaderDependency()
        {
            var authority = Authority("invalid-json");
            var previousLog = CoreServices.Log; var log = new CollectingLog(); CoreServices.Log = log;
            try
            {
                CoreServices.Resources = new MutableReader();
                var result = Task.Run(() => { Assert.IsNull(authority.Load("res://data/a.json")); return authority.ReadText("res://data/a.json"); });
                Assert.IsTrue(result.Wait(TimeSpan.FromSeconds(5))); Assert.AreEqual("invalid-json", result.Result);
                Assert.IsEmpty(log.Errors); Assert.IsEmpty(log.Warnings);
                Assert.Contains("malformed_resource:res://data/a.json", (System.Collections.ICollection)authority.Diagnostics());
            }
            finally { CoreServices.Log = previousLog; }
        }
    }
}
