using System.Globalization;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>A labelled meter (name + numeric reading + bar). Readings are always text, never bar alone.</summary>
    public sealed class Meter : VisualElement
    {
        public enum Severity { Normal, Caution, Danger }

        readonly Label _name;
        readonly Label _value;
        readonly VisualElement _fill;

        public Severity CurrentSeverity { get; private set; }
        public string ValueText => _value.text;

        readonly string _baseName;

        public Meter(string name)
        {
            _baseName = name;
            AddToClassList("ss-meter");
            var header = new VisualElement();
            header.AddToClassList("ss-meter__header");
            _name = new Label(name);
            _name.AddToClassList("ss-label");
            _value = new Label();
            _value.AddToClassList("ss-label");
            _value.AddToClassList("ss-label--mono");
            header.Add(_name);
            header.Add(_value);
            Add(header);

            var track = new VisualElement();
            track.AddToClassList("ss-meter__track");
            _fill = new VisualElement();
            _fill.AddToClassList("ss-meter__fill");
            track.Add(_fill);
            Add(track);
        }

        /// <summary>Sets the reading (0..max), the suffix text, and the severity class.</summary>
        public void Set(double value, double max, string suffix, Severity severity)
        {
            double ratio = max <= 0 ? 0 : System.Math.Max(0, System.Math.Min(1, value / max));
            _fill.style.width = Length.Percent((float)(ratio * 100.0));
            _value.text = System.Math.Round(value, System.MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + suffix;
            EnableInClassList("ss-meter--caution", severity == Severity.Caution);
            EnableInClassList("ss-meter--danger", severity == Severity.Danger);
            CurrentSeverity = severity;
            // Severity is never hue alone: the name carries the caution/danger symbol (the status chips carry the wording).
            _name.text = severity == Severity.Danger ? "⚠ " + _baseName : severity == Severity.Caution ? "▲ " + _baseName : _baseName;
        }

        public string NameText => _name.text;
    }
}
