using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Services
{
    /// <summary>
    /// Explicit closed publication, not a freshness claim about a mutable reader copied once.
    /// Its publisher owns all updates. Undeclared reads refuse; declared null texts/directories represent absence.
    /// No fallback reader, filesystem, global logger, or mutable catalog alias survives construction.
    /// </summary>
    public sealed class ImmutableResourceAuthority : IResourceReader, IResourceDirectoryReader
    {
        readonly Dictionary<string, string> _texts;
        readonly Dictionary<string, object> _parsed;
        readonly Dictionary<string, string[]> _directories;
        readonly string[] _diagnostics;
        public string ContentSha256 { get; }

        public ImmutableResourceAuthority(IReadOnlyDictionary<string, string> texts,
            IReadOnlyDictionary<string, IReadOnlyList<string>> directories = null)
        {
            if (texts == null) throw new ArgumentNullException(nameof(texts));
            _texts = new Dictionary<string, string>(StringComparer.Ordinal);
            _parsed = new Dictionary<string, object>(StringComparer.Ordinal);
            _directories = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var diagnostics = new List<string>();
            foreach (var row in texts.OrderBy(row => row.Key, StringComparer.Ordinal))
            {
                RequirePath(row.Key); _texts.Add(row.Key, row.Value);
                object parsed = row.Value == null ? null : GdJson.ParseString(row.Value);
                _parsed.Add(row.Key, parsed);
                if (row.Value == null) diagnostics.Add("missing_resource:" + row.Key);
                else if (parsed == null) diagnostics.Add("malformed_resource:" + row.Key);
            }
            if (directories != null) foreach (var row in directories.OrderBy(row => row.Key, StringComparer.Ordinal))
            {
                RequirePath(row.Key);
                string[] names = row.Value?.ToArray();
                if (names != null)
                {
                    if (names.Any(name => string.IsNullOrEmpty(name) || name == "." || name == ".."
                        || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
                        || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                        throw new ArgumentException("invalid_resource_directory_membership:" + row.Key);
                    Array.Sort(names, StringComparer.Ordinal);
                    // Membership cannot advertise a file for which this closed publication has no bytes.
                    foreach (string name in names)
                        if (!_texts.TryGetValue(row.Key + "/" + name, out string text) || text == null)
                            throw new ArgumentException("undeclared_directory_member:" + row.Key + "/" + name);
                }
                string prefix = row.Key + "/";
                var declaredChildren = _texts.Where(file => file.Value != null && file.Key.StartsWith(prefix, StringComparison.Ordinal)
                    && file.Key.Substring(prefix.Length).IndexOf('/') < 0).Select(file => file.Key.Substring(prefix.Length)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                if (names == null && _texts.Any(file => file.Value != null && file.Key.StartsWith(prefix, StringComparison.Ordinal))
                    || names != null && !declaredChildren.SequenceEqual(names))
                    throw new ArgumentException("inconsistent_resource_directory:" + row.Key);
                _directories.Add(row.Key, names);
            }
            _diagnostics = diagnostics.ToArray();
            ContentSha256 = HashPublication();
        }

        static void RequirePath(string path)
        {
            if (path == null || !(path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal)))
                throw new ArgumentException("invalid_resource_path");
            string relative = path.Substring(path.IndexOf("://", StringComparison.Ordinal) + 3);
            if (relative.Length == 0 || relative.IndexOf('\\') >= 0 || relative.IndexOf(':') >= 0
                || relative.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
                throw new ArgumentException("invalid_resource_path:" + path);
        }
        string Text(string path)
        {
            if (!_texts.TryGetValue(path, out string text)) throw new InvalidOperationException("undeclared_resource:" + path);
            return text;
        }
        string[] DirectoryNames(string path)
        {
            if (!_directories.TryGetValue(path, out var names)) throw new InvalidOperationException("undeclared_resource_directory:" + path);
            return names;
        }
        public bool Exists(string path) => Text(path) != null;
        public string ReadText(string path) => Text(path);
        public bool DirExists(string path) => DirectoryNames(path) != null;
        public IReadOnlyList<string> ListFiles(string path) => (string[])(DirectoryNames(path)?.Clone() ?? Array.Empty<string>());
        public object Load(string path)
        {
            Text(path); return V.DeepCopy(_parsed[path]);
        }
        public IReadOnlyList<string> Diagnostics() => (string[])_diagnostics.Clone();

        // Closed immutable-source projection preparation, outside live mutation gates. No package reader is trusted here.
        internal bool TryCreateProofResourceCapsule(out GdDict capsule,out string reason)
        {
            capsule=null;reason="proof_resource_bound";
            if(_texts.Count>256||_directories.Count>256)return false;
            var entries=new GdArray();var directories=new GdArray();long sourceBytes=0;int members=0;
            var utf8=new UTF8Encoding(false,true);
            try
            {
                foreach(var row in _texts.OrderBy(r=>r.Key,StringComparer.Ordinal))
                {
                    if(row.Key.Length>256||row.Value!=null&&row.Value.Length>65536)return false;
                    sourceBytes=checked(sourceBytes+utf8.GetByteCount(row.Key)+(row.Value==null?0:utf8.GetByteCount(row.Value)));
                    if(sourceBytes>4L*1024*1024)return false;
                    entries.Add(new GdDict{{"path",row.Key},{"present",row.Value!=null},{"text",row.Value}});
                }
                foreach(var row in _directories.OrderBy(r=>r.Key,StringComparer.Ordinal))
                {
                    if(row.Key.Length>256||row.Value!=null&&row.Value.Length>256)return false;
                    sourceBytes=checked(sourceBytes+utf8.GetByteCount(row.Key));GdArray names=null;
                    if(row.Value!=null)
                    {
                        names=new GdArray();foreach(string name in row.Value)
                        {
                            if(name.Length>256||++members>256)return false;sourceBytes=checked(sourceBytes+utf8.GetByteCount(name));
                            if(sourceBytes>4L*1024*1024)return false;names.Add(name);
                        }
                    }
                    if(sourceBytes>4L*1024*1024)return false;
                    directories.Add(new GdDict{{"path",row.Key},{"present",row.Value!=null},{"members",names}});
                }
                capsule=new GdDict{{"resource_version",1L},{"path_policy","resource-path-exact-v1"},{"snapshot_content_sha256",ContentSha256},{"entries",entries},{"directories",directories}};
                reason="prepared_unadmitted";return true;
            }
            catch(ArgumentException){return false;}catch(OverflowException){return false;}
        }

        string HashPublication()
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true), true))
                {
                    writer.Write("sealed-resource-authority-1"); writer.Write(_texts.Count);
                    foreach (var row in _texts.OrderBy(row => row.Key, StringComparer.Ordinal))
                    { writer.Write(row.Key); writer.Write(row.Value != null); if (row.Value != null) writer.Write(row.Value); }
                    writer.Write(_directories.Count);
                    foreach (var row in _directories.OrderBy(row => row.Key, StringComparer.Ordinal))
                    {
                        writer.Write(row.Key); writer.Write(row.Value != null);
                        if (row.Value == null) continue;
                        writer.Write(row.Value.Length); foreach (string name in row.Value) writer.Write(name);
                    }
                }
                using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    /// <summary>An input identity/freshness token only; issuance does not replace complete domain admission.</summary>
    public sealed class ResourceAuthorityLease
    {
        public ImmutableResourceAuthority Snapshot { get; }
        public long Version { get; }
        internal ResourceAuthorityLease(ImmutableResourceAuthority snapshot, long version) { Snapshot = snapshot; Version = version; }
        /// <summary>Instantaneous freshness observation, not a reservation or atomic publication fence.
        /// A future admission caller must serialize its final check and canonical write against reader replacement
        /// and cache clear; checking this property alone does not authorize publication.</summary>
        public bool IsCurrent => ResourceAuthorityPublication.IsCurrent(this);
    }

    /// <summary>
    /// Reader replacement and cache clear invalidate tokens atomically. Only explicitly immutable publications issue
    /// reusable tokens. Runtime bootstrap and DataSync must own this seam before a live work lease can be activated.
    /// Snapshot validation uses Snapshot directly, never a process-global reader scope or worker log callback.
    /// </summary>
    public static class ResourceAuthorityPublication
    {
        static readonly object Gate = CommonParticipantGate.SyncRoot;
        static IResourceReader _reader;
        static long _version;
        public static IResourceReader Reader { get { PinnedAdmissionResourceScope.RefusePort("ResourceAuthorityPublication.Reader"); lock (Gate) return _reader; } }
        public static void ReplaceReader(IResourceReader reader)
        { PinnedAdmissionResourceScope.RefusePort("ResourceAuthorityPublication.ReplaceReader"); lock (Gate) { long next = checked(_version + 1); _reader = reader; _version = next; } }
        public static void Publish(ImmutableResourceAuthority authority)
        { PinnedAdmissionResourceScope.RefusePort("ResourceAuthorityPublication.Publish"); if (authority == null) throw new ArgumentNullException(nameof(authority)); ReplaceReader(authority); }
        internal static void Invalidate() { PinnedAdmissionResourceScope.RefusePort("ResourceAuthorityPublication.Invalidate"); lock (Gate) _version = checked(_version + 1); }
        public static bool TryAcquire(out ResourceAuthorityLease lease, out string reason)
        {
            PinnedAdmissionResourceScope.RefusePort("ResourceAuthorityPublication.TryAcquire");
            lock (Gate)
            {
                lease = null;
                if (!(_reader is ImmutableResourceAuthority authority)) { reason = "unversioned_mutable_resource_reader"; return false; }
                lease = new ResourceAuthorityLease(authority, _version); reason = ""; return true;
            }
        }
        internal static bool IsCurrent(ResourceAuthorityLease lease)
        { lock (Gate) return lease.Version == _version && ReferenceEquals(lease.Snapshot, _reader); }
    }
}
