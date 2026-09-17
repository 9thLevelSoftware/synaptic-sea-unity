using System.Linq;
using NUnit.Framework;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;

namespace SynapticSea.Tests.Parity
{
    /// <summary>
    /// The Unity port's run schema (<c>gate2-current-run-5</c>) is Godot's <c>gate2-current-run-4</c> plus
    /// <see cref="RunSnapshot.PortExtensionFields"/>. Parity tests compare Godot's keys exactly through
    /// <see cref="GodotView"/> and assert the extension keys separately.
    /// </summary>
    public static class SavePortSchema
    {
        public const string PortRunVersion = SaveMigrationService.TargetVersion;
        public const string GodotRunVersion = SaveMigrationService.GodotTargetVersion;

        /// <summary>
        /// A deep copy with every run-snapshot dict (a <c>slice_version</c> of <c>gate2-current-run-*</c>) stripped of the port
        /// extension keys, and every <c>gate2-current-run-5</c> string value rewritten to <c>gate2-current-run-4</c>
        /// (slice_version, index/manifest schema_version, migration to_version).
        /// </summary>
        public static object GodotView(object value)
        {
            switch (value)
            {
                case GdDict d:
                    {
                        var output = new GdDict();
                        bool isRunSnapshot = V.Str(d.Get("slice_version", "")).StartsWith("gate2-current-run-");
                        foreach (object key in d.Keys)
                        {
                            if (isRunSnapshot && RunSnapshot.PortExtensionFields.Contains(V.Str(key)))
                                continue;
                            output[key] = GodotView(d[key]);
                        }
                        return output;
                    }
                case GdArray a:
                    {
                        var output = new GdArray();
                        foreach (object item in a)
                            output.Add(GodotView(item));
                        return output;
                    }
                case string s when s == PortRunVersion:
                    return GodotRunVersion;
                default:
                    return value;
            }
        }

        public static GdDict GodotView(GdDict d) => (GdDict)GodotView((object)d);

        /// <summary>Asserts a run-snapshot dict carries exactly the extension keys with the migration defaults.</summary>
        public static void AssertExtensionDefaults(GdDict runDict, string where)
        {
            foreach (object key in RunSnapshot.PortExtensionFields)
            {
                Assert.IsTrue(runDict.Has(key), $"{where}: missing port key {key}");
                Assert.IsTrue(V.VariantEquals(SaveMigrationService.V5Defaults[key], runDict[key]), $"{where}: {key} is not the migration default");
            }
        }

        /// <summary>The extension keys sit between ship_modification_summary and play_time_seconds, in order.</summary>
        public static void AssertExtensionOrder(GdDict runDict, string where)
        {
            var keys = runDict.Keys.Select(k => V.Str(k)).ToList();
            int at = keys.IndexOf("ship_modification_summary");
            Assert.GreaterOrEqual(at, 0, where);
            for (int i = 0; i < RunSnapshot.PortExtensionFields.Count; i++)
                Assert.AreEqual(V.Str(RunSnapshot.PortExtensionFields[i]), keys[at + 1 + i], where);
            Assert.AreEqual("play_time_seconds", keys[at + 1 + RunSnapshot.PortExtensionFields.Count], where);
        }
    }
}
