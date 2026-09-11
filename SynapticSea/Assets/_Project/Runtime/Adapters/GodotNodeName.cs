using System.Text;

namespace SynapticSea.Runtime
{
    /// <summary>
    /// Godot's <c>String.validate_node_name()</c>, applied when a node is named: the characters
    /// <c>. : @ / " %</c> become <c>_</c> (so an objective id <c>cargo_01:cache</c> yields the node
    /// <c>ObjectiveVolume_seq1_…_cargo_01_cache</c>). GameObjects built from Godot names use it so name-based lookups
    /// written against the Godot scene keep working.
    /// </summary>
    public static class GodotNodeName
    {
        const string Invalid = ".:@/\"%";

        public static string Validate(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(Invalid.ToCharArray()) < 0) return name ?? "";
            var sb = new StringBuilder(name);
            for (int i = 0; i < sb.Length; i++)
                if (Invalid.IndexOf(sb[i]) >= 0) sb[i] = '_';
            return sb.ToString();
        }
    }
}
