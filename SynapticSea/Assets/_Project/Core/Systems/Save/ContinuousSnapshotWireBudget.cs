using System.Text;
namespace SynapticSea.Core.Systems
{
    // Immutable-text allocation preflight only. The ordinary parser still validates JSON grammar.
    internal static class ContinuousSnapshotWireBudget
    {
        internal const long MaximumBytes=4194304,MaximumTokens=549999;
        internal const int MaximumDepth=386;
        internal static bool TryPreflight(string runText,string worldText,out long bytes,out long tokens,out string reason)
        {
            bytes=0;tokens=0;reason="whole_snapshot_wire_missing";
            if(runText==null||worldText==null)return false;
            if((long)runText.Length+worldText.Length>MaximumBytes){reason="whole_snapshot_wire_bytes";return false;}
            bytes=(long)Encoding.UTF8.GetByteCount(runText)+Encoding.UTF8.GetByteCount(worldText);
            if(bytes>MaximumBytes){reason="whole_snapshot_wire_bytes";return false;}
            if(!Scan(runText,ref tokens,out reason)||!Scan(worldText,ref tokens,out reason))return false;
            reason="whole_snapshot_wire_preflight";return true;
        }
        static bool Scan(string text,ref long tokens,out string reason)
        {
            reason="whole_snapshot_wire_invalid";int containers=0;
            for(int i=0;i<text.Length;i++)
            {
                char c=text[i];if(c==' '||c=='\t'||c=='\r'||c=='\n'||c==','||c==':')continue;
                if(c=='}'||c==']'){if(--containers<0)return false;continue;}
                if(containers>MaximumDepth){reason="whole_snapshot_wire_depth";return false;}
                if(++tokens>MaximumTokens){reason="whole_snapshot_wire_nodes";return false;}
                if(c=='{'||c=='['){containers++;continue;}
                if(c=='"')
                {
                    bool closed=false;
                    while(++i<text.Length)
                    {
                        c=text[i];if(c=='\\'){if(++i>=text.Length)return false;continue;}
                        if(c=='"'){closed=true;break;}
                    }
                    if(!closed)return false;continue;
                }
                // Scalar token: grammar/number range is checked by the exact ordinary parser later.
                while(i+1<text.Length){c=text[i+1];if(c==' '||c=='\t'||c=='\r'||c=='\n'||c==','||c==':'||c=='{'||c=='}'||c=='['||c==']'||c=='"')break;i++;}
            }
            if(containers!=0)return false;return true;
        }
    }
}
