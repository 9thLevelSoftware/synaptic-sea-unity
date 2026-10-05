using System;
using System.Globalization;
using System.Text;
using SynapticSea.Core.Variant;
namespace SynapticSea.Core.Session
{
    // Closed bounded JSON transport parser. No resource access, Unity or admission grants.
    internal sealed class ProofPackageJsonReader
    {
        internal const int MaximumBytes=4*1024*1024,MaximumWireNodes=549999,MaximumWireDepth=386;
        readonly string _text;int _position,_nodes;
        ProofPackageJsonReader(string text){_text=text;}
        static readonly UTF8Encoding StrictUtf8=new UTF8Encoding(false,true);
        internal static bool TryRead(string text,out GdDict result,out string reason)
        {
            result=null;reason="proof_package_transport_bound";
            if(text==null||text.Length>MaximumBytes)return false;
            try
            {
                if(StrictUtf8.GetByteCount(text)>MaximumBytes)return false;
                var parser=new ProofPackageJsonReader(text);object value=parser.Value(0);parser.Space();
                if(parser._position!=text.Length||!(value is GdDict root))return false;
                result=root;reason="ok";return true;
            }
            catch(ArgumentException){return false;}catch(OverflowException){return false;}
        }
        void Bound(int depth){if(depth>MaximumWireDepth||++_nodes>MaximumWireNodes)throw new ArgumentException("wire_bound");}
        void Space(){while(_position<_text.Length&&(_text[_position]==' '||_text[_position]=='\n'||_text[_position]=='\r'||_text[_position]=='\t'))_position++;}
        bool Take(char c){Space();if(_position<_text.Length&&_text[_position]==c){_position++;return true;}return false;}
        void Require(char c){if(!Take(c))throw new ArgumentException("invalid_json");}
        object Value(int depth)
        {
            Bound(depth);Space();if(_position==_text.Length)throw new ArgumentException("missing_json");char c=_text[_position];
            if(c=='{')
            {
                _position++;var d=new GdDict();if(Take('}'))return d;
                do{Bound(depth+1);string key=String();if(d.Has(key))throw new ArgumentException("duplicate_key");Require(':');d[key]=Value(depth+1);}while(Take(','));Require('}');return d;
            }
            if(c=='['){_position++;var a=new GdArray();if(Take(']'))return a;do{a.Add(Value(depth+1));}while(Take(','));Require(']');return a;}
            if(c=='"')return String();
            if(c=='t'){Literal("true");return true;}if(c=='f'){Literal("false");return false;}if(c=='n'){Literal("null");return null;}
            return Number();
        }
        void Literal(string value){if(_text.Length-_position<value.Length||string.CompareOrdinal(_text,_position,value,0,value.Length)!=0)throw new ArgumentException("invalid_literal");_position+=value.Length;}
        string String()
        {
            Require('"');var s=new StringBuilder();bool ended=false;
            while(_position<_text.Length)
            {
                char c=_text[_position++];if(c=='"'){ended=true;break;}if(c<32)throw new ArgumentException("control");
                if(c=='\\')
                {
                    if(_position==_text.Length)throw new ArgumentException("escape");c=_text[_position++];
                    switch(c){case '"':case '\\':case '/':break;case 'b':c='\b';break;case 'f':c='\f';break;case 'n':c='\n';break;case 'r':c='\r';break;case 't':c='\t';break;case 'u':
                        if(_text.Length-_position<4)throw new ArgumentException("unicode");int code=0;for(int i=0;i<4;i++){char h=_text[_position++];int n=h>='0'&&h<='9'?h-'0':h>='a'&&h<='f'?h-'a'+10:h>='A'&&h<='F'?h-'A'+10:-1;if(n<0)throw new ArgumentException("unicode");code=code*16+n;}c=(char)code;break;
                        default:throw new ArgumentException("escape");}
                }
                if(s.Length==65536)throw new ArgumentException("string_bound");s.Append(c);
            }
            if(!ended)throw new ArgumentException("unterminated_string");string value=s.ToString();StrictUtf8.GetByteCount(value);return value;
        }
        object Number()
        {
            int begin=_position;if(_text[_position]=='-')_position++;if(_position==_text.Length)throw new ArgumentException("number");
            if(_text[_position]=='0')_position++;else{if(_text[_position]<'1'||_text[_position]>'9')throw new ArgumentException("number");while(_position<_text.Length&&char.IsDigit(_text[_position])&&_text[_position]<128)_position++;}
            bool real=false;if(_position<_text.Length&&_text[_position]=='.'){real=true;_position++;int first=_position;while(_position<_text.Length&&_text[_position]>='0'&&_text[_position]<='9')_position++;if(first==_position)throw new ArgumentException("fraction");}
            if(_position<_text.Length&&(_text[_position]=='e'||_text[_position]=='E')){real=true;_position++;if(_position<_text.Length&&(_text[_position]=='+'||_text[_position]=='-'))_position++;int first=_position;while(_position<_text.Length&&_text[_position]>='0'&&_text[_position]<='9')_position++;if(first==_position)throw new ArgumentException("exponent");}
            if(_position-begin>64)throw new ArgumentException("number_bound");string token=_text.Substring(begin,_position-begin);
            if(!real&&long.TryParse(token,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out long integer))return integer;
            if(!real)throw new ArgumentException("integer_overflow");if(!double.TryParse(token,NumberStyles.Float,CultureInfo.InvariantCulture,out double d)||double.IsNaN(d)||double.IsInfinity(d))throw new ArgumentException("real_bound");return d;
        }
    }
}
