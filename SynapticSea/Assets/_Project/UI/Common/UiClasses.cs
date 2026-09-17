using System.Collections.Generic;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>USS class names shared by every presenter (Content/UI/Theme/{base,hud,panels}.uss).</summary>
    public static class UiClasses
    {
        public const string Root = "ss-root";
        public const string Panel = "ss-panel";
        public const string Label = "ss-label";
        public const string LabelSecondary = "ss-label--secondary";
        public const string LabelMono = "ss-label--mono";
        public const string LabelHeading = "ss-label--heading";
        public const string Focusable = "ss-focusable";
        public const string Button = "ss-button";

        public const string Surface = "ss-surface";
        public const string SurfaceLive = "ss-surface--live";
        public const string SurfacePaused = "ss-surface--paused";
        public const string SurfaceTerminal = "ss-surface--terminal";
        public const string SurfaceCovered = "ss-surface--covered";
        public const string SurfaceHeader = "ss-surface__header";
        public const string SurfaceTitle = "ss-surface__title";
        public const string SurfaceBody = "ss-surface__body";
        public const string SurfaceActions = "ss-surface__actions";
        public const string Badge = "ss-badge";
        public const string BadgeLive = "ss-badge--live";
        public const string BadgePaused = "ss-badge--paused";

        public const string List = "ss-list";
        public const string Row = "ss-row";
        public const string RowSelected = "ss-row--selected";
        public const string RowMarked = "ss-row--marked";
        public const string RowMuted = "ss-row--muted";
        public const string RowText = "ss-row__text";
        public const string RowDetail = "ss-row__detail";
        public const string RowChip = "ss-row__chip";
        public const string Empty = "ss-empty";
        public const string Status = "ss-status";
        public const string Detail = "ss-detail";
        public const string Section = "ss-section";
        public const string SectionTitle = "ss-section__title";
        public const string Columns = "ss-columns";
        public const string Pane = "ss-pane";
        public const string PaneActive = "ss-pane--active";
        public const string Chip = "ss-chip";
        public const string Glyph = "ss-glyph";
        public const string Toolbar = "ss-toolbar";
        public const string DropTarget = "ss-drop-target";
        public const string DropTargetHot = "ss-drop-target--hot";

        public const string ReduceMotion = "reduce-motion";
        public const string ColorblindPrefix = "cb-";

        public const string SevInfo = "ss-sev--info";
        public const string SevSuccess = "ss-sev--success";
        public const string SevCaution = "ss-sev--caution";
        public const string SevDanger = "ss-sev--danger";

        public static readonly IReadOnlyList<string> ColorblindModes = new[] { "none", "protanopia", "deuteranopia", "tritanopia" };
    }

    /// <summary>Severity is always wording + symbol + colour (spec: never hue alone; symbols exist in Inter and JetBrains Mono).</summary>
    public enum Severity
    {
        None,
        Info,
        Success,
        Caution,
        Danger,
    }

    public static class SeverityText
    {
        public static string Symbol(Severity s)
        {
            switch (s)
            {
                case Severity.Info: return "●";
                case Severity.Success: return "✓";
                case Severity.Caution: return "▲";
                case Severity.Danger: return "⚠";
                default: return "";
            }
        }

        public static string Word(Severity s)
        {
            switch (s)
            {
                case Severity.Info: return "Info";
                case Severity.Success: return "OK";
                case Severity.Caution: return "Caution";
                case Severity.Danger: return "Danger";
                default: return "";
            }
        }

        public static string ClassFor(Severity s)
        {
            switch (s)
            {
                case Severity.Info: return UiClasses.SevInfo;
                case Severity.Success: return UiClasses.SevSuccess;
                case Severity.Caution: return UiClasses.SevCaution;
                case Severity.Danger: return UiClasses.SevDanger;
                default: return "";
            }
        }

        /// <summary>"▲ Caution: text" / "⚠ Danger: text" / "✓ text" / "● text"; plain text for None.</summary>
        public static string Format(Severity s, string text)
        {
            switch (s)
            {
                case Severity.Caution:
                case Severity.Danger:
                    return Symbol(s) + " " + Word(s) + ": " + text;
                case Severity.Info:
                case Severity.Success:
                    return Symbol(s) + " " + text;
                default:
                    return text;
            }
        }

        /// <summary>Sets exactly one severity class on <paramref name="el"/>.</summary>
        public static void Apply(VisualElement el, Severity s)
        {
            el.EnableInClassList(UiClasses.SevInfo, s == Severity.Info);
            el.EnableInClassList(UiClasses.SevSuccess, s == Severity.Success);
            el.EnableInClassList(UiClasses.SevCaution, s == Severity.Caution);
            el.EnableInClassList(UiClasses.SevDanger, s == Severity.Danger);
        }
    }
}
