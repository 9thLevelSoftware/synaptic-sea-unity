namespace SynapticSea.Core.Variant
{
    /// <summary>
    /// Shorthands for the most common GDScript access patterns, e.g.
    /// <c>int(d.get("k", 0))</c> becomes <c>d.GetInt("k", 0)</c>.
    /// A key that is present with a null value yields the fallback (GDScript would error on <c>int(null)</c>).
    /// </summary>
    public static class GdDictExtensions
    {
        public static long GetInt(this GdDict d, object key, long fallback = 0)
        {
            object v = d?.Get(key);
            return v == null ? fallback : V.I64(v, fallback);
        }

        public static double GetFloat(this GdDict d, object key, double fallback = 0.0)
        {
            object v = d?.Get(key);
            return v == null ? fallback : V.F64(v, fallback);
        }

        public static string GetString(this GdDict d, object key, string fallback = "")
        {
            object v = d?.Get(key);
            return v == null ? fallback : V.Str(v);
        }

        public static bool GetBool(this GdDict d, object key, bool fallback = false)
        {
            object v = d?.Get(key);
            return v == null ? fallback : V.Bool(v, fallback);
        }

        /// <summary>The value when it is a dictionary, else <paramref name="fallback"/> (null by default).</summary>
        public static GdDict GetDict(this GdDict d, object key, GdDict fallback = null) => d?.Get(key) as GdDict ?? fallback;

        /// <summary>The value when it is an array, else <paramref name="fallback"/> (null by default).</summary>
        public static GdArray GetArray(this GdDict d, object key, GdArray fallback = null) => d?.Get(key) as GdArray ?? fallback;

        /// <summary>The value as a dictionary, or a new empty one (the common <c>d.get(k, {})</c> pattern).</summary>
        public static GdDict GetDictOrEmpty(this GdDict d, object key) => d?.Get(key) as GdDict ?? new GdDict();

        /// <summary>The value as an array, or a new empty one (the common <c>d.get(k, [])</c> pattern).</summary>
        public static GdArray GetArrayOrEmpty(this GdDict d, object key) => d?.Get(key) as GdArray ?? new GdArray();
    }
}
