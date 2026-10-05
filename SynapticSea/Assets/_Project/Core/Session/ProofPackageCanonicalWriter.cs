using System;
using System.Globalization;
using System.Linq;
using System.Text;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Detached preparation/output only; no live capture or proof admission.
    internal sealed class ProofPackageCanonicalWriter
    {
        readonly StringBuilder _output=new StringBuilder();long _bytes;int _nodes;
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        internal static bool TryWrite(OwnedProofPackageInput input,out string text,out string reason)
        {
            text=null;reason="proof_package_output_bound";if(input==null)return false;
            try
            {
                var inner=ComponentDomainCodec.Encode(input.CopyPackage(),ComponentDomainCodec.BitExactSchema);
                var outer=new GdDict{{"schema",AuxiliaryProofOwnerProfile.OuterSchema},{"codec",inner}};
                var writer=new ProofPackageCanonicalWriter();writer.Value(outer,0);text=writer._output.ToString();reason="owned_unadmitted_output";return true;
            }
            catch(ArgumentException){return false;}catch(OverflowException){return false;}
        }
        void Append(string token)
        {
            _bytes=checked(_bytes+Utf8.GetByteCount(token));if(_bytes>ProofPackageJsonReader.MaximumBytes)throw new ArgumentException("byte_bound");_output.Append(token);
        }
        void Value(object value,int depth)
        {
            if(depth>ProofPackageJsonReader.MaximumWireDepth||++_nodes>ProofPackageJsonReader.MaximumWireNodes)throw new ArgumentException("wire_bound");
            if(value==null){Append("null");return;}if(value is bool b){Append(b?"true":"false");return;}
            if(value is string s){Text(s);return;}if(value is long n){Append(n.ToString(CultureInfo.InvariantCulture));return;}
            if(value is GdArray array){Append("[");bool first=true;foreach(var item in array){if(!first)Append(",");first=false;Value(item,depth+1);}Append("]");return;}
            if(value is GdDict dict)
            {
                if(dict.Keys.Any(k=>!(k is string)))throw new ArgumentException("key_type");Append("{");bool first=true;
                foreach(var pair in dict.OrderBy(p=>(string)p.Key,StringComparer.Ordinal))
                {
                    if(++_nodes>ProofPackageJsonReader.MaximumWireNodes||depth+1>ProofPackageJsonReader.MaximumWireDepth)throw new ArgumentException("key_bound");
                    if(!first)Append(",");first=false;Text((string)pair.Key);Append(":");Value(pair.Value,depth+1);
                }
                Append("}");return;
            }
            throw new ArgumentException("unsupported_wire_scalar");
        }
        void Text(string text)
        {
            if(text.Length>65536)throw new ArgumentException("string_bound");Utf8.GetByteCount(text);Append("\"");
            for(int i=0;i<text.Length;i++)
            {
                char c=text[i];switch(c)
                {
                    case '"':Append("\\\"");break;case '\\':Append("\\\\");break;
                    case '\b':Append("\\b");break;case '\f':Append("\\f");break;case '\n':Append("\\n");break;case '\r':Append("\\r");break;case '\t':Append("\\t");break;
                    default:
                        if(c<32)Append("\\u"+((int)c).ToString("x4",CultureInfo.InvariantCulture));
                        else if(char.IsHighSurrogate(c)){Append(text.Substring(i,2));i++;}
                        else Append(c.ToString());break;
                }
            }
            Append("\"");
        }
    }
}
