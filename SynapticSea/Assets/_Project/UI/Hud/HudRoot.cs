using System.Collections.Generic;
using System.Linq;
using SynapticSea.UI.Presenters;
using UnityEngine;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// HUD document root (ui_presentation_program.md "Persistent HUD and disclosure"): applies the theme root class and
    /// the text scale (1x / 1.5x / 2x by reflow), and zones the persistent HUD:
    /// <list type="bullet">
    /// <item>upper-left: one small objective chip (<see cref="Objective"/>);</item>
    /// <item>lower-left column: transient stack (tutorial, context prompt / tooltip, active work) directly above the
    /// primary cluster (health · O2 · stamina, urgent states, quick use).</item>
    /// </list>
    /// No minimap or radar (ADR-0045). Nothing persistent is placed in the protected centre or the lower-middle corridor.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class HudRoot : MonoBehaviour
    {
        public enum TextScale { X100, X150, X200 }

        public VisualElement Root { get; private set; }
        public HudVitalsCluster Vitals { get; private set; }
        public ObjectiveChip Objective { get; private set; }
        public WorkActionStrip Work { get; private set; }
        public VisualElement LeftColumn { get; private set; }
        public VisualElement Transients { get; private set; }
        public Label ContextPrompt { get; private set; }

        /// <summary>Screen-projected world labels (affordances, hazard warnings); behind the HUD, never picks.</summary>
        public VisualElement WorldLabelLayer { get; private set; }

        /// <summary>Short feedback toast (denials such as "No web chart"); a transient above the cluster.</summary>
        public Label Toast { get; private set; }

        /// <summary>Screen-edge damage flash (skipped under reduced motion).</summary>
        public VisualElement DamageFlash { get; private set; }

        [SerializeField] TextScale textScale = TextScale.X100;

        void OnEnable() => Build(GetComponent<UIDocument>().rootVisualElement);

        /// <summary>Builds the HUD tree under <paramref name="documentRoot"/> (public for tests and tools).</summary>
        public VisualElement Build(VisualElement documentRoot)
        {
            documentRoot.Clear();
            WorldLabelLayer = new VisualElement { name = "hud-world-labels" };
            WorldLabelLayer.pickingMode = PickingMode.Ignore;
            FillParent(WorldLabelLayer);
            documentRoot.Add(WorldLabelLayer);
            Root = new VisualElement { name = "hud-root" };
            Root.AddToClassList(UiClasses.Root);
            Root.AddToClassList("hud-root");
            Root.pickingMode = PickingMode.Ignore;
            documentRoot.Add(Root);

            Objective = new ObjectiveChip();
            Root.Add(Objective);

            LeftColumn = UiFactory.Box("hud-left-column");
            LeftColumn.name = "hud-left-column";
            LeftColumn.pickingMode = PickingMode.Ignore;
            Transients = UiFactory.Box("hud-transients");
            Transients.name = "hud-transients";
            Transients.pickingMode = PickingMode.Ignore;
            ContextPrompt = UiFactory.Text("", "hud-context-prompt");
            ContextPrompt.name = "hud-context-prompt";
            UiFactory.SetShown(ContextPrompt, false);
            Work = new WorkActionStrip();
            _slots.Clear();
            Vitals = new HudVitalsCluster();
            LeftColumn.Add(Transients);
            LeftColumn.Add(Vitals);
            Root.Add(LeftColumn);
            AddTransient(ContextPrompt, PriorityPrompt);
            AddTransient(Work, PriorityWork);
            Toast = UiFactory.Text("", "hud-context-prompt", "hud-toast");
            Toast.name = "hud-toast";
            UiFactory.SetShown(Toast, false);
            AddTransient(Toast, PriorityToast);
            DamageFlash = new VisualElement { name = "hud-damage-flash" };
            DamageFlash.pickingMode = PickingMode.Ignore;
            FillParent(DamageFlash);
            DamageFlash.style.borderTopWidth = DamageFlash.style.borderBottomWidth = DamageFlash.style.borderLeftWidth = DamageFlash.style.borderRightWidth = 14f;
            var flashColor = new Color(0.95f, 0.12f, 0.08f, 1f);
            DamageFlash.style.borderTopColor = DamageFlash.style.borderBottomColor = DamageFlash.style.borderLeftColor = DamageFlash.style.borderRightColor = flashColor;
            DamageFlash.style.opacity = 0f;
            UiFactory.SetShown(DamageFlash, false);
            documentRoot.Add(DamageFlash);
            _toastRemaining = 0f;
            _flashRemaining = 0f;

            Objective.PromptChanged += SetContextPrompt;
            SetContextPrompt(Objective.InteractionPrompt);
            ApplyTextScale(textScale);
            Root.schedule.Execute(RefreshTransients).Every(100);
            return Root;
        }

        // Feedback priority (spec "Feedback, tutorials, and horror"): active work > contextual instruction > detail
        // tooltip > tutorial. Critical danger lives in the cluster itself and is never suppressed.
        public const int PriorityWork = 0;
        public const int PriorityToast = 1;
        public const int PriorityPrompt = 1;
        public const int PriorityTooltip = 2;
        public const int PriorityTutorial = 3;

        readonly List<(VisualElement slot, VisualElement content, int priority)> _slots = new List<(VisualElement, VisualElement, int)>();

        /// <summary>How many transients may show at once: 2 at 1x, 1 at 1.5x and 2x (disclosure instead of smaller text;
        /// measured by HudLayoutTests so the column never reaches the objective chip).</summary>
        public int TransientLimit => textScale == TextScale.X100 ? 2 : 1;

        /// <summary>Adds a transient (it controls its own display; the HUD arbitrates by priority). Slots are ordered
        /// top→bottom by descending priority number, so active work sits directly above the cluster.</summary>
        public void AddTransient(VisualElement content, int priority)
        {
            var slot = UiFactory.Box("hud-transient-slot");
            slot.pickingMode = PickingMode.Ignore;
            slot.Add(content);
            int index = 0;
            while (index < _slots.Count && _slots[index].priority > priority) index++;
            _slots.Insert(index, (slot, content, priority));
            Transients.Insert(index, slot);
            RefreshTransients();
        }

        /// <summary>Shows the highest-priority transients that want to show, up to <see cref="TransientLimit"/>.</summary>
        public void RefreshTransients()
        {
            int shown = 0;
            int limit = TransientLimit;
            foreach (var entry in _slots.OrderBy(s => s.priority))
            {
                bool wants = UiFactory.IsShown(entry.content);
                bool show = wants && shown < limit;
                UiFactory.SetShown(entry.slot, show);
                if (show) shown++;
            }
            Vitals?.SetWorkLineSuppressed(Work != null && Work.IsOpen());
        }

        /// <summary>Short transient context prompt above the cluster (bound glyph, verb, target, blocker).</summary>
        public void SetContextPrompt(string text)
        {
            if (ContextPrompt == null) return;
            ContextPrompt.text = text ?? "";
            UiFactory.SetShown(ContextPrompt, !string.IsNullOrEmpty(text));
            RefreshTransients();
        }

        /// <summary>Mounts the coordinator-owned HUD pieces (tutorial, tooltip, hotbar) into their zones and lets the
        /// coordinator drive this root's text scale.</summary>
        public void Mount(MenuCoordinator coordinator)
        {
            if (Root == null || coordinator == null) return;
            AddTransient(coordinator.TutorialBanner, PriorityTutorial);
            AddTransient(coordinator.TooltipCard, PriorityTooltip);
            Vitals.QuickUseSlot.Clear();
            Vitals.QuickUseSlot.Add(coordinator.HotbarStrip);
            coordinator.RegisterScaleRoot(Root);
            ApplyDisclosure();
        }

        public void ApplyTextScale(TextScale scale)
        {
            textScale = scale;
            if (Root == null) return;
            Root.EnableInClassList(AccessibilitySettings.ClassScale150, scale == TextScale.X150);
            Root.EnableInClassList(AccessibilitySettings.ClassScale200, scale == TextScale.X200);
            ApplyDisclosure();
        }

        /// <summary>Large-text disclosure (reflow, never smaller text): compact status chips, quick-use names and work
        /// hint at 1.5x/2x, plus the transient limit.</summary>
        void ApplyDisclosure()
        {
            bool compact = textScale != TextScale.X100;
            Vitals?.SetCompact(compact, textScale == TextScale.X200 ? 1 : 2);
            Work?.SetCompact(compact);
            Vitals?.QuickUseSlot.Query<HotbarStrip>().ForEach(h => h.SetCompact(compact));
            RefreshTransients();
        }

        // ------------------------------------------------------------------ feedback (toasts, damage)

        public const float ToastSeconds = 2.5f;
        public const float DamageFlashSeconds = 0.35f;
        public const float DamageIndicatorSeconds = 1.5f;

        float _toastRemaining;
        float _flashRemaining;
        float _damageRemaining;
        bool _motionReduce;

        /// <summary>The last text shown by <see cref="ShowToast"/> (empty once it expired).</summary>
        public string ToastText => Toast != null && UiFactory.IsShown(Toast) ? Toast.text : "";

        /// <summary>True while the screen-edge flash is showing.</summary>
        public bool DamageFlashActive => _flashRemaining > 0f;

        /// <summary>Shows a short feedback line above the cluster (symbol + wording) for <see cref="ToastSeconds"/>.</summary>
        public void ShowToast(string text, Severity severity = Severity.Caution)
        {
            if (Toast == null || string.IsNullOrEmpty(text)) return;
            string symbol = SeverityText.Symbol(severity);
            Toast.text = symbol.Length != 0 ? symbol + " " + text : text;
            UiFactory.SetShown(Toast, true);
            _toastRemaining = ToastSeconds;
            RefreshTransients();
        }

        /// <summary>
        /// Player hit feedback: the cluster's damage indicator ("▲ Hit −12 · stalker") and, unless motion is reduced, a
        /// short screen-edge flash.
        /// </summary>
        public void ShowDamage(double damage, string source)
        {
            if (Root == null) return;
            string amount = damage.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            Vitals?.SetDamageIndicator(SeverityText.Symbol(Severity.Danger) + " Hit −" + amount + (string.IsNullOrEmpty(source) ? "" : " · " + source));
            _damageRemaining = DamageIndicatorSeconds;
            if (_motionReduce || DamageFlash == null) return;
            _flashRemaining = DamageFlashSeconds;
            UiFactory.SetShown(DamageFlash, true);
            DamageFlash.style.opacity = 0.85f;
        }

        void Update() => TickFeedback(Time.unscaledDeltaTime);

        /// <summary>Advances the toast / flash / indicator timers (public for tests).</summary>
        public void TickFeedback(float deltaSeconds)
        {
            if (_toastRemaining > 0f)
            {
                _toastRemaining -= deltaSeconds;
                if (_toastRemaining <= 0f && Toast != null)
                {
                    UiFactory.SetShown(Toast, false);
                    RefreshTransients();
                }
            }
            if (_damageRemaining > 0f)
            {
                _damageRemaining -= deltaSeconds;
                if (_damageRemaining <= 0f) Vitals?.SetDamageIndicator("");
            }
            if (_flashRemaining > 0f && DamageFlash != null)
            {
                _flashRemaining -= deltaSeconds;
                if (_flashRemaining <= 0f)
                {
                    DamageFlash.style.opacity = 0f;
                    UiFactory.SetShown(DamageFlash, false);
                }
                else
                {
                    DamageFlash.style.opacity = 0.85f * (_flashRemaining / DamageFlashSeconds);
                }
            }
        }

        static void FillParent(VisualElement e)
        {
            e.style.position = Position.Absolute;
            e.style.left = 0;
            e.style.top = 0;
            e.style.right = 0;
            e.style.bottom = 0;
        }

        /// <summary>Applies the reflow step, reduced motion and colour-blind classes from the settings sink.</summary>
        public void ApplyAccessibility(AccessibilitySettings settings)
        {
            if (Root == null || settings == null) return;
            _motionReduce = settings.IsMotionReduce();
            MenuCoordinator.ApplyAccessibilityClasses(Root, settings);
            int step = settings.ReflowStep();
            textScale = step == 200 ? TextScale.X200 : step == 150 ? TextScale.X150 : TextScale.X100;
            ApplyDisclosure();
        }
    }
}
