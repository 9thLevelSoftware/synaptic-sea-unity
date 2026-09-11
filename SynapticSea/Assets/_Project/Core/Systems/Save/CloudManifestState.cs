// Ported from scripts/systems/cloud_manifest_state.gd @ 96ecb2b0
using System.Text;
using SynapticSea.Core.Services;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Cloud-ready manifest sidecar (ADR-0032). NOT a cloud SDK: captures device id, build id, schema version,
    /// the slot file's SHA-256 and size, and sync eligibility. Slot files are read through an <see cref="IStorage"/>
    /// (defaulting to <see cref="CoreServices.UserStorage"/> in the static helpers).
    /// </summary>
    public class CloudManifestState
    {
        public const string ManifestVersion = "cloud-manifest-1";
        public const string CloudProviderStub = "stub";

        public string DeviceId = "";
        public string BuildId = "";
        public string SchemaVersion = "";
        public string PayloadSha256 = "";
        public long PayloadSizeBytes = 0;
        public string CreatedAt = "";
        public bool SyncEligible = true;
        // ADR-0032 placeholder: future values include "steam" and "icloud".
        public string CloudProvider = CloudProviderStub;
        public string SlotId = "";

        public GdDict ToDict()
        {
            return new GdDict
            {
                { "manifest_version", ManifestVersion },
                { "device_id", DeviceId },
                { "build_id", BuildId },
                { "schema_version", SchemaVersion },
                { "payload_sha256", PayloadSha256 },
                { "payload_size_bytes", PayloadSizeBytes },
                { "created_at", CreatedAt },
                { "sync_eligible", SyncEligible },
                { "cloud_provider", CloudProvider },
                { "slot_id", SlotId },
            };
        }

        public static CloudManifestState FromDict(object data)
        {
            var m = new CloudManifestState();
            if (!(data is GdDict dict)) return m;
            m.DeviceId = V.Str(dict.Get("device_id", ""));
            m.BuildId = V.Str(dict.Get("build_id", ""));
            m.SchemaVersion = V.Str(dict.Get("schema_version", ""));
            m.PayloadSha256 = V.Str(dict.Get("payload_sha256", ""));
            m.PayloadSizeBytes = V.I64(dict.Get("payload_size_bytes", 0L));
            m.CreatedAt = V.Str(dict.Get("created_at", ""));
            m.SyncEligible = V.Bool(dict.Get("sync_eligible", true));
            m.CloudProvider = V.Str(dict.Get("cloud_provider", CloudProviderStub));
            m.SlotId = V.Str(dict.Get("slot_id", ""));
            return m;
        }

        /// <summary>Build a manifest for a freshly-written slot file.</summary>
        public static CloudManifestState BuildForSlot(string slotId, string slotPath, string schemaVersion, IStorage storage = null, IClock clock = null)
        {
            storage = storage ?? CoreServices.UserStorage;
            clock = clock ?? CoreServices.Clock;
            var m = new CloudManifestState();
            m.SlotId = slotId;
            m.DeviceId = DeriveDeviceId(storage);
            // RUNTIME: ProjectSettings.get_setting("application/config/version", "0.0.0").
            m.BuildId = InfraCompat.ProjectVersion;
            m.SchemaVersion = schemaVersion;
            m.PayloadSha256 = Sha256OfFile(slotPath, storage);
            m.PayloadSizeBytes = SizeOfFile(slotPath, storage);
            m.CreatedAt = clock.DateTimeString(true);
            m.SyncEligible = true;
            m.CloudProvider = CloudProviderStub;
            return m;
        }

        /// <summary>Recompute the SHA against the slot file's current text; "" on I/O error.</summary>
        public static string RecomputeSha256(string slotPath, IStorage storage = null)
        {
            return Sha256OfFile(slotPath, storage ?? CoreServices.UserStorage);
        }

        static string Sha256OfFile(string path, IStorage storage)
        {
            if (!storage.FileExists(path)) return "";
            // Slot files are JSON; the JSON text is hashed directly.
            string content = storage.ReadText(path);
            if (string.IsNullOrEmpty(content)) return "";
            return InfraCompat.Sha256Text(content);
        }

        static long SizeOfFile(string path, IStorage storage)
        {
            if (!storage.FileExists(path)) return 0;
            string content = storage.ReadText(path);
            if (content == null) return 0;
            // FileAccess.get_length(): byte length of the UTF-8 file.
            return Encoding.UTF8.GetByteCount(content);
        }

        static string DeriveDeviceId(IStorage storage)
        {
            // Stable across runs on the same machine: hash the user data dir.
            string path = storage.Globalize("user://");
            return InfraCompat.Sha256Text(path).Substring(0, 16);
        }
    }
}
