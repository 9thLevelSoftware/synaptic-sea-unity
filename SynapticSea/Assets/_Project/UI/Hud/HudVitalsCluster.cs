// Ported from scripts/ui/player_vitals_panel.gd @ 96ecb2b0 (presents its PlayerVitalsModel directly instead of pushed text lines)
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// Lower-left primary HUD cluster (ui_presentation_program.md): health, personal oxygen, and stamina readable
    /// without hover, plus severity-ranked urgent status chips and the active repair/work line. Presents
    /// <see cref="PlayerVitalsModel.GetVitalsSummary"/> (the Godot player_vitals_panel's model) — it never computes
    /// gameplay values itself.
    /// </summary>
    public sealed class HudVitalsCluster : VisualElement
    {
        public const double OxygenLowThreshold = 30.0;

        readonly Meter _health = new Meter("Health");
        readonly Meter _oxygen = new Meter("Suit O2");
        readonly Meter _stamina = new Meter("Stamina");
        readonly VisualElement _statusRow = new VisualElement();
        readonly Label _workLine = new Label();
        readonly Label _damageIndicator = new Label();
        readonly Label _weaponLine = new Label();
        readonly VisualElement _effectIcons = new VisualElement();
        readonly List<string> _effectIconIds = new List<string>();

        public IReadOnlyList<string> StatusChipTexts => _chipTexts;
        readonly List<string> _chipTexts = new List<string>();

        public Meter Health => _health;
        public Meter Oxygen => _oxygen;
        public Meter Stamina => _stamina;
        public string WorkLine => _workLine.text;

        /// <summary>The combat line (Godot hotbar_panel text: weapon | ammo | threat awareness | combat/stealth).</summary>
        public string WeaponLine => _weaponLine.text;

        /// <summary>The transient "hit" indicator text ("" when hidden).</summary>
        public string DamageIndicatorText => _damageIndicator.style.display == DisplayStyle.None ? "" : _damageIndicator.text;

        /// <summary>Status effect ids that currently show an icon chip.</summary>
        public IReadOnlyList<string> EffectIconIds => _effectIconIds;

        /// <summary>Quick-use / weapon slot row inside the cluster (spec: not a separate full-width bar).</summary>
        public VisualElement QuickUseSlot { get; } = new VisualElement();

        /// <summary>The model's status lines (Godot's pushed panel text), kept for the survivor detail view.</summary>
        public string GetHudText() => string.Join("\n", _statusLines);
        readonly List<string> _statusLines = new List<string>();

        public HudVitalsCluster()
        {
            AddToClassList("ss-panel");
            AddToClassList("hud-cluster");
            name = "hud-vitals-cluster";
            _damageIndicator.name = "hud-damage-indicator";
            _damageIndicator.AddToClassList("hud-status-chip");
            _damageIndicator.AddToClassList("hud-status-chip--danger");
            _damageIndicator.style.display = DisplayStyle.None;
            Add(_damageIndicator);
            Add(_health);
            Add(_oxygen);
            Add(_stamina);
            _statusRow.AddToClassList("hud-status-row");
            Add(_statusRow);
            _effectIcons.name = "hud-effect-icons";
            _effectIcons.style.flexDirection = FlexDirection.Row;
            _effectIcons.style.flexWrap = Wrap.Wrap;
            _effectIcons.style.display = DisplayStyle.None;
            _effectIcons.pickingMode = PickingMode.Ignore;
            Add(_effectIcons);
            _workLine.AddToClassList("ss-label");
            _workLine.AddToClassList("ss-label--secondary");
            Add(_workLine);
            _weaponLine.name = "hud-weapon-line";
            _weaponLine.AddToClassList("ss-label");
            _weaponLine.AddToClassList("ss-label--secondary");
            _weaponLine.AddToClassList("ss-label--mono");
            _weaponLine.style.display = DisplayStyle.None;
            Add(_weaponLine);
            QuickUseSlot.AddToClassList("hud-quick-use");
            QuickUseSlot.pickingMode = PickingMode.Ignore;
            Add(QuickUseSlot);
            pickingMode = PickingMode.Ignore;
        }

        /// <summary>Refresh from the vitals model (call when the model changes, not every frame).</summary>
        public void Refresh(PlayerVitalsModel model)
        {
            _statusLines.Clear();
            _statusLines.AddRange(model.GetStatusLines());
            Refresh(model.GetVitalsSummary());
        }

        public void Refresh(GdDict s)
        {
            double health = s.GetFloat("health", 0);
            double oxygen = s.GetFloat("oxygen", 0);
            double stamina = s.GetFloat("stamina", 0);
            _health.Set(health, 100, "", health <= 25 ? Meter.Severity.Danger : health <= 50 ? Meter.Severity.Caution : Meter.Severity.Normal);
            _oxygen.Set(oxygen, 100, "%", oxygen <= OxygenLowThreshold ? Meter.Severity.Danger : oxygen <= 50 ? Meter.Severity.Caution : Meter.Severity.Normal);
            _stamina.Set(stamina, 100, "", stamina <= 20 ? Meter.Severity.Caution : Meter.Severity.Normal);

            // Severity-ranked urgent states: danger first, then caution; each with explicit wording.
            var chips = new List<(string text, int rank)>();
            string breach = s.GetString("breach_state");
            if (breach == "breach") chips.Add(("BREACH", 2));
            if (oxygen <= OxygenLowThreshold) chips.Add(("O2 LOW", 2));
            if (s.GetBool("heavy")) chips.Add(("OVERLOADED", 1));
            long rad = s.GetInt("radiation");
            if (rad >= 50) chips.Add(("RADIATION " + rad.ToString(CultureInfo.InvariantCulture), 2));
            else if (rad > 0) chips.Add(("RADIATION " + rad.ToString(CultureInfo.InvariantCulture), 1));
            if (s.GetInt("sanity", 100) <= 30) chips.Add(("SANITY", 1));
            long effects = s.GetInt("status_effects_count");
            if (effects > 0) chips.Add(("EFFECTS " + effects.ToString(CultureInfo.InvariantCulture), 0));
            if (breach == "sealed") chips.Add(("SEALED", 0));
            // Stable rank order (List.Sort is unstable and would reorder equal-rank chips between refreshes).
            _chips = chips.Select((c, i) => (c.text, c.rank, i)).OrderByDescending(c => c.rank).ThenBy(c => c.i)
                .Select(c => (c.text, c.rank)).ToList();
            _chipTexts.Clear();
            foreach (var (text, _) in _chips) _chipTexts.Add(text);
            RenderChips();

            string repair = s.GetString("repair_line");
            _workLine.text = repair;
            _workLine.style.display = string.IsNullOrEmpty(repair) || _workLineSuppressed ? DisplayStyle.None : DisplayStyle.Flex;
        }

        List<(string text, int rank)> _chips = new List<(string, int)>();
        bool _compact;

        /// <summary>Chips as displayed (symbol + wording); in compact mode lower-severity states collapse into "+N".</summary>
        public IReadOnlyList<string> VisibleChipTexts => _statusRow.Query<Label>().ToList().Select(l => l.text).ToList();

        /// <summary>
        /// Disclosure at 1.5x/2x text: every danger state stays as its own chip (imminent danger is shown immediately);
        /// caution/info states collapse into one "+N" chip whose wording carries the highest collapsed severity. Full
        /// detail remains available through <see cref="StatusChipTexts"/> / the survivor view.
        /// </summary>
        /// <param name="maxChips">Chips shown individually in compact mode (2 at 1.5x, 1 at 2x); the rest collapse into "+N".</param>
        public void SetCompact(bool compact, int maxChips = 2)
        {
            if (_compact == compact && CompactChipCount == maxChips) return;
            _compact = compact;
            CompactChipCount = System.Math.Max(1, maxChips);
            RenderChips();
        }

        /// <summary>Chips shown individually in compact mode (the most severe first); the rest collapse into "+N".</summary>
        public int CompactChipCount { get; private set; } = 2;

        void RenderChips()
        {
            _statusRow.Clear();
            int collapsed = 0;
            int collapsedRank = 0;
            for (int i = 0; i < _chips.Count; i++)
            {
                var (text, rank) = _chips[i];
                if (_compact && i >= CompactChipCount)
                {
                    collapsed++;
                    collapsedRank = System.Math.Max(collapsedRank, rank);
                    continue;
                }
                _statusRow.Add(Chip(text, rank));
            }
            if (collapsed > 0)
            {
                string word = collapsedRank == 2 ? " danger" : collapsedRank == 1 ? " caution" : " more";
                _statusRow.Add(Chip("+" + collapsed.ToString(CultureInfo.InvariantCulture) + word, collapsedRank));
            }
        }

        /// <summary>The session's <c>HotbarText</c> (Godot's weapon/ammo/threat line); hidden when empty.</summary>
        public void SetWeaponLine(string text)
        {
            _weaponLine.text = text ?? "";
            _weaponLine.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>The transient damage chip at the top of the cluster; "" hides it.</summary>
        public void SetDamageIndicator(string text)
        {
            _damageIndicator.text = text ?? "";
            _damageIndicator.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>
        /// Active status effects as small icon chips (<paramref name="iconFor"/> resolves an effect id to a texture, null
        /// when the effect has no icon). Rebuilt only when the id list changes.
        /// </summary>
        public void SetStatusEffects(IReadOnlyList<string> effectIds, System.Func<string, UnityEngine.Texture2D> iconFor)
        {
            var ids = new List<string>();
            var textures = new List<UnityEngine.Texture2D>();
            if (effectIds != null && iconFor != null)
            {
                foreach (string id in effectIds)
                {
                    UnityEngine.Texture2D tex = iconFor(id);
                    if (tex == null) continue;
                    ids.Add(id);
                    textures.Add(tex);
                }
            }
            if (ids.SequenceEqual(_effectIconIds)) return;
            _effectIconIds.Clear();
            _effectIconIds.AddRange(ids);
            _effectIcons.Clear();
            for (int i = 0; i < ids.Count; i++)
            {
                var icon = new Image { image = textures[i], scaleMode = UnityEngine.ScaleMode.ScaleToFit, tooltip = ids[i] };
                icon.name = "effect:" + ids[i];
                icon.style.width = 20;
                icon.style.height = 20;
                icon.style.marginRight = 4;
                icon.pickingMode = PickingMode.Ignore;
                _effectIcons.Add(icon);
            }
            _effectIcons.style.display = ids.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>Hides the cluster's repair line while the richer work strip above the cluster shows the same work.</summary>
        public void SetWorkLineSuppressed(bool suppressed)
        {
            _workLineSuppressed = suppressed;
            _workLine.style.display = string.IsNullOrEmpty(_workLine.text) || suppressed ? DisplayStyle.None : DisplayStyle.Flex;
        }

        bool _workLineSuppressed;

        static Label Chip(string text, int rank)
        {
            string symbol = rank == 2 ? SeverityText.Symbol(Severity.Danger) : rank == 1 ? SeverityText.Symbol(Severity.Caution) : SeverityText.Symbol(Severity.Info);
            var chip = new Label(symbol + " " + text);
            chip.AddToClassList("hud-status-chip");
            if (rank == 2) chip.AddToClassList("hud-status-chip--danger");
            else if (rank == 1) chip.AddToClassList("hud-status-chip--caution");
            return chip;
        }
    }
}
