using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SynapticSea.Core.Session;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>SHA-256 over the key-sorted ComponentDomainCodec encoding of a value. Same digest as the former PaidCraftingState.Hash.</summary>
    public static class CanonicalHash
    {
        public static string Of(object value)
        {
            using (var sha = SHA256.Create())
            {
                GdDict envelope = ComponentDomainCodec.Encode(new GdDict { { "value", Sorted(value) } });
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(GdJson.Stringify(envelope)));
                return string.Concat(bytes.Select(b => b.ToString("x2")));
            }
        }
        static object Sorted(object value)
        {
            if (value is GdDict dict)
            {
                var copy = new GdDict();
                foreach (object key in dict.Keys.OrderBy(V.Str, StringComparer.Ordinal)) copy[key] = Sorted(dict[key]);
                return copy;
            }
            if (value is GdArray array) return new GdArray(array.Select(Sorted));
            return value;
        }
    }
}
