// Ported from scripts/ui/audio_log_panel.gd and scripts/ui/audio_settings_panel.gd @ 96ecb2b0
using System;
using System.Collections.Generic;
using SynapticSea.Core.Systems;
using SynapticSea.Core.Variant;
using UnityEngine.UIElements;

namespace SynapticSea.UI
{
    /// <summary>
    /// REQ-AU-006 / ADR-0029 voice-log list (records screen, PAUSED): entries from the manager's AudioLog registry,
    /// Play / Stop, and the "Playing: …" transcript status. Unity departure: moving the cursor no longer auto-plays
    /// (Godot's ItemList.item_selected fired on every keyboard step); Submit, double click, or Play starts the entry.
    /// </summary>
    public sealed class AudioLogPanel : SurfacePanel
    {
        IUiAudio _audio;
        readonly SelectableList _list;
        readonly Label _playing;
        readonly List<string> _ids = new List<string>();
        int _selected;

        public event Action BackRequested;

        public AudioLogPanel() : base("AUDIO LOG", SurfaceTime.Paused)
        {
            _list = new SelectableList("voicelog", "No voice logs.");
            Body.Add(_list);
            var tools = UiFactory.Box(UiClasses.Toolbar);
            tools.Add(UiFactory.Button("Play", () => PlaySelected(), "act:play"));
            tools.Add(UiFactory.Button("Stop", () => Stop(), "act:stop"));
            Body.Add(tools);
            _playing = UiFactory.Text("(no entry playing)", UiClasses.LabelSecondary);
            Body.Add(_playing);
            _list.SelectionRequested += i =>
            {
                _selected = i;
                RenderList();
            };
            _list.ActivateRequested += i =>
            {
                _selected = i;
                PlaySelected();
            };
        }

        public override string SurfaceId => "audio_log";

        public IUiAudio AudioManager => _audio;

        public void SetAudioManager(IUiAudio audio)
        {
            _audio = audio;
            PopulateEntries();
        }

        /// <summary>Listed entries (the coordinator's populated gate reads this).</summary>
        public int GetEntryCount() => _ids.Count;

        public string StatusLabelText => _playing.text;
        public SelectableList List => _list;

        void PopulateEntries()
        {
            _ids.Clear();
            if (_audio?.AudioLog != null)
            {
                foreach (object id in _audio.AudioLog.ListEntryIds()) _ids.Add(V.Str(id));
            }
            RenderList();
            RefreshStatus();
        }

        void RenderList()
        {
            var items = new List<SelectableList.Item>();
            foreach (string id in _ids)
            {
                GdDict entry = _audio.AudioLog.GetEntry(id);
                bool playing = _audio.CurrentVoiceLogId == id;
                items.Add(new SelectableList.Item { Id = id, Text = V.Str(entry.Get("label", id)), Chip = playing ? "▶" : "" });
            }
            _list.SetItems(items, _selected);
        }

        void RefreshStatus()
        {
            if (_audio == null) return;
            string current = _audio.CurrentVoiceLogId;
            if (string.IsNullOrEmpty(current))
            {
                _playing.text = "(no entry playing)";
            }
            else
            {
                GdDict entry = _audio.AudioLog != null ? _audio.AudioLog.GetEntry(current) : new GdDict();
                _playing.text = "Playing: " + V.Str(entry.Get("label", current));
            }
        }

        public void SelectEntry(int index)
        {
            if (index < 0 || index >= _ids.Count) return;
            _selected = index;
            RenderList();
        }

        public void PlaySelected()
        {
            if (_audio == null || _selected < 0 || _selected >= _ids.Count) return;
            _audio.PlayVoiceLog(_ids[_selected]);
            RefreshStatus();
            RenderList();
        }

        public void Stop()
        {
            if (_audio == null) return;
            _audio.StopVoiceLog();
            RefreshStatus();
            RenderList();
        }

        protected override bool OnCommand(UiCommand command)
        {
            switch (command)
            {
                case UiCommand.Up:
                case UiCommand.Down:
                    if (_ids.Count == 0) return true;
                    SelectEntry(_list.StepFrom(_selected, command == UiCommand.Up ? -1 : 1));
                    return true;
                case UiCommand.Accept:
                    PlaySelected();
                    return true;
            }
            return false;
        }

        protected override void RequestClose() => BackRequested?.Invoke();

        protected override VisualElement InitialFocusElement() => _list.Count > 0 ? _list.RowAt(Math.Max(0, _list.SelectedIndex)) : CloseButton;
    }

    /// <summary>
    /// REQ-AU-008 audio settings (records screen, PAUSED): per-bus volume sliders (−60..0 dB) and mute toggles through
    /// the manager's setters, plus the captions and voice-log toggles. ADR-0044: captions are written ONLY to the
    /// coordinator's SettingsState, then pushed through the injected settings push (the coordinator's settings_changed
    /// seam) — never to the SFX router directly.
    /// </summary>
    public sealed class AudioSettingsPanel : SurfacePanel
    {
        public static readonly IReadOnlyList<string> BusList = new[]
        {
            AudioEventSeam.BUS_MASTER, AudioEventSeam.BUS_SFX, AudioEventSeam.BUS_MUSIC, AudioEventSeam.BUS_VOICE,
            AudioEventSeam.BUS_UI, AudioEventSeam.BUS_AMBIENT, AudioEventSeam.BUS_META,
        };

        IUiAudio _audio;
        SettingsState _settings;
        Action _settingsPush;
        readonly Dictionary<string, SliderInt> _sliders = new Dictionary<string, SliderInt>();
        readonly Dictionary<string, Toggle> _mutes = new Dictionary<string, Toggle>();
        readonly Toggle _captions;
        readonly Toggle _voiceLog;

        public event Action BackRequested;

        public AudioSettingsPanel() : base("AUDIO SETTINGS", SurfaceTime.Paused)
        {
            var list = UiFactory.Box("ss-settings-list");
            foreach (string bus in BusList)
            {
                string b = bus;
                var row = UiFactory.Box("ss-settings-row");
                row.Add(UiFactory.Text(GdString.Capitalize(b), "ss-settings-row__label"));
                var slider = new SliderInt((int)AudioBusConfig.MIN_DB, (int)AudioBusConfig.MAX_DB) { showInputField = false };
                slider.AddToClassList("ss-slider");
                slider.AddToClassList(UiClasses.Focusable);
                UiFocus.Tag(slider, "bus:" + b);
                slider.RegisterValueChangedCallback(e => OnVolumeChanged(b, e.newValue));
                var mute = new Toggle("Mute");
                mute.AddToClassList("ss-toggle");
                UiFocus.Tag(mute, "mute:" + b);
                mute.RegisterValueChangedCallback(e => OnMuteChanged(b, e.newValue));
                row.Add(slider);
                row.Add(mute);
                list.Add(row);
                _sliders[b] = slider;
                _mutes[b] = mute;
            }
            _captions = new Toggle("Closed captions");
            _captions.AddToClassList("ss-toggle");
            UiFocus.Tag(_captions, "toggle:captions");
            _captions.RegisterValueChangedCallback(e => OnCaptionToggled(e.newValue));
            _voiceLog = new Toggle("Voice log");
            _voiceLog.AddToClassList("ss-toggle");
            UiFocus.Tag(_voiceLog, "toggle:voice_log");
            list.Add(_captions);
            list.Add(_voiceLog);
            Body.Add(list);
        }

        public override string SurfaceId => "audio_settings";

        public IUiAudio AudioManager => _audio;

        public void SetAudioManager(IUiAudio audio)
        {
            _audio = audio;
            RefreshFromManager();
        }

        public void SetSettingsState(SettingsState state)
        {
            _settings = state;
            RefreshFromManager();
        }

        /// <summary>ADR-0044 seam: the coordinator's settings_changed re-emit.</summary>
        public void SetSettingsPush(Action push) => _settingsPush = push;

        public SliderInt VolumeSlider(string busId) => _sliders.TryGetValue(busId, out SliderInt s) ? s : null;
        public Toggle MuteToggle(string busId) => _mutes.TryGetValue(busId, out Toggle t) ? t : null;
        public Toggle CaptionToggle => _captions;
        public Toggle VoiceLogToggle => _voiceLog;

        public void RefreshFromManager()
        {
            if (_audio != null)
            {
                foreach (string bus in BusList)
                {
                    _sliders[bus].SetValueWithoutNotify((int)GdMath.Round(_audio.GetBusVolume(bus)));
                    _mutes[bus].SetValueWithoutNotify(_audio.IsBusMuted(bus));
                }
                if (_audio.AudioLog != null) _voiceLog.SetValueWithoutNotify(true);
            }
            if (_settings != null) _captions.SetValueWithoutNotify(_settings.IsCaptionsEnabled());
        }

        public void OnVolumeChanged(string busId, double value) => _audio?.SetBusVolume(busId, value);

        public void OnMuteChanged(string busId, bool pressed) => _audio?.SetBusMuted(busId, pressed);

        public void OnCaptionToggled(bool pressed)
        {
            if (_settings == null) return;
            _settings.SetCaptionsEnabled(pressed);
            _settingsPush?.Invoke();
        }

        protected override void RequestClose() => BackRequested?.Invoke();

        protected override VisualElement InitialFocusElement() => _sliders[AudioEventSeam.BUS_MASTER];
    }
}
