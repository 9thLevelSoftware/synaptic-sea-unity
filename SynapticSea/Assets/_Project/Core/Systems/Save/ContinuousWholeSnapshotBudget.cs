using System;
using System.Collections.Generic;
using System.Globalization;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Systems
{
    // Preflight only. Passing a bound never authenticates snapshots or authorizes output.
    internal static class ContinuousWholeSnapshotBudget
    {
        internal const long MaximumNodes=100000,MaximumJsonUpperBytes=4194304;
        internal const int MaximumDepth=128;
        sealed class Frame
        {
            internal readonly bool TypedSource;internal readonly object Value;internal readonly string Path;internal readonly int Depth;internal int Index;internal bool Started;
            internal HashSet<string> WireKeys;
            internal Frame(object value,int depth,string path="$",bool typedSource=false){Value=value;Depth=depth;Path=path;TypedSource=typedSource;}
        }
        internal static bool TryPreflight(GdDict run,GdDict world,out long nodes,out long jsonUpperBytes,out string reason)
        {
            nodes=0;jsonUpperBytes=0;reason="whole_snapshot_missing";if(run==null||world==null)return false;
            return TryPreflightEnvelope(new GdDict{{"run",run},{"world",world}},out nodes,out jsonUpperBytes,out reason);
        }
        // Callers with metadata MUST pass the actual complete raw envelope here, not reset a per-root budget.
        internal static bool TryPreflightEnvelope(GdDict envelope,out long nodes,out long jsonUpperBytes,out string reason)
        {
            return TryPreflightOwnedProjection(envelope,null,out nodes,out jsonUpperBytes,out reason);
        }
        // Reader-owned exact decoded occurrences replace transport wrappers for source-budget accounting only.
        internal static bool TryPreflightOwnedProjection(GdDict envelope,Dictionary<GdDict,GdDict> decodedOccurrences,out long nodes,out long jsonUpperBytes,out string reason)
        {
            return Scan(envelope,decodedOccurrences,0,0,0,out nodes,out jsonUpperBytes,out reason);
        }
        internal static bool TryPreflightAdditional(GdDict ownedMetadataAndArtifacts,long chargedNodes,long chargedJsonUpperBytes,
            out long totalNodes,out long totalJsonUpperBytes,out string reason)
            =>Scan(ownedMetadataAndArtifacts,null,chargedNodes,chargedJsonUpperBytes,1,out totalNodes,out totalJsonUpperBytes,out reason);
        static bool Scan(GdDict envelope,Dictionary<GdDict,GdDict> decodedOccurrences,long initialNodes,long initialBytes,int rootDepth,
            out long nodes,out long jsonUpperBytes,out string reason)
        {
            nodes=initialNodes;jsonUpperBytes=initialBytes;reason="whole_snapshot_missing";if(envelope==null)return false;
            if(nodes<0||nodes>MaximumNodes||jsonUpperBytes<0||jsonUpperBytes>MaximumJsonUpperBytes){reason="whole_snapshot_initial_budget";return false;}
            var stack=new Stack<Frame>();stack.Push(new Frame(envelope,rootDepth));
            while(stack.Count>0)
            {
                var f=stack.Peek();if(f.Depth>MaximumDepth){reason="whole_snapshot_depth";return false;}
                if(!f.Started)
                {
                    f.Started=true;if(++nodes>MaximumNodes){reason="whole_snapshot_nodes";return false;}
                    long bytes;
                    if(f.Value is GdDict || f.Value is GdArray)bytes=2;
                    else if(f.Value==null)bytes=4;
                    else if(f.Value is string text){if(!TryStringBytes(text,out bytes)){reason="whole_snapshot_unicode";return false;}}
                    else if(f.Value is bool)bytes=5;
                    else if(f.Value is long)bytes=20;
                    else if(f.Value is double number){if(double.IsNaN(number)||double.IsInfinity(number)){reason="whole_snapshot_nonfinite";return false;}bytes=32;}
                    else if(f.TypedSource && f.Value is Vec3 vector)
                    {
                        if(float.IsNaN(vector.X)||float.IsInfinity(vector.X)||float.IsNaN(vector.Y)||float.IsInfinity(vector.Y)||float.IsNaN(vector.Z)||float.IsInfinity(vector.Z))
                        {reason="whole_snapshot_nonfinite";return false;}
                        // ComponentDomainCodec counts vector3 as ONE source leaf. Its closed BitsV2 wire
                        // is ["vector3",three quoted16hex components] (<128bytes); full wire charged separately.
                        bytes=128;
                    }
                    else {reason="whole_snapshot_scalar_type:"+f.Value.GetType().FullName+":"+f.Path;return false;}
                    jsonUpperBytes+=bytes;if(jsonUpperBytes>MaximumJsonUpperBytes){reason="whole_snapshot_bytes:"+jsonUpperBytes.ToString(CultureInfo.InvariantCulture)+":"+f.Path;return false;}
                }
                if(f.Value is GdDict dict)
                {
                    if(f.Index>=dict.Count){stack.Pop();continue;}
                    int i=f.Index++;object rawKey=dict.Keys[i];string key;
                    if(rawKey is string textKey)key=textKey;
                    else if(rawKey is long sequence)key=sequence.ToString(CultureInfo.InvariantCulture);
                    else {reason="whole_snapshot_key_type";return false;}
                    // Stock objective summaries use Int64 sequence keys; JSON uses exact invariant decimal spelling.
                    // Charge every occurrence and reject collisions before ordinary detached DTO normalization.
                    if(f.WireKeys==null)f.WireKeys=new HashSet<string>(StringComparer.Ordinal);
                    if(!f.WireKeys.Add(key)&&!f.TypedSource){reason="whole_snapshot_key_collision";return false;}
                    // Exact strict UTF8/JSON string bytes plus colon and conservative one comma.
                    if(++nodes>MaximumNodes){reason="whole_snapshot_nodes";return false;}
                    if(!TryStringBytes(key,out long keyBytes)){reason="whole_snapshot_unicode";return false;}
                    jsonUpperBytes+=2L+keyBytes;if(jsonUpperBytes>MaximumJsonUpperBytes){reason="whole_snapshot_bytes:"+jsonUpperBytes.ToString(CultureInfo.InvariantCulture)+":"+f.Path;return false;}
                    stack.Push(new Frame(Project(dict.Values[i],decodedOccurrences),f.Depth+1,f.Path+"["+key+"]",f.TypedSource||IsDecoded(dict.Values[i],decodedOccurrences)));
                }
                else if(f.Value is GdArray array)
                {
                    if(f.Index>=array.Count){stack.Pop();continue;}
                    jsonUpperBytes++;if(jsonUpperBytes>MaximumJsonUpperBytes){reason="whole_snapshot_bytes:"+jsonUpperBytes.ToString(CultureInfo.InvariantCulture)+":"+f.Path;return false;}
                    stack.Push(new Frame(Project(array[f.Index],decodedOccurrences),f.Depth+1,f.Path+"["+(f.Index++)+"]",f.TypedSource||IsDecoded(array[f.Index-1],decodedOccurrences)));
                }
                else stack.Pop();
            }
            reason="whole_snapshot_conservative_bounds";return true;
        }
        // Matches PaidSnapshotCodec.WriteString, measured as strict UTF8 without allocating text/bytes.
        internal static bool TryStringBytes(string text,out long bytes)
        {
            bytes=2;
            for(int i=0;i<text.Length;i++)
            {
                char c=text[i];
                if(c=='"'||c=='\\'||c=='\b'||c=='\t'||c=='\n'||c=='\f'||c=='\r')bytes+=2;
                else if(c<32)bytes+=6;
                else if(c<128)bytes++;
                else if(c<2048)bytes+=2;
                else if(char.IsHighSurrogate(c))
                {if(i+1>=text.Length||!char.IsLowSurrogate(text[i+1]))return false;i++;bytes+=4;}
                else if(char.IsLowSurrogate(c))return false;
                else bytes+=3;
                // Once alone over the aggregate cap, caller will reject before any output; stop bounded scan.
                if(bytes>MaximumJsonUpperBytes)return true;
            }
            return true;
        }
        static bool IsDecoded(object value,Dictionary<GdDict,GdDict> occurrences)
            =>value is GdDict dict && occurrences!=null && occurrences.ContainsKey(dict);
        static object Project(object value,Dictionary<GdDict,GdDict> occurrences)
            => value is GdDict dict && occurrences!=null && occurrences.TryGetValue(dict,out var decoded) ? decoded : value;
    }
}
