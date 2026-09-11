using System.Collections.Generic;
using System.Globalization;
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

        public IReadOnlyList<string> StatusChipTexts => _chipTexts;
        readonly List<string> _chipTexts = new List<string>();

        public Meter Health => _health;
        public Meter Oxygen => _oxygen;
        public Meter Stamina => _stamina;
        public string WorkLine => _workLine.text;

        public HudVitalsCluster()
        {
            AddToClassList("ss-panel");
            AddToClassList("hud-cluster");
            name = "hud-vitals-cluster";
            Add(_health);
            Add(_oxygen);
            Add(_stamina);
            _statusRow.AddToClassList("hud-status-row");
            Add(_statusRow);
            _workLine.AddToClassList("ss-label");
            _workLine.AddToClassList("ss-label--secondary");
            Add(_workLine);
        }

        /// <summary>Refresh from the vitals model (call when the model changes, not every frame).</summary>
        public void Refresh(PlayerVitalsModel model) => Refresh(model.GetVitalsSummary());

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
            chips.Sort((a, b) => b.rank.CompareTo(a.rank));

            _statusRow.Clear();
            _chipTexts.Clear();
            foreach (var (text, rank) in chips)
            {
                var chip = new Label(text);
                chip.AddToClassList("hud-status-chip");
                if (rank == 2) chip.AddToClassList("hud-status-chip--danger");
                else if (rank == 1) chip.AddToClassList("hud-status-chip--caution");
                _statusRow.Add(chip);
                _chipTexts.Add(text);
            }

            string repair = s.GetString("repair_line");
            _workLine.text = repair;
            _workLine.style.display = string.IsNullOrEmpty(repair) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
