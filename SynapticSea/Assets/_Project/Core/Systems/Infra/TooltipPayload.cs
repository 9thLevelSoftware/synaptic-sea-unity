// Ported from scripts/systems/tooltip_payload.gd @ 96ecb2b0
using System;
using SynapticSea.Core.Variant;

namespace SynapticSea.Core.Systems
{
    /// <summary>
    /// Read-only payload returned by <c>TooltipPresenter.resolve()</c>. The footer "[glyph] action_label"
    /// is split so the panel can swap the glyph per controller scheme.
    /// </summary>
    public class TooltipPayload
    {
        public string Title = "";
        public string Body = "";
        public string Footer = "";
        public string FooterGlyph = "";
        public string FooterActionLabel = "";
        public string SubjectKind = "";
        public string SubjectId = "";

        public TooltipPayload(string pTitle = "", string pBody = "", string pFooter = "", string pKind = "", string pId = "")
        {
            Title = pTitle;
            Body = pBody;
            Footer = pFooter;
            SubjectKind = pKind;
            SubjectId = pId;
            SplitFooter();
        }

        void SplitFooter()
        {
            // Footer convention: "[glyph] action_label", e.g. "[E] Pick up".
            string stripped = InfraCompat.StripEdges(Footer);
            if (stripped.Length == 0) return;
            if (!stripped.StartsWith("[", StringComparison.Ordinal))
            {
                FooterActionLabel = stripped;
                return;
            }
            int endBracket = stripped.IndexOf(']');
            if (endBracket < 0)
            {
                FooterActionLabel = stripped;
                return;
            }
            FooterGlyph = stripped.Substring(0, endBracket + 1);
            string rest = InfraCompat.StripEdges(stripped.Substring(endBracket + 1));
            FooterActionLabel = rest;
        }

        public GdDict ToDict()
        {
            return new GdDict
            {
                { "title", Title },
                { "body", Body },
                { "footer", Footer },
                { "footer_glyph", FooterGlyph },
                { "footer_action_label", FooterActionLabel },
                { "subject_kind", SubjectKind },
                { "subject_id", SubjectId },
            };
        }

        public static TooltipPayload FromDict(GdDict d)
        {
            return new TooltipPayload(
                V.Str(d.Get("title", "")),
                V.Str(d.Get("body", "")),
                V.Str(d.Get("footer", "")),
                V.Str(d.Get("subject_kind", "")),
                V.Str(d.Get("subject_id", ""))
            );
        }
    }
}
