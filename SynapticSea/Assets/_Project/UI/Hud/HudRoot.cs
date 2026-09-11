using UnityEngine;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// HUD document root: applies the theme root class and the accessibility text scale (1x / 1.5x / 2x by reflow),
    /// and hosts the persistent HUD pieces.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class HudRoot : MonoBehaviour
    {
        public enum TextScale { X100, X150, X200 }

        public HudVitalsCluster Vitals { get; private set; }
        public VisualElement Root { get; private set; }

        [SerializeField] TextScale textScale = TextScale.X100;

        void OnEnable() => Build(GetComponent<UIDocument>().rootVisualElement);

        /// <summary>Builds the HUD tree under <paramref name="documentRoot"/> (public for tests and tools).</summary>
        public VisualElement Build(VisualElement documentRoot)
        {
            documentRoot.Clear();
            Root = new VisualElement { name = "hud-root" };
            Root.AddToClassList("ss-root");
            Root.AddToClassList("hud-root");
            Root.pickingMode = PickingMode.Ignore;
            documentRoot.Add(Root);
            Vitals = new HudVitalsCluster();
            Root.Add(Vitals);
            ApplyTextScale(textScale);
            return Root;
        }

        public void ApplyTextScale(TextScale scale)
        {
            textScale = scale;
            if (Root == null) return;
            Root.EnableInClassList("scale-150", scale == TextScale.X150);
            Root.EnableInClassList("scale-200", scale == TextScale.X200);
        }
    }
}
