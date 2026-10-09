// Unity-port addition (plan C1): the title's New Run setup. Godot's title started a run with the fixed default start
// (title_main.gd _instantiate_gameplay). Milestone A New Run boots golden coherent_ship_001 for the slice defaults
// for any seed (Phase 1.5: the seed drives the world and the first wreck; the home stays golden). Biome and difficulty are
// fixed to breach_field / standard (shown locked with the reason); a non-slice choice fails closed.
using System;
using System.Collections.Generic;
using System.Globalization;
using SynapticSea.Core.Procgen;
using SynapticSea.Core.Services;
using SynapticSea.Core.Systems;
using SynapticSea.Runtime.Session;
using SynapticSea.UI;
using UnityEngine.UIElements;

namespace SynapticSea.App
{
    /// <summary>
    /// PAUSED title submenu built like <see cref="MenuPanel"/> (44 px focusable rows, selector rows with ◀ value ▶): Biome
    /// and Difficulty cycle with Left/Right, Seed steps with Left/Right and opens an inline numeric field on Accept (keyboard
    /// entry; Enter commits, Back cancels), Randomize seed rolls a new seed, Start fills a <see cref="RunLaunchRequest"/>.
    /// Back closes the submenu; the modal stack restores focus to the New Run row below. Input arrives either as UI Toolkit
    /// navigation events on the focused row (gamepad / keyboard through the EventSystem) or as <see cref="Consume"/> from
    /// the title's router when nothing owns focus.
    /// </summary>
    public sealed class NewRunSetupPanel : SurfacePanel
    {
        public const string Id = "new_run_setup";
        public const string BiomesDir = "res://data/procgen/biomes";
        public const string DifficultyDir = "res://data/procgen/difficulty";
        public const string RowBiome = "biome";
        public const string RowDifficulty = "difficulty";
        public const string RowSeed = "seed";
        public const string RowRandomize = "randomize";
        public const string RowTimeScale = "time_scale";
        public const string RowStart = "start";

        /// <summary>Godot difficulty order (<c>title_main.gd</c> cycled standard → hardened → deep_dive).</summary>
        public static readonly IReadOnlyList<string> DifficultyOrder = new[] { DifficultyProfile.STANDARD_ID, "hardened", "deep_dive" };

        static readonly string[] RowIds = { RowBiome, RowDifficulty, RowSeed, RowRandomize, RowTimeScale, RowStart };

        /// <summary>Game seconds per real second the setup offers: 30 (2 real minutes per game hour), 60 (the default, 1 minute), 120 (30 seconds) and 1 ("off": real-time pacing).</summary>
        public static readonly IReadOnlyList<double> TimeScales = new[] { 30.0, WorldClock.DefaultNewRunScale, 120.0, WorldClock.DefaultScale };

        public static string TimeScaleLabel(double scale)
        {
            if (scale == WorldClock.DefaultScale) return "Off (real time)";
            double minutesPerHour = 60.0 / scale;
            return (minutesPerHour >= 1.0 ? minutesPerHour.ToString("0.#", CultureInfo.InvariantCulture) + " min" : (minutesPerHour * 60.0).ToString("0", CultureInfo.InvariantCulture) + " s") + " = 1 hour";
        }

        /// <summary>Why the biome and difficulty rows are fixed: the hardened / deep_dive home and the other biomes' homes are untested.</summary>
        public const string LockedReason = "Biome and difficulty are fixed for this build (other homes are not playtested yet)";

        /// <summary>
        /// True (default) while Milestone A fixes the biome and difficulty: those rows show their slice value, cannot be cycled and say why.
        /// Set false only to exercise the cycling mechanics (tests); the launch contract still rejects non-slice values.
        /// </summary>
        public bool BiomeDifficultyLocked { get; set; } = true;

        /// <summary>Why Start run falls back to the authored hub when pacing is off.</summary>
        public const string AuthoredHubReason = "Real-time pacing starts in the authored hub: the generated home needs scaled time to supply its food and water";

        /// <summary>
        /// True (default): Start run begins in a home generated from the seed (Phase 1.11). False boots the authored hub, which is what
        /// direct open does and what the scripted golden-hub routes use. Independent of this flag, real-time pacing ("off") always boots the
        /// authored hub, because the generated home's food and water guarantee is computed for scaled time.
        /// </summary>
        public bool GeneratedHome { get; set; } = true;

        /// <summary>Whether Start run would begin in a generated home with the current choices.</summary>
        public bool StartsInGeneratedHome => GeneratedHome && TimeScale > WorldClock.DefaultScale;

        /// <summary>Random seed source (tests pin it).</summary>
        public static Func<long> RandomSeed = () => UnityEngine.Random.Range((int)MilestoneALaunch.MinSeed, int.MaxValue);

        public event Action<RunLaunchRequest> StartRequested;
        public event Action BackRequested;

        readonly List<string> _biomes;
        readonly List<string> _difficulties;
        readonly List<VisualElement> _rows = new List<VisualElement>();
        readonly TextField _seedField;
        int _biomeIndex;
        int _difficultyIndex;
        int _focusIndex;
        int _timeScaleIndex = 1;
        long _seed;

        public NewRunSetupPanel(IEnumerable<string> biomeIds, IEnumerable<string> difficultyIds, string biomeId, string difficultyId, long seed)
            : base("NEW RUN", SurfaceTime.Paused)
        {
            AddToClassList("ss-menu");
            AddToClassList("ss-new-run-setup");
            _biomes = new List<string>(biomeIds ?? Array.Empty<string>());
            _difficulties = new List<string>(difficultyIds ?? Array.Empty<string>());
            if (_difficulties.Count == 0) _difficulties.Add(DifficultyProfile.STANDARD_ID);
            _biomeIndex = Math.Max(0, _biomes.IndexOf(biomeId ?? ""));
            _difficultyIndex = Math.Max(0, _difficulties.IndexOf(difficultyId ?? ""));
            _seed = ClampSeed(seed);

            var rowsBox = UiFactory.Box("ss-menu__rows");
            Body.Add(rowsBox);
            foreach (string id in RowIds)
            {
                VisualElement row = BuildRow(id);
                _rows.Add(row);
                rowsBox.Add(row);
            }
            _seedField = new TextField { name = "new-run-seed-field", maxLength = 18, isDelayed = false };
            _seedField.AddToClassList("ss-new-run-setup__seed-field");
            _seedField.RegisterCallback<KeyDownEvent>(OnSeedFieldKey, TrickleDown.TrickleDown);
            _seedField.RegisterCallback<FocusOutEvent>(_ => CommitSeedEdit());
            UiFactory.SetShown(_seedField, false);
            RowElement(RowSeed).Add(_seedField);
            Refresh();
            RefreshNote();
            SetViewVisible(true);
        }

        static long ClampSeed(long seed) => Math.Min(MilestoneALaunch.MaxSeed, Math.Max(MilestoneALaunch.MinSeed, seed));

        public override string SurfaceId => Id;

        public IReadOnlyList<string> BiomeIds => _biomes;
        public IReadOnlyList<string> DifficultyIds => _difficulties;
        public string BiomeId => _biomes.Count == 0 ? "" : _biomes[_biomeIndex];
        public string DifficultyId => _difficulties[_difficultyIndex];
        public long Seed => _seed;
        public double TimeScale => TimeScales[_timeScaleIndex];
        public int FocusIndex => _focusIndex;
        public string FocusedRowId => RowIds[_focusIndex];
        public bool IsEditingSeed { get; private set; }
        public TextField SeedField => _seedField;

        public static string TokenFor(string rowId) => "new_run:" + rowId;

        public VisualElement RowElement(string rowId) => _rows[Array.IndexOf(RowIds, rowId)];

        /// <summary>The value text a row shows ("" for command rows).</summary>
        public string RowValue(string rowId) => RowElement(rowId).Q<Label>(className: "ss-menu-row__value").text;

        /// <summary>Biome ids from <c>data/procgen/biomes/*.json</c> (sorted).</summary>
        public static List<string> LoadBiomeIds()
        {
            var ids = new List<string>();
            foreach (string file in ListData(BiomesDir))
            {
                if (ResPath.GetExtension(file) == "json") ids.Add(ResPath.GetBasename(file));
            }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        /// <summary>Difficulty ids from <c>data/procgen/difficulty/*.json</c>: Godot's order first, then any others sorted.</summary>
        public static List<string> LoadDifficultyIds()
        {
            var present = new List<string>();
            foreach (string file in ListData(DifficultyDir))
            {
                if (ResPath.GetExtension(file) == "json") present.Add(ResPath.GetBasename(file));
            }
            present.Sort(StringComparer.Ordinal);
            var ids = new List<string>();
            foreach (string id in DifficultyOrder)
            {
                if (present.Contains(id)) ids.Add(id);
            }
            foreach (string id in present)
            {
                if (!ids.Contains(id)) ids.Add(id);
            }
            if (ids.Count == 0) ids.AddRange(DifficultyOrder);
            return ids;
        }

        static IReadOnlyList<string> ListData(string dir) =>
            CoreServices.Resources is FileSystemResourceReader reader ? reader.ListFiles(dir) : (IReadOnlyList<string>)Array.Empty<string>();

        /// <summary>The request this setup describes (class and settings are filled by the title).</summary>
        public RunLaunchRequest BuildRequest() => StartsInGeneratedHome
            ? RunLaunchRequest.GeneratedHomeRun(_seed, BiomeId, DifficultyId, TimeScale)
            : RunLaunchRequest.NewRun(_seed, BiomeId, DifficultyId, TimeScale);

        /// <summary>The note under the rows: the locked-rows reason, or why real-time pacing starts in the authored hub.</summary>
        void RefreshNote()
        {
            if (GeneratedHome && TimeScale <= WorldClock.DefaultScale) StatusText.Set(AuthoredHubReason, Severity.Caution);
            else StatusText.Set(LockedReason, Severity.Info);
        }

        /// <summary>Shows a start failure (for example, no viable generated home for the seed) under the rows; the seed can be rerolled with Randomize seed.</summary>
        public void ShowStartFailure(string message) => StatusText.Set(message, Severity.Caution);

        // ------------------------------------------------------------------ commands

        public override bool Consume(UiCommand command)
        {
            if (IsEditingSeed)
            {
                if (command == UiCommand.Cancel) CancelSeedEdit();
                else if (command == UiCommand.Accept) CommitSeedEdit();
                return true;
            }
            return base.Consume(command);
        }

        protected override bool OnCommand(UiCommand command)
        {
            switch (command)
            {
                case UiCommand.Up:
                    MoveFocus(-1);
                    return true;
                case UiCommand.Down:
                    MoveFocus(1);
                    return true;
                case UiCommand.Left:
                    Cycle(-1);
                    return true;
                case UiCommand.Right:
                    Cycle(1);
                    return true;
                case UiCommand.Accept:
                    Activate();
                    return true;
            }
            return false;
        }

        protected override void RequestClose() => BackRequested?.Invoke();

        protected override VisualElement InitialFocusElement() => _rows[_focusIndex];

        public override void RestoreFocus(string token)
        {
            int index = Array.IndexOf(RowIds, (token ?? "").StartsWith("new_run:", StringComparison.Ordinal) ? token.Substring(8) : "");
            if (index >= 0) _focusIndex = index;
            Refresh();
            base.RestoreFocus(TokenFor(RowIds[_focusIndex]));
        }

        void MoveFocus(int delta)
        {
            _focusIndex = (_focusIndex + delta + _rows.Count) % _rows.Count;
            Refresh();
            UiFocus.Focus(_rows[_focusIndex]);
        }

        /// <summary>Left/Right on the focused row: biome / difficulty wrap around, seed steps by one.</summary>
        public void Cycle(int direction)
        {
            switch (FocusedRowId)
            {
                case RowBiome:
                    if (BiomeDifficultyLocked) return;
                    if (_biomes.Count > 0) _biomeIndex = (_biomeIndex + direction + _biomes.Count) % _biomes.Count;
                    break;
                case RowDifficulty:
                    if (BiomeDifficultyLocked) return;
                    _difficultyIndex = (_difficultyIndex + direction + _difficulties.Count) % _difficulties.Count;
                    break;
                case RowSeed:
                    _seed = ClampSeed(_seed + direction);
                    break;
                case RowTimeScale:
                    _timeScaleIndex = (_timeScaleIndex + direction + TimeScales.Count) % TimeScales.Count;
                    break;
                default:
                    return;
            }
            Refresh();
            RefreshNote();
        }

        /// <summary>Accept on the focused row.</summary>
        public void Activate()
        {
            switch (FocusedRowId)
            {
                case RowBiome:
                case RowDifficulty:
                case RowTimeScale:
                    Cycle(1);
                    break;
                case RowSeed:
                    BeginSeedEdit();
                    break;
                case RowRandomize:
                    SetSeed(RandomSeed());
                    break;
                case RowStart:
                    RequestStart();
                    break;
            }
        }

        /// <summary>Start run with the current choices (what accepting the Start row does; also used by the dev-only smoke automation).</summary>
        public void RequestStart() => StartRequested?.Invoke(BuildRequest());

        public void SetSeed(long seed)
        {
            _seed = ClampSeed(seed);
            Refresh();
            RefreshNote();
        }

        public void FocusRow(string rowId)
        {
            int index = Array.IndexOf(RowIds, rowId);
            if (index < 0) return;
            _focusIndex = index;
            Refresh();
            UiFocus.Focus(_rows[_focusIndex]);
        }

        // ------------------------------------------------------------------ seed entry

        public void BeginSeedEdit()
        {
            _focusIndex = Array.IndexOf(RowIds, RowSeed);
            IsEditingSeed = true;
            _seedField.SetValueWithoutNotify(_seed.ToString(CultureInfo.InvariantCulture));
            UiFactory.SetShown(_seedField, true);
            Refresh();
            _seedField.Focus();
        }

        /// <summary>Commits the typed seed: digits only (non-digits are dropped); an empty entry keeps the old seed.</summary>
        public void CommitSeedEdit()
        {
            if (!IsEditingSeed) return;
            string digits = "";
            foreach (char c in _seedField.value ?? "")
            {
                if (c >= '0' && c <= '9') digits += c;
            }
            if (digits.Length > 0 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed <= MilestoneALaunch.MaxSeed) _seed = ClampSeed(parsed);
            else if (digits.Length > 0) StatusText.Set("Seed is too large (1 to " + MilestoneALaunch.MaxSeed.ToString(CultureInfo.InvariantCulture) + ")", Severity.Caution);
            EndSeedEdit();
        }

        public void CancelSeedEdit()
        {
            if (!IsEditingSeed) return;
            EndSeedEdit();
        }

        void EndSeedEdit()
        {
            IsEditingSeed = false;
            UiFactory.SetShown(_seedField, false);
            Refresh();
            UiFocus.Focus(RowElement(RowSeed));
        }

        void OnSeedFieldKey(KeyDownEvent e)
        {
            if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter)
            {
                UiFocus.Consume(e, this);
                CommitSeedEdit();
            }
            else if (e.keyCode == UnityEngine.KeyCode.Escape)
            {
                UiFocus.Consume(e, this);
                CancelSeedEdit();
            }
        }

        // ------------------------------------------------------------------ view

        VisualElement BuildRow(string id)
        {
            var row = new VisualElement();
            row.AddToClassList(UiClasses.Row);
            row.AddToClassList(UiClasses.Focusable);
            row.AddToClassList("ss-menu-row");
            row.focusable = true;
            row.tabIndex = 0;
            UiFocus.Tag(row, TokenFor(id));
            var prev = UiFactory.Text("◀", "ss-menu-row__cycle");
            var label = UiFactory.Text(LabelFor(id), "ss-menu-row__label");
            var value = UiFactory.Text("", "ss-menu-row__value", UiClasses.LabelMono);
            var next = UiFactory.Text("▶", "ss-menu-row__cycle");
            row.Add(label);
            row.Add(prev);
            row.Add(value);
            row.Add(next);
            prev.RegisterCallback<ClickEvent>(e =>
            {
                e.StopPropagation();
                FocusRow(id);
                Cycle(-1);
            });
            next.RegisterCallback<ClickEvent>(e =>
            {
                e.StopPropagation();
                FocusRow(id);
                Cycle(1);
            });
            row.RegisterCallback<ClickEvent>(e =>
            {
                if (IsEditingSeed) return;
                FocusRow(id);
                Activate();
            });
            row.RegisterCallback<NavigationSubmitEvent>(e =>
            {
                if (IsEditingSeed) return;
                UiFocus.Consume(e, this);
                FocusRow(id);
                Activate();
            });
            row.RegisterCallback<NavigationMoveEvent>(e =>
            {
                if (IsEditingSeed) return;
                UiFocus.Consume(e, this);
                switch (e.direction)
                {
                    case NavigationMoveEvent.Direction.Up: MoveFocus(-1); break;
                    case NavigationMoveEvent.Direction.Down: MoveFocus(1); break;
                    case NavigationMoveEvent.Direction.Left: Cycle(-1); break;
                    case NavigationMoveEvent.Direction.Right: Cycle(1); break;
                }
                UiFocus.Focus(_rows[_focusIndex]);
            });
            row.RegisterCallback<FocusInEvent>(e =>
            {
                int index = _rows.IndexOf(row);
                if (index >= 0 && index != _focusIndex)
                {
                    _focusIndex = index;
                    Refresh();
                }
            });
            return row;
        }

        static string LabelFor(string id)
        {
            switch (id)
            {
                case RowBiome: return "Biome";
                case RowDifficulty: return "Difficulty";
                case RowSeed: return "Seed";
                case RowRandomize: return "Randomize seed";
                case RowTimeScale: return "Time scale";
                default: return "Start run";
            }
        }

        void Refresh()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                string id = RowIds[i];
                VisualElement row = _rows[i];
                bool locked = BiomeDifficultyLocked && (id == RowBiome || id == RowDifficulty);
                bool cyclable = !locked && (id == RowBiome || id == RowDifficulty || id == RowTimeScale || (id == RowSeed && !IsEditingSeed));
                row.Q<Label>(className: "ss-menu-row__label").text = LabelFor(id) + (locked ? " (fixed)" : "");
                string value = id == RowBiome ? BiomeId : id == RowDifficulty ? DifficultyId : id == RowSeed ? _seed.ToString(CultureInfo.InvariantCulture) : id == RowTimeScale ? TimeScaleLabel(TimeScale) : "";
                var valueLabel = row.Q<Label>(className: "ss-menu-row__value");
                valueLabel.text = value;
                UiFactory.SetShown(valueLabel, value.Length != 0 && !(id == RowSeed && IsEditingSeed));
                foreach (Label cycle in row.Query<Label>(className: "ss-menu-row__cycle").ToList()) UiFactory.SetShown(cycle, cyclable);
                row.EnableInClassList(UiClasses.RowSelected, i == _focusIndex);
            }
            RememberFocus(TokenFor(RowIds[_focusIndex]));
        }
    }
}
