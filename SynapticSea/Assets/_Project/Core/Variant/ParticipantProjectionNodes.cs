using System;
using System.Collections.Generic;

namespace SynapticSea.Core.Variant
{
    internal enum ProjectionScalarKind { Null, Bool, Int64, Float64, String, Vec2i, Vec3 }
    internal readonly struct ProjectionScalar : IEquatable<ProjectionScalar>
    {
        internal const int MaximumTextLength = 1024;
        internal readonly ProjectionScalarKind Kind;
        internal readonly ulong Bits0, Bits1, Bits2;
        internal readonly string Text;
        ProjectionScalar(ProjectionScalarKind kind, ulong a = 0, ulong b = 0, ulong c = 0, string text = null)
        { Kind=kind;Bits0=a;Bits1=b;Bits2=c;Text=text; }
        internal static ProjectionScalar FromNormalized(object value)
        {
            switch(value)
            {
                case null: return new ProjectionScalar(ProjectionScalarKind.Null);
                case bool b: return new ProjectionScalar(ProjectionScalarKind.Bool,b?1UL:0UL);
                case long n: return new ProjectionScalar(ProjectionScalarKind.Int64,unchecked((ulong)n));
                case double d: return new ProjectionScalar(ProjectionScalarKind.Float64,unchecked((ulong)BitConverter.DoubleToInt64Bits(d)));
                case string text:
                    if(text.Length>MaximumTextLength)throw new ArgumentException("projection_text_capacity");
                    return new ProjectionScalar(ProjectionScalarKind.String,text:text);
                case Vec2i v: return new ProjectionScalar(ProjectionScalarKind.Vec2i,unchecked((uint)v.X),unchecked((uint)v.Y));
                case Vec3 v: return new ProjectionScalar(ProjectionScalarKind.Vec3,unchecked((uint)BitConverter.SingleToInt32Bits(v.X)),unchecked((uint)BitConverter.SingleToInt32Bits(v.Y)),unchecked((uint)BitConverter.SingleToInt32Bits(v.Z)));
                default: throw new ArgumentException("projection_requires_normalized_scalar");
            }
        }
        internal object ToNormalized()
        {
            switch(Kind)
            {
                case ProjectionScalarKind.Null:return null;
                case ProjectionScalarKind.Bool:return Bits0!=0;
                case ProjectionScalarKind.Int64:return unchecked((long)Bits0);
                case ProjectionScalarKind.Float64:return BitConverter.Int64BitsToDouble(unchecked((long)Bits0));
                case ProjectionScalarKind.String:return Text;
                case ProjectionScalarKind.Vec2i:return new Vec2i(unchecked((int)Bits0),unchecked((int)Bits1));
                case ProjectionScalarKind.Vec3:return new Vec3(BitConverter.Int32BitsToSingle(unchecked((int)Bits0)),BitConverter.Int32BitsToSingle(unchecked((int)Bits1)),BitConverter.Int32BitsToSingle(unchecked((int)Bits2)));
                default:throw new InvalidOperationException("invalid_projection_scalar");
            }
        }
        public bool Equals(ProjectionScalar value)=>Kind==value.Kind&&Bits0==value.Bits0&&Bits1==value.Bits1&&Bits2==value.Bits2&&StringComparer.Ordinal.Equals(Text,value.Text);
        public override bool Equals(object value)=>value is ProjectionScalar scalar&&Equals(scalar);
        public override int GetHashCode()=>unchecked((int)Kind*397^(int)Bits0^(int)(Bits0>>32)^(int)Bits1^(int)Bits2^(Text==null?0:StringComparer.Ordinal.GetHashCode(Text)));
    }
    internal readonly struct ProjectionNodeId : IEquatable<ProjectionNodeId>
    {
        internal readonly ulong Registry, Value;
        internal ProjectionNodeId(ulong registry,ulong value){Registry=registry;Value=value;}
        public bool Equals(ProjectionNodeId other)=>Registry==other.Registry&&Value==other.Value;
        public override bool Equals(object value)=>value is ProjectionNodeId id&&Equals(id);
        public override int GetHashCode()=>unchecked((int)Registry*397^(int)Value^(int)(Value>>32));
    }
    internal readonly struct ProjectionValue
    {
        internal readonly bool IsChild;
        internal readonly ProjectionScalar Scalar;
        internal readonly ProjectionNodeId Child;
        internal ProjectionValue(ProjectionScalar scalar){IsChild=false;Scalar=scalar;Child=default;}
        internal ProjectionValue(ProjectionNodeId child){if(child.Registry==0||child.Value==0)throw new ArgumentException("invalid_projection_child");IsChild=true;Child=child;Scalar=default;}
    }
    internal readonly struct ProjectionEntry
    {
        internal readonly ProjectionScalar Key;
        internal readonly ProjectionValue Value;
        internal ProjectionEntry(ProjectionScalar key,ProjectionValue value){Key=key;Value=value;}
        internal ProjectionEntry(ProjectionValue value){Key=default;Value=value;}
    }
    internal enum ProjectionNodeKind { Dictionary, Array }
    internal sealed class ProjectionEntryPage
    {
        internal const int Fanout=32;
        readonly ProjectionEntry[] _entries;
        internal int Count=>_entries.Length;
        internal ProjectionEntry this[int index]=>_entries[index];
        internal ProjectionEntryPage(ProjectionEntry[] entries,int offset,int count)
        {if(count<0||count>Fanout||offset<0||offset>entries.Length-count)throw new ArgumentException("projection_page_bounds");_entries=new ProjectionEntry[count];Array.Copy(entries,offset,_entries,0,count);}
        internal ProjectionEntryPage Copy()=>new ProjectionEntryPage(_entries,0,_entries.Length);
    }
    internal sealed class ProjectionNodeIssuer { }
    internal sealed class ProjectionNodeVersion
    {
        readonly ProjectionEntryPage[] _pages;
        readonly ProjectionNodeIssuer _issuer;
        internal bool HasIssuer(ProjectionNodeIssuer issuer)=>ReferenceEquals(_issuer,issuer);
        internal ProjectionNodeId Id{get;}
        internal ulong Version{get;}
        internal ProjectionNodeKind Kind{get;}
        internal int EntryCount{get;}
        internal int PageCount=>_pages.Length;
        internal long Units=>1L+PageCount;
        internal ProjectionNodeVersion(ProjectionNodeId id,ulong version,ProjectionNodeKind kind,ProjectionEntry[] entries,int maximumEntries,ProjectionNodeIssuer issuer=null)
        {
            if(id.Registry==0||id.Value==0||version==0||entries==null||entries.Length>maximumEntries||maximumEntries<0||maximumEntries>4096)throw new ArgumentException("projection_node_capacity");
            if(kind!=ProjectionNodeKind.Dictionary&&kind!=ProjectionNodeKind.Array)throw new ArgumentException("projection_node_kind");
            Id=id;Version=version;Kind=kind;EntryCount=entries.Length;_issuer=issuer;
            var keys=kind==ProjectionNodeKind.Dictionary?new HashSet<object>(VariantKeyComparer.Instance):null;
            foreach(var entry in entries)
            {
                if(keys==null&&entry.Key.Kind!=ProjectionScalarKind.Null)throw new ArgumentException("projection_array_key");
                if(keys!=null&&(entry.Key.Kind==ProjectionScalarKind.Null||!keys.Add(entry.Key.ToNormalized())))throw new ArgumentException("projection_duplicate_or_null_key");
                if(entry.Value.IsChild&&entry.Value.Child.Registry!=id.Registry)throw new ArgumentException("projection_cross_registry_child");
            }
            _pages=new ProjectionEntryPage[(entries.Length+31)/32];
            for(int i=0;i<_pages.Length;i++)_pages[i]=new ProjectionEntryPage(entries,i*32,Math.Min(32,entries.Length-i*32));
        }
        ProjectionNodeVersion(ProjectionNodeVersion before,ulong version,ProjectionEntryPage[] pages)
        {Id=before.Id;Version=version;Kind=before.Kind;EntryCount=before.EntryCount;_pages=pages;_issuer=before._issuer;}
        internal ProjectionNodeVersion ReplaceValue(ulong version,int index,ProjectionValue value)
        {
            if(version<=Version||index<0||index>=EntryCount)throw new ArgumentException("projection_entry_update_binding");
            if(value.IsChild&&value.Child.Registry!=Id.Registry)throw new ArgumentException("projection_cross_registry_child");
            // At most 128 page references plus one 32-entry leaf; all other pages are shared immutable values.
            var pages=(ProjectionEntryPage[])_pages.Clone();var old=pages[index/32];var entries=new ProjectionEntry[old.Count];
            for(int i=0;i<entries.Length;i++)entries[i]=old[i];
            entries[index%32]=new ProjectionEntry(entries[index%32].Key,value);pages[index/32]=new ProjectionEntryPage(entries,0,entries.Length);
            return new ProjectionNodeVersion(this,version,pages);
        }
        internal ProjectionEntry GetEntry(int index)
        {if(index<0||index>=EntryCount)throw new ArgumentOutOfRangeException(nameof(index));return _pages[index/32][index%32];}
        internal ProjectionEntryPage GetPage(int index)=>_pages[index];
    }
    // Radix32, exactly thirteen lookup/update levels for the full unsigned 64-bit key width.
    internal sealed class ProjectionVersionTable
    {
        internal const int Height=13;
        internal const int Fanout=32;
        readonly Page _root;
        internal ulong Registry{get;}
        internal int Count=>_root?.Count??0;
        internal long Units=>_root?.Units??0;
        internal ProjectionVersionTable(ulong registry){if(registry==0)throw new ArgumentException("invalid_registry");Registry=registry;}
        ProjectionVersionTable(ulong registry,Page root){Registry=registry;_root=root;}
        internal ProjectionNodeVersion Get(ProjectionNodeId id)
        {
            if(id.Registry!=Registry||id.Value==0)return null;
            Page page=_root;
            for(int level=12;level>0;level--){if(page==null)return null;page=page.Children[Index(id.Value,level)];}
            return page?.Values[Index(id.Value,0)];
        }
        internal ProjectionVersionTable Set(ProjectionNodeId id,ProjectionNodeVersion value)
        {
            if(id.Registry!=Registry||id.Value==0||value!=null&&!value.Id.Equals(id))throw new ArgumentException("projection_table_binding");
            return new ProjectionVersionTable(Registry,Update(_root,id.Value,12,value));
        }
        static int Index(ulong id,int level)=>(int)((id>>(level*5))&31UL);
        static Page Update(Page before,ulong id,int level,ProjectionNodeVersion value)
        {
            if(level==0)
            {
                var values=before==null?new ProjectionNodeVersion[32]:(ProjectionNodeVersion[])before.Values.Clone();
                values[Index(id,0)]=value;var page=new Page(values);return page.Count==0?null:page;
            }
            var children=before==null?new Page[32]:(Page[])before.Children.Clone();
            int at=Index(id,level);children[at]=Update(children[at],id,level-1,value);var next=new Page(children);return next.Count==0?null:next;
        }
        sealed class Page
        {
            internal readonly Page[] Children;
            internal readonly ProjectionNodeVersion[] Values;
            internal readonly int Count;
            internal readonly long Units;
            internal Page(Page[] children)
            {Children=children;int count=0;long units=1;foreach(var child in children){count+=child?.Count??0;units+=child?.Units??0;}Count=count;Units=units;}
            internal Page(ProjectionNodeVersion[] values)
            {Values=values;int count=0;long units=1;foreach(var value in values)if(value!=null){count++;units+=value.Units;}Count=count;Units=units;}
        }
    }
}
