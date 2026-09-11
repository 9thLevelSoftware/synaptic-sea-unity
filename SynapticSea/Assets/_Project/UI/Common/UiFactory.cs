using System;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// A stable focus identity. Focus restores from a model token (item id / command id), never from a possibly-rebuilt
    /// element reference (spec: "focus restores from a stable item/command token rather than a potentially freed Control").
    /// </summary>
    public sealed class FocusTag
    {
        public readonly string Token;
        public FocusTag(string token) => Token = token ?? "";
    }

    public static class UiFocus
    {
        public static void Tag(VisualElement el, string token) => el.userData = new FocusTag(token);

        public static string TokenOf(VisualElement el) => (el?.userData as FocusTag)?.Token ?? "";

        public static VisualElement FindByToken(VisualElement root, string token)
        {
            if (root == null || string.IsNullOrEmpty(token)) return null;
            return root.Query<VisualElement>().Where(e => TokenOf(e) == token).First();
        }

        /// <summary>The focused element when it lives under <paramref name="root"/> (null outside a panel).</summary>
        public static VisualElement FocusedWithin(VisualElement root)
        {
            var focused = root?.panel?.focusController?.focusedElement as VisualElement;
            if (focused == null) return null;
            return root == focused || root.Contains(focused) ? focused : null;
        }

        /// <summary>Focuses when attached to a panel; a no-op headless.</summary>
        public static void Focus(VisualElement el)
        {
            if (el == null || el.panel == null || !el.focusable) return;
            el.Focus();
        }

        /// <summary>Prevents UI Toolkit's default focus navigation for an event a list/grid handled itself.</summary>
        public static void Consume(EventBase evt, VisualElement ctx)
        {
            evt.StopPropagation();
            ctx?.panel?.focusController?.IgnoreEvent(evt);
        }
    }

    public static class UiFactory
    {
        public static Label Text(string text, params string[] classes)
        {
            var label = new Label(text ?? "");
            label.AddToClassList(UiClasses.Label);
            foreach (string c in classes)
            {
                if (!string.IsNullOrEmpty(c)) label.AddToClassList(c);
            }
            return label;
        }

        /// <summary>A 44 px (row-height) button, focusable, tagged with <paramref name="token"/> for focus restore.</summary>
        public static UnityEngine.UIElements.Button Button(string text, Action onClick, string token)
        {
            var b = new UnityEngine.UIElements.Button(onClick) { text = text };
            b.AddToClassList(UiClasses.Button);
            b.AddToClassList(UiClasses.Focusable);
            b.focusable = true;
            b.tabIndex = 0;
            UiFocus.Tag(b, token);
            return b;
        }

        public static VisualElement Box(params string[] classes)
        {
            var el = new VisualElement();
            foreach (string c in classes)
            {
                if (!string.IsNullOrEmpty(c)) el.AddToClassList(c);
            }
            return el;
        }

        public static void SetShown(VisualElement el, bool shown) => el.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;

        /// <summary>Inline display state (works headless; undefined reads as Flex).</summary>
        public static bool IsShown(VisualElement el) => el.style.display.value != DisplayStyle.None;
    }

    /// <summary>A status/feedback line: text with severity wording + symbol + colour class.</summary>
    public sealed class StatusLine : Label
    {
        public string Raw { get; private set; } = "";
        public Severity Severity { get; private set; }

        public StatusLine()
        {
            AddToClassList(UiClasses.Label);
            AddToClassList(UiClasses.Status);
            Set("", Severity.None);
        }

        public void Set(string raw, Severity severity)
        {
            Raw = raw ?? "";
            Severity = severity;
            text = Raw.Length == 0 ? "" : SeverityText.Format(severity, Raw);
            SeverityText.Apply(this, severity);
            UiFactory.SetShown(this, Raw.Length != 0);
        }
    }
}
