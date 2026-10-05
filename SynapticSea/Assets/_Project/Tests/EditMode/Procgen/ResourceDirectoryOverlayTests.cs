using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.EditMode.Procgen
{
    public sealed class ResourceDirectoryOverlayTests
    {
        IResourceReader _original;
        [SetUp] public void Setup() { _original=CoreServices.Resources;CoreServices.Resources=new FileSystemResourceReader(Fixtures.StreamingDataRoot);CatalogRegistry.Clear(); }
        [TearDown] public void Cleanup() { CoreServices.Resources=_original;CatalogRegistry.Clear(); }
        static IResourceReader Overlay(IResourceReader fallback,params KeyValuePair<string,string>[] files)
        {
            var artifacts=new GdArray();foreach(var file in files) artifacts.Append(new GdDict { {"logical_path",file.Key},{"text",file.Value} });
            var payload=new GdDict { {"artifacts",artifacts} };
            var selection=new GdDict { {"ok",true},{"payloads",payload},{"payloads_sha256",SaveGenerationArtifacts.Hash(GdJson.Stringify(payload))} };
            Assert.IsTrue(SaveGenerationArtifacts.TryCreateReader(selection,fallback,out var reader,out var reason),reason);return reader;
        }
        [Test]
        public void ArchiveDirectoryCapabilityPreservesCanonicalProfileAndRestore()
        {
            var original=CoreServices.Resources;
            string dir=ModularSocketCatalog.CONTRACTS_ROOT+ModularSocketCatalog.DEFAULT_KIT_ID;
            var names=ProcgenCompat.ListResFiles(dir);Assert.IsNotEmpty(names);Assert.IsTrue(ProcgenCompat.ResDirExists(dir));
            var inputs=new FirstAwayGenerationInputs(42,17,0,2,"0:0:0","ship_0:0:0","breach_field","standard");
            var before=new ShipGenerator().GenerateFirstAway(inputs);Assert.IsNotNull(before);
            var snapshot=before.FirstAwayDescriptor.Snapshot();
            var reader=Overlay(original,new KeyValuePair<string,string>(before.KitPath,original.ReadText(before.KitPath)));
            CoreServices.Resources=reader;CatalogRegistry.Clear();
            Assert.IsTrue(ProcgenCompat.ResDirExists(dir));CollectionAssert.AreEqual(names,ProcgenCompat.ListResFiles(dir));
            Assert.IsTrue(new ModularSocketCatalog().LoadKit("ship_structural_hazard"));
            var inside=new ShipGenerator().GenerateFirstAway(inputs);Assert.IsNotNull(inside);
            Assert.AreEqual(before.LayoutJson,inside.LayoutJson);Assert.AreEqual(before.GameplaySliceJson,inside.GameplaySliceJson);
            Assert.IsTrue(new ShipGenerator().TryRestoreFirstAway(inputs,snapshot,before.LayoutJson,before.GameplaySliceJson,out _));
        }
        [Test]
        public void ArchiveListingUnionsDirectFilesWithoutDuplicatesAndKeepsOverlayTextAuthority()
        {
            var fallback=new LogicalDirectoryReader();
            var reader=Overlay(fallback,new KeyValuePair<string,string>("res://example/B.json","{\"archive\":true}"),
                new KeyValuePair<string,string>("res://example/C.json","{}"),new KeyValuePair<string,string>("res://example/nested/D.json","{}"));
            var directories=(IResourceDirectoryReader)reader;
            Assert.IsTrue(directories.DirExists("res://example"));Assert.IsTrue(directories.DirExists("res://example/nested/"));
            Assert.IsFalse(directories.DirExists("res://missing"));
            CollectionAssert.AreEqual(new[]{"A.json","B.json","C.json"},directories.ListFiles("res://example/"));
            CollectionAssert.AreEqual(new[]{"D.json"},directories.ListFiles("res://example/nested"));
            Assert.AreEqual("{\"archive\":true}",reader.ReadText("res://example/B.json"));
            CoreServices.Resources=reader;CollectionAssert.AreEqual(new[]{"A.json","B.json","C.json"},ProcgenCompat.ListResFiles("res://example"));
        }
        sealed class LogicalDirectoryReader : IResourceReader,IResourceDirectoryReader
        {
            public bool Exists(string path)=>path=="res://example/A.json"||path=="res://example/B.json";
            public string ReadText(string path)=>Exists(path)?"{\"fallback\":true}":null;
            public bool DirExists(string path)=>path.TrimEnd('/')=="res://example";
            public IReadOnlyList<string> ListFiles(string dir)=>DirExists(dir)?new[]{"B.json","A.json"}:Array.Empty<string>();
        }
    }
}
